using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mister.Core.Extraction;

internal readonly record struct FileHandleIdentity(
    ulong VolumeSerialNumber,
    ulong Low,
    ulong High);

internal readonly record struct OpenedFileProof(
    string FinalPath,
    FileHandleIdentity? Identity);

internal static class OpenedFileHandle
{
    private const uint FileNameNormalized = 0;

    public static OpenedFileProof Capture(
        SafeFileHandle handle,
        string fallbackPath)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackPath);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new IOException("The opened file handle is not valid.");
        }

        if (OperatingSystem.IsWindows())
        {
            return CaptureWindows(handle);
        }

        string finalPath = OperatingSystem.IsLinux()
            ? ResolveLinuxHandle(handle)
            : ExtractionOptions.Canonicalize(fallbackPath);
        return new OpenedFileProof(finalPath, Identity: null);
    }

    public static bool RepresentsSameObject(
        OpenedFileProof expected,
        OpenedFileProof actual)
    {
        if (expected.Identity is not null && actual.Identity is not null)
        {
            return expected.Identity == actual.Identity;
        }

        return StringComparer.OrdinalIgnoreCase.Equals(
            expected.FinalPath,
            actual.FinalPath);
    }

    internal static bool IsWindowsNameCollision(
        IOException exception) =>
        exception is WindowsNativeIOException native
            && native.NativeErrorCode
                is NativeMethods.ErrorFileExists
                    or NativeMethods.ErrorAlreadyExists;

    internal static SafeFileHandle OpenWindowsDirectory(string path)
    {
        EnsureWindows();
        SafeFileHandle handle = NativeMethods.CreateFile(
            ToExtendedWindowsPath(path),
            NativeMethods.GenericRead
                | NativeMethods.Synchronize,
            NativeMethods.FileShareRead
                | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics,
            IntPtr.Zero);
        return EnsureValid(handle, "open the output directory");
    }

    internal static FileStream CreateWindowsOutputPart(
        SafeFileHandle directory,
        string fileName)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException(
                "A handle-relative create requires a simple file name.",
                nameof(fileName));
        }

        byte[] name = Encoding.Unicode.GetBytes(fileName + '\0');
        GCHandle pinnedName = GCHandle.Alloc(name, GCHandleType.Pinned);
        IntPtr unicodePointer = IntPtr.Zero;
        bool directoryReference = false;
        SafeFileHandle? handle = null;
        try
        {
            directory.DangerousAddRef(ref directoryReference);
            var unicodeName = new NativeMethods.UnicodeString
            {
                Length = checked((ushort)(name.Length - sizeof(char))),
                MaximumLength = checked((ushort)name.Length),
                Buffer = pinnedName.AddrOfPinnedObject()
            };
            unicodePointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<NativeMethods.UnicodeString>());
            Marshal.StructureToPtr(
                unicodeName,
                unicodePointer,
                fDeleteOld: false);
            var attributes = new NativeMethods.ObjectAttributes
            {
                Length = checked((uint)
                    Marshal.SizeOf<NativeMethods.ObjectAttributes>()),
                RootDirectory = directory.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = NativeMethods.ObjCaseInsensitive
            };
            int status = NativeMethods.NtCreateFile(
                out handle,
            NativeMethods.GenericRead
                | NativeMethods.GenericWrite
                    | NativeMethods.Delete
                    | NativeMethods.Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                NativeMethods.FileAttributeNormal,
                NativeMethods.FileShareRead,
                NativeMethods.FileCreate,
                NativeMethods.FileNonDirectoryFile
                    | NativeMethods.FileSequentialOnly,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                handle?.Dispose();
                handle = null;
                throw NtIOException(
                    "create the atomic output part relative to its directory handle",
                    status);
            }

            return new FileStream(
                handle,
                FileAccess.ReadWrite,
                bufferSize: 64 * 1024,
                isAsync: true);
        }
        catch
        {
            handle?.Dispose();
            throw;
        }
        finally
        {
            if (directoryReference)
            {
                directory.DangerousRelease();
            }

            if (unicodePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodePointer);
            }

            pinnedName.Free();
        }
    }

    internal static void DeleteWindowsHandleOnClose(
        SafeFileHandle file)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(file);
        byte[] information = [1];
        GCHandle pinned = GCHandle.Alloc(
            information,
            GCHandleType.Pinned);
        try
        {
            int status = NativeMethods.NtSetInformationFile(
                file,
                out _,
                pinned.AddrOfPinnedObject(),
                (uint)information.Length,
                NativeMethods.FileDispositionInformation);
            if (status < 0)
            {
                throw NtIOException(
                    "mark the atomic output handle for deletion",
                    status);
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    internal static void RenameWindowsHandleNoReplace(
        SafeFileHandle source,
        SafeFileHandle destinationDirectory,
        string destinationFileName)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFileName);
        if (Path.GetFileName(destinationFileName) != destinationFileName)
        {
            throw new ArgumentException(
                "A handle-relative rename requires a simple file name.",
                nameof(destinationFileName));
        }

        byte[] name = Encoding.Unicode.GetBytes(destinationFileName);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int nameLengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = nameLengthOffset + sizeof(uint);
        byte[] information = new byte[
            nameOffset + name.Length + sizeof(char)];
        GCHandle pinned = GCHandle.Alloc(
            information,
            GCHandleType.Pinned);
        bool directoryReference = false;
        try
        {
            destinationDirectory.DangerousAddRef(
                ref directoryReference);
            IntPtr buffer = pinned.AddrOfPinnedObject();
            Marshal.WriteByte(buffer, 0, 0);
            Marshal.WriteIntPtr(
                buffer,
                rootOffset,
                destinationDirectory.DangerousGetHandle());
            Marshal.WriteInt32(
                buffer,
                nameLengthOffset,
                name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            int status = NativeMethods.NtSetInformationFile(
                    source,
                    out _,
                    buffer,
                    (uint)information.Length,
                    NativeMethods.FileRenameInformation);
            if (status < 0)
            {
                throw NtIOException(
                    "rename the atomic output handle",
                    status);
            }
        }
        finally
        {
            if (directoryReference)
            {
                destinationDirectory.DangerousRelease();
            }

            pinned.Free();
        }
    }

    private static OpenedFileProof CaptureWindows(
        SafeFileHandle handle)
    {
        string finalPath = GetFinalWindowsPath(handle);
        int size = Marshal.SizeOf<NativeMethods.FileIdInfo>();
        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                NativeMethods.FileIdInfoClass,
                out NativeMethods.FileIdInfo information,
                (uint)size))
        {
            throw WindowsIOException(
                "query the opened file identity");
        }

        return new OpenedFileProof(
            finalPath,
            new FileHandleIdentity(
                information.VolumeSerialNumber,
                information.FileId.Low,
                information.FileId.High));
    }

    private static string GetFinalWindowsPath(SafeFileHandle handle)
    {
        int capacity = 512;
        while (capacity <= short.MaxValue * 2)
        {
            var buffer = new StringBuilder(capacity);
            uint length = NativeMethods.GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)capacity,
                FileNameNormalized);
            if (length == 0)
            {
                throw WindowsIOException(
                    "query the opened file path");
            }

            if (length < capacity)
            {
                return NormalizeWindowsFinalPath(buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }

        throw new IOException(
            "The opened file path exceeds the supported Windows path length.");
    }

    private static string ResolveLinuxHandle(SafeFileHandle handle)
    {
        string procPath = $"/proc/self/fd/{handle.DangerousGetHandle()}";
        FileSystemInfo? target =
            new FileInfo(procPath).ResolveLinkTarget(
                returnFinalTarget: true);
        if (target is null)
        {
            throw new IOException(
                "The opened file path could not be resolved from /proc.");
        }

        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(target.FullName));
    }

    private static string NormalizeWindowsFinalPath(string path)
    {
        const string extendedUnc = @"\\?\UNC\";
        const string extended = @"\\?\";
        string normalized = path.StartsWith(
                extendedUnc,
                StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[extendedUnc.Length..]
            : path.StartsWith(
                extended,
                StringComparison.OrdinalIgnoreCase)
                ? path[extended.Length..]
                : path;
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(normalized));
    }

    private static string ToExtendedWindowsPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(
                @"\\?\",
                StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.StartsWith(
                @"\\",
                StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private static SafeFileHandle EnsureValid(
        SafeFileHandle handle,
        string operation)
    {
        if (!handle.IsInvalid)
        {
            return handle;
        }

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException(
            $"Windows could not {operation}: {new Win32Exception(error).Message}",
            new Win32Exception(error));
    }

    private static IOException WindowsIOException(string operation)
    {
        int error = Marshal.GetLastWin32Error();
        return new IOException(
            $"Windows could not {operation}: {new Win32Exception(error).Message}",
            new Win32Exception(error));
    }

    private static WindowsNativeIOException NtIOException(
        string operation,
        int status)
    {
        int error = unchecked((int)
            NativeMethods.RtlNtStatusToDosError(status));
        return new WindowsNativeIOException(
            $"Windows could not {operation}: {new Win32Exception(error).Message}",
            error,
            new Win32Exception(error));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "This handle operation is available only on Windows.");
        }
    }

    private static class NativeMethods
    {
        internal const uint GenericWrite = 0x40000000;
        internal const uint GenericRead = 0x80000000;
        internal const uint Delete = 0x00010000;
        internal const uint Synchronize = 0x00100000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint FileAttributeNormal = 0x00000080;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint ObjCaseInsensitive = 0x00000040;
        internal const uint FileCreate = 2;
        internal const uint FileSequentialOnly = 0x00000004;
        internal const uint FileNonDirectoryFile = 0x00000040;
        internal const int ErrorFileExists = 80;
        internal const int ErrorAlreadyExists = 183;
        internal const int FileRenameInformation = 10;
        internal const int FileDispositionInformation = 13;
        internal const int FileIdInfoClass = 18;

        [StructLayout(LayoutKind.Sequential)]
        internal struct UnicodeString
        {
            internal ushort Length;
            internal ushort MaximumLength;
            internal IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ObjectAttributes
        {
            internal uint Length;
            internal IntPtr RootDirectory;
            internal IntPtr ObjectName;
            internal uint Attributes;
            internal IntPtr SecurityDescriptor;
            internal IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileId128
        {
            internal ulong Low;
            internal ulong High;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdInfo
        {
            internal ulong VolumeSerialNumber;
            internal FileId128 FileId;
        }

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetFinalPathNameByHandleW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [DllImport(
            "kernel32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            out FileIdInfo information,
            uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoStatusBlock
        {
            internal IntPtr Status;
            internal UIntPtr Information;
        }

        [DllImport("ntdll.dll")]
        internal static extern int NtCreateFile(
            out SafeFileHandle file,
            uint desiredAccess,
            ref ObjectAttributes objectAttributes,
            out IoStatusBlock ioStatusBlock,
            IntPtr allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            IntPtr eaBuffer,
            uint eaLength);

        [DllImport("ntdll.dll")]
        internal static extern int NtSetInformationFile(
            SafeFileHandle file,
            out IoStatusBlock ioStatusBlock,
            IntPtr information,
            uint bufferSize,
            int informationClass);

        [DllImport("ntdll.dll")]
        internal static extern uint RtlNtStatusToDosError(int status);
    }

    private sealed class WindowsNativeIOException(
        string message,
        int nativeErrorCode,
        Exception innerException) : IOException(message, innerException)
    {
        internal int NativeErrorCode { get; } = nativeErrorCode;
    }
}
