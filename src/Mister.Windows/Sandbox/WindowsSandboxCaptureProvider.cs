using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;
using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.T2;
using Mister.Windows.Capture;

namespace Mister.Windows.Sandbox;

[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxCaptureProvider
{
    private const int MaxWorkerBytes = 256 * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private static readonly SemaphoreSlim SandboxGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };
    private readonly string workerExecutable;
    private readonly IWindowsSandboxCli cli;
    private readonly Func<bool> sandboxAvailable;

    public WindowsSandboxCaptureProvider()
        : this(
            Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The current executable path is unavailable."))
    {
    }

    public WindowsSandboxCaptureProvider(string workerExecutable)
        : this(
            workerExecutable,
            new WindowsSandboxCli(
                WindowsSandboxCapability.CliExecutablePath),
            static () => WindowsSandboxCapability.IsAvailable)
    {
    }

    internal WindowsSandboxCaptureProvider(
        string workerExecutable,
        IWindowsSandboxCli cli,
        Func<bool> sandboxAvailable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerExecutable);
        this.workerExecutable = Path.GetFullPath(workerExecutable);
        this.cli = cli
            ?? throw new ArgumentNullException(nameof(cli));
        this.sandboxAvailable = sandboxAvailable
            ?? throw new ArgumentNullException(nameof(sandboxAvailable));
    }

    public async ValueTask<ToolResult<T2CaptureBundle>> CaptureAsync(
        string clientPath,
        string validationPakPath,
        string workRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(validationPakPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        string client = Path.GetFullPath(clientPath);
        string pak = Path.GetFullPath(validationPakPath);
        string work = Path.GetFullPath(workRoot);
        string? stage = null;
        string? session = null;
        string? sandboxId = null;
        bool gateHeld = false;
        byte[] sessionKey = [];
        var guards = new List<IDisposable>();
        ToolDiagnostic? lifecycleCleanup = null;
        ToolResult<T2CaptureBundle> outcome = Failure(
            DiagnosticCode.ChildNetworkDenialFailure,
            "Windows Sandbox capture did not complete.",
            client);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sandboxAvailable())
            {
                return Failure(
                    DiagnosticCode.ChildNetworkDenialFailure,
                    "Windows Sandbox is not available on this host.",
                    client);
            }
            if (!File.Exists(client)
                || !Path.GetFileName(client).Equals(
                    "CLIENT.EXE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    DiagnosticCode.UnsupportedClient,
                    "The selected T2 CLIENT.EXE does not exist.",
                    client);
            }
            if (!File.Exists(pak))
            {
                return Failure(
                    DiagnosticCode.UnknownPakFormat,
                    "The selected validation PAK does not exist.",
                    pak);
            }
            if (!File.Exists(workerExecutable))
            {
                return Failure(
                    DiagnosticCode.UnsupportedClient,
                    "The self-contained sandbox worker is unavailable.",
                    workerExecutable);
            }

            SandboxPathGuard workGuard =
                SandboxPathGuard.LockDirectory(work);
            guards.Add(workGuard);
            LockedSandboxFile clientInput =
                SandboxPathGuard.OpenBoundedFile(
                    Path.GetDirectoryName(client)!,
                    client,
                    int.MaxValue);
            guards.Add(clientInput);
            LockedSandboxFile pakInput =
                SandboxPathGuard.OpenBoundedFile(
                    Path.GetDirectoryName(pak)!,
                    pak,
                    int.MaxValue);
            guards.Add(pakInput);
            LockedSandboxFile workerInput =
                SandboxPathGuard.OpenBoundedFile(
                    Path.GetDirectoryName(workerExecutable)!,
                    workerExecutable,
                    MaxWorkerBytes);
            guards.Add(workerInput);

            string clientHash = await Sha256Async(
                clientInput.Handle,
                cancellationToken);
            string pakHash = await Sha256Async(
                pakInput.Handle,
                cancellationToken);

            await SandboxGate.WaitAsync(cancellationToken);
            gateHeld = true;
            stage = await T2ClientStager.StageAsync(
                Path.GetDirectoryName(client)!,
                work,
                cancellationToken);
            SandboxPathGuard stageGuard =
                SandboxPathGuard.LockTree(stage);
            guards.Add(stageGuard);

            session = Path.Combine(
                work,
                $"t2-sandbox-{Guid.NewGuid():N}");
            Directory.CreateDirectory(session);
            SandboxPathGuard sessionGuard =
                SandboxPathGuard.LockDirectory(session);
            guards.Add(sessionGuard);
            string job = Path.Combine(session, "job");
            string result = Path.Combine(session, "result");
            Directory.CreateDirectory(job);
            SandboxPathGuard jobDirectoryGuard =
                SandboxPathGuard.LockDirectory(job);
            guards.Add(jobDirectoryGuard);
            Directory.CreateDirectory(result);
            SandboxPathGuard resultGuard =
                SandboxPathGuard.LockDirectory(result);
            guards.Add(resultGuard);

            const string guestWorkerName =
                "Mister.SandboxWorker.exe";
            await CopyFromHandleAsync(
                workerInput.Handle,
                Path.Combine(job, guestWorkerName),
                MaxWorkerBytes,
                cancellationToken);
            await CopyFromHandleAsync(
                pakInput.Handle,
                Path.Combine(job, "Config.pak"),
                int.MaxValue,
                cancellationToken);

            string sessionId = Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(
                        SandboxSessionProtocol.SessionIdBytes))
                .ToLowerInvariant();
            sessionKey = RandomNumberGenerator.GetBytes(
                SandboxSessionProtocol.SessionKeyBytes);
            var contract = new SandboxWorker.SandboxCaptureContract(
                @"C:\MisterInput\CLIENT.EXE",
                @"C:\MisterJob\Config.pak",
                @"C:\MisterResult",
                clientHash,
                pakHash,
                sessionId);
            string contractPath = Path.Combine(job, "contract.json");
            string contractJson = JsonSerializer.Serialize(
                contract,
                JsonOptions);
            if (Encoding.UTF8.GetByteCount(contractJson)
                > SandboxWorker.MaxContractBytes)
            {
                throw new InvalidDataException(
                    "The sandbox capture contract exceeded its size limit.");
            }
            await File.WriteAllTextAsync(
                contractPath,
                contractJson,
                new UTF8Encoding(false),
                cancellationToken);

            SandboxPathGuard jobGuard =
                SandboxPathGuard.LockTree(job);
            guards.Add(jobGuard);
            string configuration = CreateConfiguration(
                stage,
                job,
                result);
            string configurationPath = Path.Combine(
                session,
                "capture.wsb");
            await File.WriteAllTextAsync(
                configurationPath,
                configuration,
                new UTF8Encoding(false),
                cancellationToken);
            LockedSandboxFile configurationLock =
                SandboxPathGuard.OpenBoundedFile(
                    session,
                    configurationPath,
                    SandboxWorker.MaxContractBytes);
            guards.Add(configurationLock);

            stageGuard.VerifyUnchanged();
            jobGuard.VerifyUnchanged();
            resultGuard.VerifyUnchanged();
            sessionGuard.VerifyUnchanged();

            string command =
                "\"C:\\MisterJob\\"
                + guestWorkerName
                + "\" --sandbox-worker --contract "
                + "\"C:\\MisterJob\\contract.json\""
                + " --session-id "
                + sessionId
                + " --session-key "
                + Convert.ToBase64String(sessionKey);
            sandboxId = Guid.NewGuid().ToString();
            SandboxCliResult startResult = await cli.RunAsync(
                [
                    "start",
                    "--id",
                    sandboxId,
                    "--config",
                    configuration,
                    "--raw"
                ],
                TimeSpan.FromSeconds(60),
                cancellationToken);
            RequireCliSuccess(
                startResult,
                "Windows Sandbox CLI could not start the offline guest.");

            SandboxCliResult execResult = await cli.RunAsync(
                [
                    "exec",
                    "--id",
                    sandboxId,
                    "--command",
                    command,
                    "--run-as",
                    "System",
                    "--working-directory",
                    @"C:\MisterJob",
                    "--raw"
                ],
                TimeSpan.FromSeconds(30),
                cancellationToken);
            int guestExitCode = GuestExitCode(execResult);

            string statusPath = Path.Combine(
                result,
                SandboxWorker.StatusFileName);
            if ((execResult.ExitCode != 0 || guestExitCode != 0)
                && !File.Exists(statusPath))
            {
                throw new IOException(
                    $"Windows Sandbox could not start or complete the authenticated guest worker. CLI exit {execResult.ExitCode}; guest exit {guestExitCode}; {Bounded(execResult.StdErr, execResult.StdOut)}");
            }
            SandboxWorker.SandboxCaptureStatus status;
            try
            {
                status = await WaitForStatusAsync(
                    result,
                    resultGuard,
                    sessionId,
                    sessionKey,
                    cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"{exception.Message} Worker exec result: CLI exit {execResult.ExitCode}; guest exit {guestExitCode}; {Bounded(execResult.StdErr, execResult.StdOut)}",
                    exception);
            }
            if (!status.IsSuccess)
            {
                outcome = Failure(
                    status.Code,
                    status.Message,
                    client);
            }
            else if (!string.Equals(
                    status.BundleFile,
                    SandboxWorker.BundleFileName,
                    StringComparison.Ordinal)
                || status.BundleSha256 is null)
            {
                outcome = Failure(
                    DiagnosticCode.UnsupportedClient,
                    "The authenticated sandbox status contained an invalid bundle contract.",
                    client);
            }
            else
            {
                string bundlePath = Path.Combine(
                    result,
                    SandboxWorker.BundleFileName);
                using LockedSandboxFile bundleFile =
                    SandboxPathGuard.OpenBoundedFile(
                        result,
                        bundlePath,
                        SandboxWorker.MaxBundleBytes);
                byte[] bundleBytes = await bundleFile.ReadAllAsync(
                    cancellationToken);
                string actualBundleHash =
                    SandboxSessionProtocol.Sha256Hex(bundleBytes);
                if (!SandboxSessionProtocol.FixedTimeSha256Equals(
                        status.BundleSha256,
                        actualBundleHash))
                {
                    outcome = Failure(
                        DiagnosticCode.UnsupportedClient,
                        "The authenticated sandbox bundle digest did not match its handoff.",
                        bundlePath);
                }
                else
                {
                    ToolResult<T2CaptureBundle> parsed =
                        T2CaptureBundleSerializer.Deserialize(
                            Encoding.UTF8.GetString(bundleBytes));
                    outcome = parsed.IsSuccess
                        ? await RevalidateAsync(
                            parsed.Value!,
                            clientHash,
                            pakHash,
                            pak,
                            pakInput.Length,
                            cancellationToken)
                        : ToolResult<T2CaptureBundle>.Failure(
                            parsed.Error! with
                            {
                                Subject = bundlePath
                            });
                }
            }

            if ((execResult.ExitCode != 0 || guestExitCode != 0)
                && outcome.IsSuccess)
            {
                outcome = Failure(
                    DiagnosticCode.UnsupportedClient,
                    "The sandbox worker reported a failing exit code despite an authenticated success handoff.",
                    client);
            }

            stageGuard.VerifyUnchanged();
            jobGuard.VerifyUnchanged();
            resultGuard.VerifyUnchanged();
            sessionGuard.VerifyUnchanged();
            if (!FixedTimeHashEquals(
                    clientHash,
                    await Sha256Async(
                        clientInput.Handle,
                        cancellationToken))
                || !FixedTimeHashEquals(
                    pakHash,
                    await Sha256Async(
                        pakInput.Handle,
                        cancellationToken)))
            {
                outcome = Failure(
                    DiagnosticCode.UnsupportedClient,
                    "An original capture input changed during sandbox capture.",
                    client);
            }
        }
        catch (OperationCanceledException exception)
        {
            outcome = Failure(
                DiagnosticCode.Cancelled,
                exception is SandboxCliCancellationException
                    ? $"Windows Sandbox T2 capture was cancelled. {exception.Message}"
                    : "Windows Sandbox T2 capture was cancelled.",
                client);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or Win32Exception
                or JsonException
                or InvalidDataException
                or OverflowException
                or TimeoutException)
        {
            outcome = Failure(
                DiagnosticCode.ChildNetworkDenialFailure,
                $"Windows Sandbox T2 capture failed safely: {exception.Message}",
                client);
        }
        finally
        {
            if (sandboxId is not null)
            {
                try
                {
                    using var cleanupDeadline =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(45));
                    await StopSandboxAsync(
                        sandboxId,
                        cleanupDeadline.Token);
                }
                catch (Exception exception)
                {
                    lifecycleCleanup = AppendCleanup(
                        lifecycleCleanup,
                        $"Exact Windows Sandbox stop/verification failed: {exception.Message}");
                }
            }

            for (int index = guards.Count - 1;
                index >= 0;
                index--)
            {
                try
                {
                    if (guards[index] is SandboxPathGuard guard)
                    {
                        guard.VerifyUnchanged();
                    }
                }
                catch (Exception exception)
                {
                    lifecycleCleanup = AppendCleanup(
                        lifecycleCleanup,
                        $"Sandbox path identity recheck failed: {exception.Message}");
                }
                try
                {
                    guards[index].Dispose();
                }
                catch (Exception exception)
                {
                    lifecycleCleanup = AppendCleanup(
                        lifecycleCleanup,
                        $"Sandbox path lock disposal failed: {exception.Message}");
                }
            }
            if (sessionKey.Length != 0)
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
            if (gateHeld)
            {
                SandboxGate.Release();
            }
        }

        if (lifecycleCleanup is not null)
        {
            outcome = T2TableCaptureProvider.MergeCleanupFailure(
                outcome,
                lifecycleCleanup);
        }
        return await CompleteCleanupAsync(
            outcome,
            stage,
            session);
    }

    internal static string CreateConfiguration(
        string inputRoot,
        string jobRoot,
        string resultRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultRoot);
        var document = new XDocument(
            new XElement(
                "Configuration",
                new XElement("vGPU", "Disable"),
                new XElement("Networking", "Disable"),
                new XElement("AudioInput", "Disable"),
                new XElement("VideoInput", "Disable"),
                new XElement("ProtectedClient", "Enable"),
                new XElement("PrinterRedirection", "Disable"),
                new XElement("ClipboardRedirection", "Disable"),
                new XElement(
                    "MappedFolders",
                    Mapping(
                        Path.GetFullPath(inputRoot),
                        @"C:\MisterInput",
                        readOnly: true),
                    Mapping(
                        Path.GetFullPath(jobRoot),
                        @"C:\MisterJob",
                        readOnly: true),
                    Mapping(
                        Path.GetFullPath(resultRoot),
                        @"C:\MisterResult",
                        readOnly: false))));
        return document.ToString();
    }

    internal static bool IsSandboxListed(
        string json,
        string sandboxId)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(
                "WindowsSandboxEnvironments",
                out JsonElement environments)
            || environments.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Windows Sandbox CLI returned an invalid environment list.");
        }
        foreach (JsonElement environment in environments.EnumerateArray())
        {
            if (environment.ValueKind == JsonValueKind.Object
                && environment.TryGetProperty(
                    "Id",
                    out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(
                    id.GetString(),
                    sandboxId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static XElement Mapping(
        string host,
        string guest,
        bool readOnly) =>
        new(
            "MappedFolder",
            new XElement("HostFolder", host),
            new XElement("SandboxFolder", guest),
            new XElement(
                "ReadOnly",
                readOnly ? "true" : "false"));

    private static async Task<SandboxWorker.SandboxCaptureStatus>
        WaitForStatusAsync(
            string resultRoot,
            SandboxPathGuard resultGuard,
            string sessionId,
            byte[] sessionKey,
            CancellationToken cancellationToken)
    {
        string statusPath = Path.Combine(
            resultRoot,
            SandboxWorker.StatusFileName);
        using var deadline = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                resultGuard.VerifyUnchanged();
                if (File.Exists(statusPath))
                {
                    using LockedSandboxFile statusFile =
                        SandboxPathGuard.OpenBoundedFile(
                            resultRoot,
                            statusPath,
                            SandboxWorker.MaxStatusBytes);
                    byte[] statusBytes =
                        await statusFile.ReadAllAsync(
                            deadline.Token);
                    SandboxWorker.SandboxCaptureStatus status =
                        JsonSerializer.Deserialize<
                            SandboxWorker.SandboxCaptureStatus>(
                                statusBytes,
                                JsonOptions)
                        ?? throw new InvalidDataException(
                            "The sandbox status handoff was empty.");
                    if (!SandboxSessionProtocol.VerifyStatus(
                            status,
                            sessionId,
                            sessionKey))
                    {
                        throw new InvalidDataException(
                            "The sandbox status handoff failed session authentication.");
                    }
                    return status;
                }
                await Task.Delay(250, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Windows Sandbox exceeded its 120-second launch/capture deadline.");
        }
    }

    private async Task StopSandboxAsync(
        string sandboxId,
        CancellationToken cancellationToken)
    {
        SandboxCliResult? stop = null;
        Exception? stopException = null;
        try
        {
            stop = await cli.RunAsync(
                ["stop", "--id", sandboxId, "--raw"],
                TimeSpan.FromSeconds(30),
                cancellationToken);
        }
        catch (Exception exception)
        {
            stopException = exception;
        }

        SandboxCliResult list;
        try
        {
            list = await cli.RunAsync(
                ["list", "--raw"],
                TimeSpan.FromSeconds(10),
                cancellationToken);
        }
        catch (Exception listException)
        {
            throw new IOException(
                stopException is null
                    ? $"Windows Sandbox CLI could not verify exact guest shutdown: {listException.Message}"
                    : $"Windows Sandbox stop failed: {stopException.Message} Exact shutdown verification also failed: {listException.Message}",
                new AggregateException(
                    stopException ?? listException,
                    listException));
        }

        RequireCliSuccess(
            list,
            "Windows Sandbox CLI could not verify exact guest shutdown.");
        bool active = IsSandboxListed(
            list.StdOut,
            sandboxId);
        var failures = new List<string>();
        if (stopException is not null)
        {
            failures.Add(
                $"Windows Sandbox stop raised: {stopException.Message}");
        }
        else if (stop!.ExitCode != 0)
        {
            failures.Add(
                $"Windows Sandbox stop exited {stop.ExitCode}: {Bounded(stop.StdErr, stop.StdOut)}");
        }
        if (active)
        {
            failures.Add(
                "Windows Sandbox remained active after its exact stop command.");
        }
        if (failures.Count != 0)
        {
            throw new IOException(string.Join(" ", failures));
        }
    }

    private static void RequireCliSuccess(
        SandboxCliResult result,
        string message)
    {
        if (result.ExitCode != 0)
        {
            throw new IOException(
                $"{message} {Bounded(result.StdErr, result.StdOut)}".Trim());
        }
    }

    private static int GuestExitCode(SandboxCliResult result)
    {
        using JsonDocument document =
            JsonDocument.Parse(result.StdOut);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(
                "ExitCode",
                out JsonElement exitCode)
            || !exitCode.TryGetInt32(out int value))
        {
            throw new InvalidDataException(
                "Windows Sandbox CLI returned an invalid exec result.");
        }
        return value;
    }

    private static string Bounded(
        string first,
        string second)
    {
        string combined = $"{first} {second}".Trim();
        return combined.Length <= 2048
            ? combined
            : combined[..2048];
    }

    private static async ValueTask<ToolResult<T2CaptureBundle>>
        RevalidateAsync(
            T2CaptureBundle bundle,
            string clientHash,
            string pakHash,
            string originalPak,
            long originalPakLength,
            CancellationToken cancellationToken)
    {
        if (!FixedTimeHashEquals(
                bundle.ClientSha256,
                clientHash)
            || !FixedTimeHashEquals(
                bundle.ValidationArchiveSha256,
                pakHash))
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                "The sandbox bundle did not bind the original client and PAK.",
                originalPak);
        }
        ToolResult<ClientEvidence> evidence =
            T2CaptureBundleSerializer.ToEvidence(
                bundle,
                clientHash);
        if (!evidence.IsSuccess)
        {
            return ToolResult<T2CaptureBundle>.Failure(
                evidence.Error!);
        }
        ToolResult<Mister.Core.Formats.ArchiveIndex> index =
            await new T2ArchiveReader(evidence.Value!)
                .IndexAsync(
                    originalPak,
                    cancellationToken);
        if (!index.IsSuccess)
        {
            return ToolResult<T2CaptureBundle>.Failure(
                index.Error!);
        }
        if (index.Value!.FinalOffset != originalPakLength)
        {
            return Failure(
                DiagnosticCode.TrailingData,
                "Host revalidation did not consume the original PAK exactly.",
                originalPak);
        }
        return ToolResult<T2CaptureBundle>.Success(bundle);
    }

    private static async Task CopyFromHandleAsync(
        SafeFileHandle source,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        long length = RandomAccess.GetLength(source);
        if (length is < 1 || length > maximumBytes)
        {
            throw new InvalidDataException(
                "A sandbox job input exceeded its size limit.");
        }
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        byte[] buffer = new byte[BufferSize];
        long offset = 0;
        while (offset < length)
        {
            int read = await RandomAccess.ReadAsync(
                source,
                buffer.AsMemory(
                    0,
                    (int)Math.Min(
                        buffer.Length,
                        length - offset)),
                offset,
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "A locked sandbox job input ended during copy.");
            }
            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            offset += read;
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> Sha256Async(
        SafeFileHandle file,
        CancellationToken cancellationToken)
    {
        long length = RandomAccess.GetLength(file);
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        long offset = 0;
        while (offset < length)
        {
            int read = await RandomAccess.ReadAsync(
                file,
                buffer.AsMemory(
                    0,
                    (int)Math.Min(
                        buffer.Length,
                        length - offset)),
                offset,
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "A locked capture input ended during hashing.");
            }
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        return Convert.ToHexString(
            hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool FixedTimeHashEquals(
        string left,
        string right) =>
        SandboxSessionProtocol.FixedTimeSha256Equals(
            left,
            right);

    private static async ValueTask<ToolResult<T2CaptureBundle>>
        CompleteCleanupAsync(
            ToolResult<T2CaptureBundle> outcome,
            string? stage,
            string? session)
    {
        ToolDiagnostic? cleanup = null;
        if (stage is not null)
        {
            try
            {
                await T2ClientStager.DeleteAsync(stage);
            }
            catch (Exception exception)
            {
                cleanup = AppendCleanup(
                    cleanup,
                    $"Sandbox stage cleanup failed: {exception.Message}");
            }
        }
        if (session is not null)
        {
            try
            {
                await DeleteDirectoryAsync(session);
            }
            catch (Exception exception)
            {
                cleanup = AppendCleanup(
                    cleanup,
                    $"Sandbox session cleanup failed: {exception.Message}");
            }
        }
        return cleanup is null
            ? outcome
            : T2TableCaptureProvider.MergeCleanupFailure(
                outcome,
                cleanup);
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(100 * (attempt + 1));
            }
        }
        throw new IOException(
            "The sandbox session remained after three deletion attempts.");
    }

    private static ToolDiagnostic AppendCleanup(
        ToolDiagnostic? existing,
        string message) =>
        existing is null
            ? new ToolDiagnostic(
                DiagnosticCode.OutputPermissionFailure,
                message)
            : existing with
            {
                Message = $"{existing.Message} {message}"
            };

    private static ToolResult<T2CaptureBundle> Failure(
        DiagnosticCode code,
        string message,
        string subject) =>
        ToolResult<T2CaptureBundle>.Failure(
            new ToolDiagnostic(code, message, subject));
}
