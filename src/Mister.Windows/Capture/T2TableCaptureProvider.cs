using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Diagnostics;
using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Formats;
using Mister.Core.T2;
using Mister.Windows.AppContainer;
using Microsoft.Win32.SafeHandles;

namespace Mister.Windows.Capture;

internal sealed record CapturedT2Tables(
    uint[] SubtractTable,
    byte[] RecordXorTable);

internal interface IAppContainerNetworkIsolationProof
{
    ValueTask<ToolResult<bool>> ProbeAsync(
        string workRoot,
        CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
public sealed class T2TableCaptureProvider
{
    private const ulong SubtractTableRva = 0x3C39E8;
    private const ulong RecordXorTableRva = 0x3698A0;
    private readonly ProcessMemoryReader memory;
    private readonly IAppContainerNetworkIsolationProof?
        networkIsolationProof;
    public T2TableCaptureProvider()
        : this(new ProcessMemoryReader(), null)
    {
    }
    internal T2TableCaptureProvider(ProcessMemoryReader memory)
        : this(memory, null)
    {
    }
    internal T2TableCaptureProvider(
        ProcessMemoryReader memory,
        IAppContainerNetworkIsolationProof? networkIsolationProof)
    {
        this.memory = memory;
        this.networkIsolationProof = networkIsolationProof;
    }

    public async ValueTask<ToolResult<T2CaptureBundle>> CaptureAsync(string clientPath, string validationPakPath, string workRoot, CancellationToken cancellationToken)
    {
        string client = clientPath;
        string pak = validationPakPath;
        string? stage = null;
        AppContainerProfile? profile = null;
        AppContainerProcess? child = null;
        bool childStopped = false;
        ToolResult<T2CaptureBundle> outcome;
        try
        {
            client = Path.GetFullPath(clientPath);
            pak = Path.GetFullPath(validationPakPath);
            string root = Path.GetDirectoryName(client)!;
            string work = Path.GetFullPath(workRoot);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(client) || !Path.GetFileName(client).Equals("CLIENT.EXE", StringComparison.OrdinalIgnoreCase))
            {
                outcome = Failure(DiagnosticCode.UnsupportedClient, "The selected T2 CLIENT.EXE does not exist.", client);
                return await CompleteCleanupAsync(outcome, child, profile, stage);
            }
            if (!File.Exists(pak))
            {
                outcome = Failure(DiagnosticCode.UnknownPakFormat, "The selected validation PAK does not exist.", pak);
                return await CompleteCleanupAsync(outcome, child, profile, stage);
            }
            if (networkIsolationProof is null)
            {
                outcome = Failure(
                    DiagnosticCode.AppContainerNetworkIsolationUnavailable,
                    "AppContainer capture is typed-disabled until the final single executable supplies its internal trusted network-isolation proof.",
                    client);
                return await CompleteCleanupAsync(
                    outcome,
                    child,
                    profile,
                    stage);
            }
            ToolResult<bool> isolation =
                await networkIsolationProof.ProbeAsync(
                    work,
                    cancellationToken);
            if (!isolation.IsSuccess)
            {
                outcome = ToolResult<T2CaptureBundle>.Failure(
                    isolation.Error! with { Subject = client });
                return await CompleteCleanupAsync(
                    outcome,
                    child,
                    profile,
                    stage);
            }
            stage = await T2ClientStager.StageAsync(root, work, cancellationToken);
            profile = AppContainerProfile.CreateUnique();
            profile.GrantReadExecute(stage);
            child = AppContainerProcess.StartSuspended(Path.Combine(stage, "CLIENT.EXE"), stage, profile, Array.Empty<string>(), "127.0.0.1:8012");
            var captureClock = Stopwatch.StartNew();
            child.Resume();
            using SafeFileHandle readHandle = child.CreateReadHandle();
            ulong? imageBase = null;
            TimeSpan remaining = TimeSpan.FromSeconds(10) - captureClock.Elapsed;
            ToolResult<CapturedT2Tables> captured =
                await PollForValidatedSnapshotAsync(
                    token =>
                    {
                        return new ValueTask<CapturedT2Tables>(Task.Run(
                            () =>
                            {
                                bool addedReference = false;
                                try
                                {
                                    readHandle.DangerousAddRef(
                                        ref addedReference);
                                    token.ThrowIfCancellationRequested();
                                    IntPtr retainedProcess =
                                        readHandle.DangerousGetHandle();
                                    imageBase ??= memory.GetImageBase(
                                        retainedProcess);
                                    token.ThrowIfCancellationRequested();
                                    byte[] subtractBytes = memory.ReadExact(
                                        retainedProcess,
                                        checked(
                                            imageBase.Value
                                            + SubtractTableRva),
                                        256 * sizeof(uint));
                                    token.ThrowIfCancellationRequested();
                                    uint[] subtract = new uint[256];
                                    Buffer.BlockCopy(
                                        subtractBytes,
                                        0,
                                        subtract,
                                        0,
                                        subtractBytes.Length);
                                    byte[] xor = memory.ReadExact(
                                        retainedProcess,
                                        checked(
                                            imageBase.Value
                                            + RecordXorTableRva),
                                        256);
                                    return new CapturedT2Tables(
                                        subtract,
                                        xor);
                                }
                                finally
                                {
                                    if (addedReference)
                                    {
                                        readHandle.DangerousRelease();
                                    }
                                }
                            },
                            token));
                    },
                    (tables, token) => ValidateSnapshotAsync(
                        pak,
                        tables.SubtractTable,
                        tables.RecordXorTable,
                        token),
                    remaining,
                    cancellationToken);
            if (!captured.IsSuccess)
            {
                outcome = ToolResult<T2CaptureBundle>.Failure(
                    captured.Error! with { Subject = client });
            }
            else
            {
                child.TerminateAndWait();
                childStopped = true;
                CapturedT2Tables tables = captured.Value!;
                var draft = new T2CaptureBundle(
                    1,
                    await Sha256Async(client, cancellationToken),
                    tables.SubtractTable,
                    tables.RecordXorTable,
                    DateTimeOffset.UtcNow,
                    await Sha256Async(pak, cancellationToken),
                    string.Empty);
                outcome = ToolResult<T2CaptureBundle>.Success(
                    T2CaptureBundleSerializer.Seal(draft));
            }
        }
        catch (OperationCanceledException)
        {
            outcome = Failure(DiagnosticCode.Cancelled, "T2 client table capture was cancelled.", client);
        }
        catch (ChildNetworkDenialFailure exception)
        {
            outcome = Failure(DiagnosticCode.ChildNetworkDenialFailure, $"{exception.Message} {exception.InnerException?.Message}".Trim(), client);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        {
            outcome = Failure(DiagnosticCode.UnsupportedClient, $"T2 client table capture failed safely: {exception.Message}", client);
        }
        return await CompleteCleanupAsync(
            outcome,
            child,
            profile,
            stage,
            childStopped);
    }

    internal static async ValueTask<ToolResult<CapturedT2Tables>>
        PollForValidatedSnapshotAsync(
            Func<CancellationToken, ValueTask<CapturedT2Tables>> snapshot,
            Func<CapturedT2Tables, CancellationToken, ValueTask<ToolResult<bool>>> validate,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(validate);
        if (timeout <= TimeSpan.Zero)
        {
            return PollFailure(
                DiagnosticCode.CaptureTimeout,
                "The staged T2 client exceeded its 10-second capture deadline.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                CapturedT2Tables tables;
                try
                {
                    tables = await snapshot(deadline.Token)
                        .AsTask()
                        .WaitAsync(deadline.Token);
                }
                catch (IOException)
                {
                    await Task.Delay(100, deadline.Token);
                    continue;
                }

                ToolResult<bool> validation = await validate(
                        tables,
                        deadline.Token)
                    .AsTask()
                    .WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if (validation.IsSuccess) return ToolResult<CapturedT2Tables>.Success(tables);
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return PollFailure(
                DiagnosticCode.CaptureTimeout,
                "The staged T2 client did not expose a structurally valid table pair within 10 seconds.");
        }
        catch (OperationCanceledException)
        {
            return PollFailure(
                DiagnosticCode.Cancelled,
                "T2 client table capture was cancelled.");
        }
    }

    internal static async ValueTask<ToolResult<bool>> ValidateSnapshotAsync(string pak, uint[] subtract, byte[] xor, CancellationToken cancellationToken)
    {
        var evidence = new ClientEvidence(GameFamily.Technika2, "live staged CLIENT.EXE", new string('0', 64), xor, subtract, 0, ClientEvidenceValidationStatus.Unvalidated);
        ToolResult<ArchiveIndex> index = await new T2ArchiveReader(evidence).IndexAsync(pak, cancellationToken);
        return index.IsSuccess
            ? ToolResult<bool>.Success(true)
            : ToolResult<bool>.Failure(index.Error!);
    }

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    private static ToolResult<T2CaptureBundle> Failure(DiagnosticCode code, string message, string subject) =>
        ToolResult<T2CaptureBundle>.Failure(new ToolDiagnostic(code, message, subject));

    internal static ToolResult<T2CaptureBundle> MergeCleanupFailure(
        ToolResult<T2CaptureBundle> outcome,
        ToolDiagnostic cleanup)
    {
        if (outcome.IsSuccess)
        {
            return ToolResult<T2CaptureBundle>.Failure(cleanup);
        }
        return ToolResult<T2CaptureBundle>.Failure(
            outcome.Error! with
            {
                Message =
                    $"{outcome.Error.Message} Cleanup also failed: {cleanup.Message}"
            });
    }

    private static async ValueTask<ToolResult<T2CaptureBundle>>
        CompleteCleanupAsync(
            ToolResult<T2CaptureBundle> outcome,
            AppContainerProcess? child,
            AppContainerProfile? profile,
            string? stage,
            bool childStopped = false)
    {
        ToolDiagnostic? cleanupFailure = null;
        if (child is not null)
        {
            if (!childStopped)
            {
                try
                {
                    child.TerminateAndWait();
                }
                catch (Exception exception)
                {
                    cleanupFailure = AppendCleanupFailure(
                        cleanupFailure,
                        DiagnosticCode.ChildNetworkDenialFailure,
                        $"Child termination failed: {exception.Message}");
                }
            }
            try
            {
                child.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = AppendCleanupFailure(
                    cleanupFailure,
                    DiagnosticCode.ChildNetworkDenialFailure,
                    $"Child handle cleanup failed: {exception.Message}");
            }
        }
        if (profile is not null)
        {
            try
            {
                profile.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = AppendCleanupFailure(
                    cleanupFailure,
                    DiagnosticCode.ChildNetworkDenialFailure,
                    $"AppContainer profile cleanup failed: {exception.Message}");
            }
        }
        if (stage is not null)
        {
            try
            {
                await T2ClientStager.DeleteAsync(stage);
            }
            catch (Exception exception)
            {
                cleanupFailure = AppendCleanupFailure(
                    cleanupFailure,
                    DiagnosticCode.OutputPermissionFailure,
                    $"Stage cleanup failed: {exception.Message}");
            }
        }
        return cleanupFailure is null
            ? outcome
            : MergeCleanupFailure(outcome, cleanupFailure);
    }

    private static ToolDiagnostic AppendCleanupFailure(
        ToolDiagnostic? existing,
        DiagnosticCode code,
        string message) =>
        existing is null
            ? new ToolDiagnostic(code, message)
            : existing with { Message = $"{existing.Message} {message}" };

    private static ToolResult<CapturedT2Tables> PollFailure(
        DiagnosticCode code,
        string message) =>
        ToolResult<CapturedT2Tables>.Failure(
            new ToolDiagnostic(code, message));
}
