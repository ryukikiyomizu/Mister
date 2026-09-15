using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Windows.AppContainer;
using Mister.Windows.Capture;

namespace Mister.Windows.Sandbox;

[SupportedOSPlatform("windows")]
public static class SandboxWorker
{
    internal const string BundleFileName = "capture.t2capture";
    internal const string StatusFileName = "status.json";
    internal const int MaxContractBytes = 64 * 1024;
    internal const int MaxStatusBytes = 64 * 1024;
    internal const int MaxBundleBytes = 1024 * 1024;
    private const ulong SubtractTableRva = 0x3C39E8;
    private const ulong RecordXorTableRva = 0x3698A0;
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? contractPath = GetArgument(args, "--contract");
        string? invocationSessionId = GetArgument(
            args,
            "--session-id");
        string? encodedSessionKey = GetArgument(
            args,
            "--session-key");
        if (contractPath is null
            || !IsSessionId(invocationSessionId)
            || !TryDecodeSessionKey(
                encodedSessionKey,
                out byte[] sessionKey))
        {
            return 2;
        }

        SandboxCaptureContract? contract = null;
        SandboxCaptureStatus? status = null;
        try
        {
            using LockedSandboxFile contractFile =
                SandboxPathGuard.OpenBoundedFile(
                    @"C:\MisterJob",
                    contractPath,
                    MaxContractBytes);
            byte[] contractBytes = await contractFile.ReadAllAsync(
                cancellationToken);
            contract = JsonSerializer.Deserialize<SandboxCaptureContract>(
                contractBytes,
                JsonOptions)
                ?? throw new InvalidDataException(
                    "The sandbox capture contract was empty.");
            ValidateContract(contract);
            if (!SandboxSessionProtocol.FixedTimeSessionIdEquals(
                    contract.SessionId,
                    invocationSessionId!))
            {
                throw new InvalidDataException(
                    "The sandbox contract did not bind the invoked session.");
            }
            using LockedSandboxFile clientInput =
                SandboxPathGuard.OpenBoundedFile(
                    @"C:\MisterInput",
                    contract.ClientPath,
                    int.MaxValue);
            using LockedSandboxFile pakInput =
                SandboxPathGuard.OpenBoundedFile(
                    @"C:\MisterJob",
                    contract.ValidationPakPath,
                    int.MaxValue);
            using SandboxPathGuard resultGuard =
                SandboxPathGuard.LockDirectory(
                    contract.ResultDirectory);
            ToolResult<T2CaptureBundle> capture = await CaptureAsync(
                contract,
                clientInput.Handle,
                pakInput.Handle,
                cancellationToken);
            if (!capture.IsSuccess)
            {
                status = SandboxSessionProtocol.CreateStatus(
                    contract.SessionId,
                    sessionKey,
                    isSuccess: false,
                    bundleFile: null,
                    bundleSha256: null,
                    capture.Error!.Code,
                    capture.Error.Message);
            }
            else
            {
                string bundleJson = T2CaptureBundleSerializer.Serialize(
                    capture.Value!);
                byte[] bundleBytes =
                    System.Text.Encoding.UTF8.GetBytes(bundleJson);
                if (bundleBytes.Length > MaxBundleBytes)
                {
                    throw new InvalidDataException(
                        "The sandbox capture bundle exceeded its size limit.");
                }
                await WriteAtomicAsync(
                    Path.Combine(
                        contract.ResultDirectory,
                        BundleFileName),
                    bundleBytes,
                    cancellationToken);
                status = SandboxSessionProtocol.CreateStatus(
                    contract.SessionId,
                    sessionKey,
                    isSuccess: true,
                    BundleFileName,
                    SandboxSessionProtocol.Sha256Hex(bundleBytes),
                    DiagnosticCode.UnsupportedClient,
                    "Capture completed.");
            }
            resultGuard.VerifyUnchanged();
        }
        catch (OperationCanceledException)
        {
            status = SandboxSessionProtocol.CreateStatus(
                invocationSessionId!,
                sessionKey,
                isSuccess: false,
                bundleFile: null,
                bundleSha256: null,
                DiagnosticCode.Cancelled,
                "Sandbox capture was cancelled.");
        }
        catch (Exception exception)
        {
            status = SandboxSessionProtocol.CreateStatus(
                invocationSessionId!,
                sessionKey,
                isSuccess: false,
                bundleFile: null,
                bundleSha256: null,
                DiagnosticCode.UnsupportedClient,
                $"Sandbox worker failed safely: {exception.Message}");
        }

        try
        {
            if (status is not null)
            {
                string statusJson = JsonSerializer.Serialize(
                    status,
                    JsonOptions);
                if (JsonSize(statusJson) > MaxStatusBytes)
                {
                    statusJson = JsonSerializer.Serialize(
                        status with
                        {
                            Message =
                                "Sandbox worker status exceeded its size limit."
                        },
                        JsonOptions);
                    status = SandboxSessionProtocol.CreateStatus(
                        invocationSessionId!,
                        sessionKey,
                        status.IsSuccess,
                        status.BundleFile,
                        status.BundleSha256,
                        status.Code,
                        "Sandbox worker status exceeded its size limit.");
                    statusJson = JsonSerializer.Serialize(
                        status,
                        JsonOptions);
                }
                await WriteAtomicAsync(
                    Path.Combine(
                        contract?.ResultDirectory
                            ?? @"C:\MisterResult",
                        StatusFileName),
                    System.Text.Encoding.UTF8.GetBytes(statusJson),
                    CancellationToken.None);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionKey);
        }
        return status?.IsSuccess == true ? 0 : 1;
    }

    private static async ValueTask<ToolResult<T2CaptureBundle>> CaptureAsync(
        SandboxCaptureContract contract,
        SafeFileHandle clientInput,
        SafeFileHandle pakInput,
        CancellationToken cancellationToken)
    {
        string clientHash = await Sha256Async(
            clientInput,
            cancellationToken);
        string pakHash = await Sha256Async(
            pakInput,
            cancellationToken);
        if (!FixedTimeHashEquals(
                clientHash,
                contract.ExpectedClientSha256)
            || !FixedTimeHashEquals(
                pakHash,
                contract.ExpectedPakSha256))
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                "Sandbox inputs did not match the host capture contract.");
        }

        SandboxJobProcess? child = null;
        bool stopped = false;
        try
        {
            child = SandboxJobProcess.Start(
                contract.ClientPath,
                Path.GetDirectoryName(contract.ClientPath)!,
                "127.0.0.1:8012");
            var clock = Stopwatch.StartNew();
            child.Resume();
            ulong? imageBase = null;
            var memory = new ProcessMemoryReader();
            ToolResult<CapturedT2Tables> captured =
                await T2TableCaptureProvider.PollForValidatedSnapshotAsync(
                    token => new ValueTask<CapturedT2Tables>(Task.Run(
                        () =>
                        {
                            bool retained = false;
                            try
                            {
                                child.ProcessHandle.DangerousAddRef(
                                    ref retained);
                                token.ThrowIfCancellationRequested();
                                IntPtr process = child.ProcessHandle
                                    .DangerousGetHandle();
                                imageBase ??= memory.GetImageBase(process);
                                byte[] subtractBytes = memory.ReadExact(
                                    process,
                                    checked(
                                        imageBase.Value
                                        + SubtractTableRva),
                                    256 * sizeof(uint));
                                var subtract = new uint[256];
                                Buffer.BlockCopy(
                                    subtractBytes,
                                    0,
                                    subtract,
                                    0,
                                    subtractBytes.Length);
                                byte[] xor = memory.ReadExact(
                                    process,
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
                                if (retained)
                                {
                                    child.ProcessHandle.DangerousRelease();
                                }
                            }
                        },
                        token)),
                    (tables, token) =>
                        T2TableCaptureProvider.ValidateSnapshotAsync(
                            contract.ValidationPakPath,
                            tables.SubtractTable,
                            tables.RecordXorTable,
                            token),
                    TimeSpan.FromSeconds(10) - clock.Elapsed,
                    cancellationToken);
            if (!captured.IsSuccess)
            {
                return ToolResult<T2CaptureBundle>.Failure(
                    captured.Error!);
            }

            child.TerminateAndWait();
            stopped = true;
            CapturedT2Tables tables = captured.Value!;
            var draft = new T2CaptureBundle(
                1,
                clientHash,
                tables.SubtractTable,
                tables.RecordXorTable,
                DateTimeOffset.UtcNow,
                pakHash,
                string.Empty);
            return ToolResult<T2CaptureBundle>.Success(
                T2CaptureBundleSerializer.Seal(draft));
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or Win32Exception
                or OverflowException)
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                $"Sandbox client capture failed safely: {exception.Message}");
        }
        finally
        {
            if (child is not null)
            {
                if (!stopped)
                {
                    child.TerminateAndWait();
                }
                child.Dispose();
            }
        }
    }

    private static void ValidateContract(SandboxCaptureContract contract)
    {
        string inputRoot = Path.GetFullPath(
            @"C:\MisterInput");
        string jobRoot = Path.GetFullPath(
            @"C:\MisterJob");
        string resultRoot = Path.GetFullPath(
            @"C:\MisterResult");
        RequireContained(
            inputRoot,
            contract.ClientPath,
            "client");
        RequireContained(
            jobRoot,
            contract.ValidationPakPath,
            "validation PAK");
        string actualResult = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(contract.ResultDirectory));
        if (!actualResult.Equals(
            resultRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The sandbox result path escaped its writable mapping.");
        }
        if (!IsSha256(contract.ExpectedClientSha256)
            || !IsSha256(contract.ExpectedPakSha256)
            || !IsSessionId(contract.SessionId))
        {
            throw new InvalidDataException(
                "The sandbox capture contract contained an invalid hash or session ID.");
        }
    }

    private static void RequireContained(
        string root,
        string path,
        string description)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The sandbox {description} path escaped its read-only mapping.");
        }
    }

    private static string? GetArgument(
        IReadOnlyList<string> args,
        string name)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (args[index].Equals(
                name,
                StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static async Task WriteAtomicAsync(
        string destination,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        string temporary = destination + ".tmp";
        await using (var output = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(
                content,
                cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination);
    }

    private static async Task<string> Sha256Async(
        SafeFileHandle file,
        CancellationToken cancellationToken)
    {
        long length = RandomAccess.GetLength(file);
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
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
                    "A locked guest input ended during hashing.");
            }
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        return Convert.ToHexString(
            hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool FixedTimeHashEquals(
        string left,
        string right)
    {
        if (!IsSha256(left) || !IsSha256(right))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    private static bool IsSha256(string? value) =>
        value is not null
        && value.Length == 64
        && value.All(static character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static bool IsSessionId(string? value) =>
        value is not null
        && value.Length == SandboxSessionProtocol.SessionIdBytes * 2
        && value.All(static character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static bool TryDecodeSessionKey(
        string? encoded,
        out byte[] sessionKey)
    {
        sessionKey = [];
        if (encoded is null)
        {
            return false;
        }
        try
        {
            sessionKey = Convert.FromBase64String(encoded);
            if (sessionKey.Length
                == SandboxSessionProtocol.SessionKeyBytes)
            {
                return true;
            }
            CryptographicOperations.ZeroMemory(sessionKey);
            sessionKey = [];
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int JsonSize(string json) =>
        System.Text.Encoding.UTF8.GetByteCount(json);

    private static ToolResult<T2CaptureBundle> Failure(
        DiagnosticCode code,
        string message) =>
        ToolResult<T2CaptureBundle>.Failure(
            new ToolDiagnostic(code, message));

    internal sealed record SandboxCaptureContract(
        string ClientPath,
        string ValidationPakPath,
        string ResultDirectory,
        string ExpectedClientSha256,
        string ExpectedPakSha256,
        string SessionId);

    internal sealed record SandboxCaptureStatus(
        string SessionId,
        bool IsSuccess,
        string? BundleFile,
        string? BundleSha256,
        DiagnosticCode Code,
        string Message,
        string Authenticator);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(
        uint sessionId,
        out SafeFileHandle token);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeFileHandle token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref NativeMethods.StartupInfo startupInfo,
        out NativeMethods.ProcessInformation processInformation);

    private sealed class SandboxJobProcess : IDisposable
    {
        private readonly KillOnCloseJob job;
        private IntPtr thread;
        private bool stopped;

        private SandboxJobProcess(
            SafeFileHandle processHandle,
            IntPtr thread,
            KillOnCloseJob job)
        {
            ProcessHandle = processHandle;
            this.thread = thread;
            this.job = job;
        }

        public SafeFileHandle ProcessHandle { get; }

        public static SandboxJobProcess Start(
            string executable,
            string workingDirectory,
            string arguments)
        {
            uint interactiveSession =
                WTSGetActiveConsoleSessionId();
            if (interactiveSession == uint.MaxValue
                || !WTSQueryUserToken(
                    interactiveSession,
                    out SafeFileHandle interactiveUser))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to obtain the Windows Sandbox interactive-user token.");
            }
            using (interactiveUser)
            {
                var startup = new NativeMethods.StartupInfo
                {
                    cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
                    lpDesktop = @"winsta0\default"
                };
                string commandLine =
                    AppContainerProcess.Quote(executable)
                    + " "
                    + arguments;
                if (!CreateProcessAsUser(
                    interactiveUser,
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.CreateSuspended
                        | NativeMethods.CreateNoWindow,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out NativeMethods.ProcessInformation information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to launch the guest capture client under the lower-privilege Sandbox user.");
                }

                var process = new SafeFileHandle(
                    information.hProcess,
                    ownsHandle: true);
                var job = new KillOnCloseJob();
                try
                {
                    job.Assign(process.DangerousGetHandle());
                    return new SandboxJobProcess(
                        process,
                        information.hThread,
                        job);
                }
                catch
                {
                    NativeMethods.TerminateProcess(
                        process.DangerousGetHandle(),
                        1);
                    NativeMethods.CloseHandle(information.hThread);
                    process.Dispose();
                    job.Dispose();
                    throw;
                }
            }
        }

        public void Resume()
        {
            IntPtr suspendedThread = Interlocked.Exchange(
                ref thread,
                IntPtr.Zero);
            if (suspendedThread == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The guest capture client was already resumed.");
            }
            try
            {
                if (NativeMethods.ResumeThread(suspendedThread)
                    == uint.MaxValue)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to resume the job-bound guest client.");
                }
            }
            finally
            {
                NativeMethods.CloseHandle(suspendedThread);
            }
        }

        public void TerminateAndWait()
        {
            if (stopped)
            {
                return;
            }
            job.Terminate();
            uint result = NativeMethods.WaitForSingleObject(
                ProcessHandle.DangerousGetHandle(),
                5000);
            AppContainerProcess.ThrowIfTerminationWaitFailed(result);
            stopped = true;
        }

        public void Dispose()
        {
            if (!stopped)
            {
                TerminateAndWait();
            }
            IntPtr suspendedThread = Interlocked.Exchange(
                ref thread,
                IntPtr.Zero);
            if (suspendedThread != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(suspendedThread);
            }
            ProcessHandle.Dispose();
            job.Dispose();
        }
    }
}
