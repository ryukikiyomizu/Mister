using System.Diagnostics;
using System.Text;

namespace Mister.Windows.Sandbox;

internal interface IWindowsSandboxCli
{
    Task<SandboxCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed record SandboxCliResult(
    int ExitCode,
    string StdOut,
    string StdErr);

internal sealed class WindowsSandboxCli : IWindowsSandboxCli
{
    private readonly string executable;

    public WindowsSandboxCli(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        this.executable = Path.GetFullPath(executable);
    }

    public async Task<SandboxCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(
                "Windows Sandbox CLI did not return a process.");
        Task<string> standardOutput =
            process.StandardOutput.ReadToEndAsync();
        Task<string> standardError =
            process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException exception)
        {
            Exception? cleanupFailure = await KillAndWaitAsync(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new SandboxCliCancellationException(
                    cleanupFailure is null
                        ? "Windows Sandbox CLI was cancelled and its exact process was terminated."
                        : $"Windows Sandbox CLI was cancelled, but its exact process cleanup failed: {cleanupFailure.Message}",
                    cancellationToken,
                    cleanupFailure ?? exception);
            }
            throw new TimeoutException(
                cleanupFailure is null
                    ? $"Windows Sandbox CLI exceeded its {timeout.TotalSeconds:0}-second command deadline and was terminated."
                    : $"Windows Sandbox CLI exceeded its {timeout.TotalSeconds:0}-second command deadline, and cleanup failed: {cleanupFailure.Message}",
                cleanupFailure ?? exception);
        }

        string stdout = await standardOutput;
        string stderr = await standardError;
        if (Encoding.UTF8.GetByteCount(stdout)
                > SandboxWorker.MaxStatusBytes
            || Encoding.UTF8.GetByteCount(stderr)
                > SandboxWorker.MaxStatusBytes)
        {
            throw new InvalidDataException(
                "Windows Sandbox CLI output exceeded its size limit.");
        }
        return new SandboxCliResult(
            process.ExitCode,
            stdout,
            stderr);
    }

    private static async Task<Exception?> KillAndWaitAsync(
        Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            using var cleanupDeadline =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanupDeadline.Token);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}

internal sealed class SandboxCliCancellationException
    : OperationCanceledException
{
    public SandboxCliCancellationException(
        string message,
        CancellationToken cancellationToken,
        Exception innerException)
        : base(message, innerException, cancellationToken)
    {
    }
}
