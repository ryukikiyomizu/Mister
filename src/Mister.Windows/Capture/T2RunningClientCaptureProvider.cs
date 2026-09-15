using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mister.Core.Diagnostics;
using Mister.Core.Evidence;

namespace Mister.Windows.Capture;

[SupportedOSPlatform("windows")]
public sealed class T2RunningClientCaptureProvider
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint StillActive = 259;
    private const ulong SubtractTableRva = 0x3C39E8;
    private const ulong RecordXorTableRva = 0x3698A0;
    private readonly ProcessMemoryReader memory;
    private readonly Func<
        string,
        uint[],
        byte[],
        CancellationToken,
        ValueTask<ToolResult<bool>>> validate;

    public T2RunningClientCaptureProvider()
        : this(
            new ProcessMemoryReader(),
            T2TableCaptureProvider.ValidateSnapshotAsync)
    {
    }

    internal T2RunningClientCaptureProvider(
        ProcessMemoryReader memory,
        Func<
            string,
            uint[],
            byte[],
            CancellationToken,
            ValueTask<ToolResult<bool>>> validate)
    {
        this.memory = memory;
        this.validate = validate;
    }

    public async ValueTask<ToolResult<T2CaptureBundle>> CaptureAsync(
        int processId,
        string expectedClientPath,
        string validationPakPath,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedClientPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(validationPakPath);
        string expectedClient = Path.GetFullPath(expectedClientPath);
        string pak = Path.GetFullPath(validationPakPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SafeFileHandle expectedClientHandle = OpenLockedFile(
                expectedClient);
            using SafeFileHandle pakHandle = OpenLockedFile(pak);
            FileIdentity expectedIdentity = GetFileIdentity(
                expectedClientHandle);
            using SafeFileHandle process = OpenReadOnlyProcess(processId);
            ProcessIdentity initial = GetProcessIdentity(
                process,
                processId,
                expectedIdentity);

            ulong? imageBase = null;
            ToolResult<CapturedT2Tables> captured =
                await T2TableCaptureProvider.PollForValidatedSnapshotAsync(
                    token => new ValueTask<CapturedT2Tables>(Task.Run(
                        () =>
                        {
                            bool retained = false;
                            try
                            {
                                process.DangerousAddRef(ref retained);
                                token.ThrowIfCancellationRequested();
                                EnsureProcessIdentity(
                                    process,
                                    processId,
                                    initial,
                                    expectedIdentity);
                                IntPtr nativeProcess =
                                    process.DangerousGetHandle();
                                imageBase ??= memory.GetImageBase(
                                    nativeProcess);
                                byte[] subtractBytes = memory.ReadExact(
                                    nativeProcess,
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
                                    nativeProcess,
                                    checked(
                                        imageBase.Value
                                        + RecordXorTableRva),
                                    256);
                                EnsureProcessIdentity(
                                    process,
                                    processId,
                                    initial,
                                    expectedIdentity);
                                return new CapturedT2Tables(
                                    subtract,
                                    xor);
                            }
                            finally
                            {
                                if (retained)
                                {
                                    process.DangerousRelease();
                                }
                            }
                        },
                        token)),
                    (tables, token) => validate(
                        pak,
                        tables.SubtractTable,
                        tables.RecordXorTable,
                        token),
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            if (!captured.IsSuccess)
            {
                return ToolResult<T2CaptureBundle>.Failure(
                    captured.Error! with { Subject = expectedClient });
            }

            EnsureProcessIdentity(
                process,
                processId,
                initial,
                expectedIdentity);
            string clientHash = await Sha256Async(
                expectedClient,
                cancellationToken);
            string pakHash = await Sha256Async(
                pak,
                cancellationToken);
            EnsureProcessIdentity(
                process,
                processId,
                initial,
                expectedIdentity);
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
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "Read-only T2 process attachment was cancelled.",
                expectedClient);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or OverflowException
                or Win32Exception)
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                $"Read-only T2 process attachment failed safely: {exception.Message}",
                expectedClient);
        }
    }

    internal static SafeFileHandle OpenReadOnlyProcess(int processId)
    {
        SafeFileHandle process = OpenProcess(
            ProcessQueryInformation | ProcessVmRead,
            inheritHandle: false,
            checked((uint)processId));
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            process.Dispose();
            throw new Win32Exception(
                error,
                "Unable to open the selected process for query/read access.");
        }
        return process;
    }

    private static ProcessIdentity GetProcessIdentity(
        SafeFileHandle process,
        int expectedProcessId,
        FileIdentity expectedFile)
    {
        if (GetProcessId(process) != checked((uint)expectedProcessId))
        {
            throw new IOException(
                "The opened process does not have the selected PID.");
        }
        EnsureStillActive(process);
        if (!GetProcessTimes(
            process,
            out FileTime creation,
            out _,
            out _,
            out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to read the selected process creation identity.");
        }
        string imagePath = QueryImagePath(process);
        using SafeFileHandle imageFile = OpenLockedFile(imagePath);
        if (GetFileIdentity(imageFile) != expectedFile)
        {
            throw new IOException(
                "The selected process image is not the expected client file.");
        }
        return new ProcessIdentity(
            creation.High,
            creation.Low,
            imagePath);
    }

    private static void EnsureProcessIdentity(
        SafeFileHandle process,
        int expectedProcessId,
        ProcessIdentity expectedProcess,
        FileIdentity expectedFile)
    {
        ProcessIdentity current = GetProcessIdentity(
            process,
            expectedProcessId,
            expectedFile);
        if (current != expectedProcess)
        {
            throw new IOException(
                "The selected process identity changed during capture.");
        }
    }

    private static void EnsureStillActive(SafeFileHandle process)
    {
        if (!GetExitCodeProcess(process, out uint exitCode))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to query whether the selected process is active.");
        }
        if (exitCode != StillActive)
        {
            throw new IOException(
                "The selected process exited during read-only capture.");
        }
    }

    private static string QueryImagePath(SafeFileHandle process)
    {
        var path = new StringBuilder(32768);
        uint length = checked((uint)path.Capacity);
        if (!QueryFullProcessImageName(
            process,
            0,
            path,
            ref length))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to prove the selected process image path.");
        }
        return Path.GetFullPath(path.ToString());
    }

    private static SafeFileHandle OpenLockedFile(string path)
    {
        SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"Unable to lock the selected capture input: {path}");
        }
        return handle;
    }

    private static FileIdentity GetFileIdentity(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(
            file,
            out ByHandleFileInformation information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to prove a selected capture file identity.");
        }
        return new FileIdentity(
            information.VolumeSerialNumber,
            information.FileIndexHigh,
            information.FileIndexLow);
    }

    private static async Task<string> Sha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static ToolResult<T2CaptureBundle> Failure(
        DiagnosticCode code,
        string message,
        string subject) =>
        ToolResult<T2CaptureBundle>.Failure(
            new ToolDiagnostic(code, message, subject));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(
        SafeFileHandle process,
        out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeFileHandle process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeFileHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    private sealed record ProcessIdentity(
        uint CreationHigh,
        uint CreationLow,
        string ImagePath);

    private sealed record FileIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
