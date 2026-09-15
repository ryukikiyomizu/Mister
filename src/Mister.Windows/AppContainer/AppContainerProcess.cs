using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;

namespace Mister.Windows.AppContainer;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

[SupportedOSPlatform("windows")]
public sealed class AppContainerProcess : IDisposable
{
    private const int MaxCapturedBytes = 64 * 1024;
    private SafeFileHandle readProcess;
    private SafeWaitHandle waitProcess;
    private IntPtr thread;
    private readonly IKillOnCloseJob job;
    private readonly Task<string> stdout;
    private readonly Task<string> stderr;
    private int resumed;
    private int terminated;
    private int disposed;

    private AppContainerProcess(SafeFileHandle readProcess, SafeWaitHandle waitProcess, IntPtr thread, IKillOnCloseJob job, Task<string> stdout, Task<string> stderr)
    { this.readProcess = readProcess; this.waitProcess = waitProcess; this.thread = thread; this.job = job; this.stdout = stdout; this.stderr = stderr; }

    public static AppContainerProcess StartSuspended(string executable, string workingDirectory, AppContainerProfile profile, IReadOnlyCollection<string> capabilities, string? arguments = null)
        => StartSuspended(executable, workingDirectory, profile, capabilities, arguments, KillOnCloseJobFactory.Instance);

    internal static AppContainerProcess StartSuspended(string executable, string workingDirectory, AppContainerProfile profile, IReadOnlyCollection<string> capabilities, string? arguments, IKillOnCloseJobFactory jobFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(jobFactory);
        if (capabilities.Count != 0) throw new ChildNetworkDenialFailure("The no-network launcher accepts zero capabilities only.");
        if (!File.Exists(executable)) throw new ChildNetworkDenialFailure("The staged executable is missing.");

        IntPtr attributes = IntPtr.Zero, securityCaps = IntPtr.Zero, outRead = IntPtr.Zero, outWrite = IntPtr.Zero, errRead = IntPtr.Zero, errWrite = IntPtr.Zero;
        NativeMethods.ProcessInformation pi = default;
        try
        {
            var sa = new NativeMethods.SecurityAttributes { Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(), InheritHandle = true };
            if (!NativeMethods.CreatePipe(out outRead, out outWrite, ref sa, 0) || !NativeMethods.CreatePipe(out errRead, out errWrite, ref sa, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NativeMethods.SetHandleInformation(outRead, NativeMethods.HandleFlagInherit, 0) || !NativeMethods.SetHandleInformation(errRead, NativeMethods.HandleFlagInherit, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr attributeBytes = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            attributes = Marshal.AllocHGlobal(attributeBytes);
            if (!NativeMethods.InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeBytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var caps = new NativeMethods.SecurityCapabilities { AppContainerSid = profile.Sid, Capabilities = IntPtr.Zero, CapabilityCount = 0, Reserved = 0 };
            securityCaps = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SecurityCapabilities>());
            Marshal.StructureToPtr(caps, securityCaps, false);
            if (!NativeMethods.UpdateProcThreadAttribute(attributes, 0, (IntPtr)NativeMethods.ProcThreadAttributeSecurityCapabilities, securityCaps, (IntPtr)Marshal.SizeOf<NativeMethods.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new NativeMethods.StartupInfoEx { AttributeList = attributes, StartupInfo = new() { cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>(), hStdInput = IntPtr.Zero, hStdOutput = outWrite, hStdError = errWrite, dwFlags = 0x100 } };
            string commandLine = Quote(executable) + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments);
            if (!NativeMethods.CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, NativeMethods.ExtendedStartupinfoPresent | NativeMethods.CreateSuspended | NativeMethods.CreateNoWindow, IntPtr.Zero, workingDirectory, ref startup, out pi)) throw new Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.CloseHandle(outWrite); outWrite = IntPtr.Zero; NativeMethods.CloseHandle(errWrite); errWrite = IntPtr.Zero;
            IKillOnCloseJob job = jobFactory.Create();
            try { job.Assign(pi.hProcess); }
            catch { job.Dispose(); throw; }
            SafeFileHandle? readProcess = null;
            SafeWaitHandle? waitProcess = null;
            try
            {
                readProcess = DuplicateFileHandle(
                    pi.hProcess,
                    NativeMethods.ProcessVmRead
                        | NativeMethods.ProcessQueryInformation);
                waitProcess = DuplicateWaitHandle(
                    pi.hProcess,
                    NativeMethods.Synchronize
                        | NativeMethods.ProcessQueryLimitedInformation);
            }
            catch (Exception duplicationFailure)
            {
                readProcess?.Dispose();
                waitProcess?.Dispose();
                Exception? terminationFailure = null;
                try { job.Terminate(); }
                catch (Exception exception) { terminationFailure = exception; }
                finally { job.Dispose(); }
                if (terminationFailure is not null)
                    throw new AggregateException(
                        "Reduced process-handle creation and child termination both failed.",
                        duplicationFailure,
                        terminationFailure);
                throw;
            }
            if (!NativeMethods.CloseHandle(pi.hProcess))
            {
                var closeFailure = new Win32Exception(
                    Marshal.GetLastWin32Error());
                readProcess.Dispose();
                waitProcess.Dispose();
                Exception? terminationFailure = null;
                try { job.Terminate(); }
                catch (Exception exception) { terminationFailure = exception; }
                finally { job.Dispose(); }
                if (terminationFailure is not null)
                    throw new AggregateException(
                        "Closing the unrestricted process handle and terminating its job both failed.",
                        closeFailure,
                        terminationFailure);
                throw closeFailure;
            }
            pi.hProcess = IntPtr.Zero;
            var result = new AppContainerProcess(readProcess, waitProcess, pi.hThread, job, ReadBoundedAsync(outRead), ReadBoundedAsync(errRead));
            pi = default; outRead = IntPtr.Zero; errRead = IntPtr.Zero;
            return result;
        }
        catch (Exception ex)
        {
            TerminateUnownedProcess(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) NativeMethods.CloseHandle(pi.hThread);
            if (ex is ChildNetworkDenialFailure) throw;
            throw new ChildNetworkDenialFailure("Unable to construct a zero-capability suspended child.", ex);
        }
        finally
        {
            if (outRead != IntPtr.Zero) NativeMethods.CloseHandle(outRead); if (outWrite != IntPtr.Zero) NativeMethods.CloseHandle(outWrite);
            if (errRead != IntPtr.Zero) NativeMethods.CloseHandle(errRead); if (errWrite != IntPtr.Zero) NativeMethods.CloseHandle(errWrite);
            if (attributes != IntPtr.Zero) { NativeMethods.DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (securityCaps != IntPtr.Zero) Marshal.FreeHGlobal(securityCaps);
        }
    }

    public void Resume()
    {
        if (Interlocked.Exchange(ref resumed, 1) != 0) throw new InvalidOperationException("The child can only be resumed once.");
        IntPtr suspendedThread = Interlocked.Exchange(ref thread, IntPtr.Zero);
        try
        {
            if (NativeMethods.ResumeThread(suspendedThread) == uint.MaxValue) throw new ChildNetworkDenialFailure("Unable to resume job-assigned child.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally
        {
            if (suspendedThread != IntPtr.Zero) NativeMethods.CloseHandle(suspendedThread);
        }
    }

    public async Task<ProcessResult> WaitForExitAsync(TimeSpan timeout)
    {
        if (Volatile.Read(ref resumed) == 0) throw new InvalidOperationException("A suspended child must be explicitly resumed.");
        uint wait = timeout <= TimeSpan.Zero ? 0U : (uint)Math.Min(timeout.TotalMilliseconds, uint.MaxValue - 1);
        IntPtr waitHandle = waitProcess.DangerousGetHandle();
        uint result = NativeMethods.WaitForSingleObject(waitHandle, wait);
        if (result == NativeMethods.WaitTimeout) { TerminateAndWait(); throw new TimeoutException("AppContainer child exceeded its capture timeout."); }
        ThrowIfTerminationWaitFailed(result);
        Interlocked.Exchange(ref terminated, 1);
        if (!NativeMethods.GetExitCodeProcess(waitHandle, out uint exit)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new ProcessResult(unchecked((int)exit), await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    internal void TerminateAndWait()
    {
        if (Volatile.Read(ref terminated) != 0) return;
        job.Terminate();
        uint result = NativeMethods.WaitForSingleObject(
            waitProcess.DangerousGetHandle(),
            5000);
        ThrowIfTerminationWaitFailed(result);
        Interlocked.Exchange(ref terminated, 1);
    }

    internal SafeFileHandle CreateReadHandle()
    {
        return DuplicateFileHandle(
            readProcess.DangerousGetHandle(),
            NativeMethods.ProcessVmRead
                | NativeMethods.ProcessQueryInformation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Exception? terminationFailure = null;
        try
        {
            if (Volatile.Read(ref terminated) == 0) TerminateAndWait();
        }
        catch (Exception exception)
        {
            terminationFailure = exception;
        }
        finally
        {
            job.Dispose();
            IntPtr oldThread = Interlocked.Exchange(ref thread, IntPtr.Zero); if (oldThread != IntPtr.Zero) NativeMethods.CloseHandle(oldThread);
            readProcess.Dispose();
            waitProcess.Dispose();
        }
        if (terminationFailure is not null) throw terminationFailure;
    }

    internal static void ThrowIfTerminationWaitFailed(uint result)
    {
        if (result == NativeMethods.WaitObject0) return;
        Exception? inner = result == NativeMethods.WaitFailed
            ? new Win32Exception(Marshal.GetLastWin32Error())
            : null;
        throw new ChildNetworkDenialFailure(
            result == NativeMethods.WaitTimeout
                ? "The AppContainer capture job did not terminate within five seconds."
                : $"Waiting for AppContainer termination failed with result {result}.",
            inner);
    }

    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var commandLine = new System.Text.StringBuilder(value.Length + 2).Append('"');
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') commandLine.Append('\\', (slashes * 2) + 1);
            else commandLine.Append('\\', slashes);
            commandLine.Append(character);
            slashes = 0;
        }
        commandLine.Append('\\', slashes * 2).Append('"');
        return commandLine.ToString();
    }

    private static void TerminateUnownedProcess(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero) return;
        try
        {
            NativeMethods.TerminateProcess(processHandle, 1);
            NativeMethods.WaitForSingleObject(processHandle, 5000);
        }
        finally { NativeMethods.CloseHandle(processHandle); }
    }
    private static SafeFileHandle DuplicateFileHandle(IntPtr source, uint access)
    {
        IntPtr current = NativeMethods.GetCurrentProcess();
        if (!NativeMethods.DuplicateHandle(current, source, current, out IntPtr duplicate, access, false, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SafeFileHandle(duplicate, ownsHandle: true);
    }
    private static SafeWaitHandle DuplicateWaitHandle(IntPtr source, uint access)
    {
        IntPtr current = NativeMethods.GetCurrentProcess();
        if (!NativeMethods.DuplicateHandle(current, source, current, out IntPtr duplicate, access, false, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new SafeWaitHandle(duplicate, ownsHandle: true);
    }
    private static Task<string> ReadBoundedAsync(IntPtr handle) => Task.Run(async () =>
    {
        using var safe = new SafeFileHandle(handle, ownsHandle: true);
        // CreatePipe creates synchronous handles; this Task.Run reader keeps their blocking reads off the caller thread.
        using var stream = new FileStream(safe, FileAccess.Read, 4096, isAsync: false);
        byte[] buffer = new byte[4096]; using var output = new MemoryStream();
        while (output.Length < MaxCapturedBytes)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxCapturedBytes - (int)output.Length))).ConfigureAwait(false);
            if (read == 0) break; await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    });
}
