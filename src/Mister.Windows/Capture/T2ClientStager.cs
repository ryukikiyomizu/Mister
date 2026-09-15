using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mister.Windows.Capture;

public static class T2ClientStager
{
    private const long MaxFileSize = 16L * 1024 * 1024;
    private const long MaxTotalSize = 128L * 1024 * 1024;
    private const int MaxFiles = 2048;
    private const uint FileReadData = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private static readonly string[] AllowedDirectories =
        ["Resource", "file", "flush"];

    public static async Task<string> StageAsync(
        string sourceRoot,
        string workRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        cancellationToken.ThrowIfCancellationRequested();
        string source = CanonicalDirectory(sourceRoot);
        string work = CanonicalDirectory(workRoot);
        RejectOverlap(source, work);

        using PathLock sourceLock = PathLock.Acquire(source);
        using PathLock workLock = PathLock.Acquire(work);

        string stage = Path.Combine(
            work,
            $"t2-client-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        SafeFileHandle? stageHandle = null;
        try
        {
            stageHandle = OpenVerifiedDirectory(stage, stage);
            var budget = new CopyBudget();
            foreach (string file in Directory.EnumerateFiles(
                source,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(file);
                bool client = name.Equals(
                    "CLIENT.EXE",
                    StringComparison.OrdinalIgnoreCase);
                bool dll = Path.GetExtension(name).Equals(
                    ".dll",
                    StringComparison.OrdinalIgnoreCase);
                bool nonExecutable = !Path.GetExtension(name).Equals(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase);
                if (IsForbiddenFile(name, allowClient: true)
                    || !client && !dll && !nonExecutable)
                {
                    continue;
                }

                using SafeFileHandle sourceHandle = OpenVerifiedEntry(
                    file,
                    source,
                    expectDirectory: false);
                FileSnapshot snapshot = Snapshot(sourceHandle);
                if (nonExecutable && snapshot.Length >= MaxFileSize)
                {
                    continue;
                }
                await CopySafeAsync(
                    sourceHandle,
                    snapshot,
                    Path.Combine(stage, name),
                    budget,
                    enforceFileLimit: nonExecutable,
                    cancellationToken);
            }

            if (!File.Exists(Path.Combine(stage, "CLIENT.EXE")))
            {
                throw new FileNotFoundException(
                    "CLIENT.EXE was not found at the selected T2 root.");
            }

            foreach (string folderName in AllowedDirectories)
            {
                string folder = Path.Combine(source, folderName);
                if (!Directory.Exists(folder))
                {
                    continue;
                }
                await CopyDirectoryAsync(
                    folder,
                    Path.Combine(stage, folderName),
                    source,
                    budget,
                    cancellationToken);
            }
            stageHandle.Dispose();
            stageHandle = null;
            return stage;
        }
        catch (Exception stagingFailure)
        {
            stageHandle?.Dispose();
            try
            {
                await DeleteAsync(stage);
            }
            catch (Exception cleanupFailure)
            {
                throw new IOException(
                    $"T2 client staging failed: {stagingFailure.Message} Stage cleanup also failed: {cleanupFailure.Message}",
                    new AggregateException(
                        stagingFailure,
                        cleanupFailure));
            }
            throw;
        }
    }

    internal static async Task DeleteAsync(string stage)
    {
        if (!Directory.Exists(stage))
        {
            return;
        }

        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                ClearReadOnly(stage);
                Directory.Delete(stage, recursive: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception;
                if (attempt < 2)
                {
                    await Task.Delay(50 * (attempt + 1));
                }
            }
        }
        throw new IOException(
            "Unable to remove the owned T2 client stage after three attempts.",
            lastFailure);
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        string sourceRoot,
        CopyBudget budget,
        CancellationToken token)
    {
        string directoryName = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(source));
        if (IsForbiddenDirectory(directoryName))
        {
            return;
        }

        using SafeFileHandle directoryHandle = OpenVerifiedEntry(
            source,
            sourceRoot,
            expectDirectory: true);
        Directory.CreateDirectory(destination);
        using SafeFileHandle destinationHandle = OpenVerifiedDirectory(
            destination,
            destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(entry);
            using SafeFileHandle entryHandle = OpenVerifiedEntry(
                entry,
                sourceRoot,
                expectDirectory: null);
            FileSnapshot snapshot = Snapshot(entryHandle);
            if (snapshot.IsDirectory)
            {
                if (IsForbiddenDirectory(name))
                {
                    continue;
                }
                await CopyDirectoryAsync(
                    entry,
                    Path.Combine(destination, name),
                    sourceRoot,
                    budget,
                    token);
                continue;
            }
            if (IsForbiddenFile(name, allowClient: false))
            {
                continue;
            }
            await CopySafeAsync(
                entryHandle,
                snapshot,
                Path.Combine(destination, name),
                budget,
                enforceFileLimit: true,
                token);
        }
    }

    private static async Task CopySafeAsync(
        SafeFileHandle sourceHandle,
        FileSnapshot snapshot,
        string destination,
        CopyBudget budget,
        bool enforceFileLimit,
        CancellationToken token)
    {
        if (snapshot.IsDirectory)
        {
            throw new InvalidOperationException(
                "A directory cannot be staged as a support file.");
        }
        if (enforceFileLimit && snapshot.Length >= MaxFileSize)
        {
            throw new InvalidOperationException(
                "A staged support file exceeds the 16 MiB limit.");
        }
        budget.Total = checked(budget.Total + snapshot.Length);
        budget.Count++;
        if (budget.Total > MaxTotalSize || budget.Count > MaxFiles)
        {
            throw new InvalidOperationException(
                "The staged support-file set exceeds its bounded copy limits.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        byte[] sourceHash;
        await using (var input = new FileStream(
            sourceHandle,
            FileAccess.Read,
            64 * 1024,
            isAsync: false))
        {
            using var hasher = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[64 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        token);
                    hasher.AppendData(buffer, 0, read);
                    copied += read;
                }
                if (copied != snapshot.Length
                    || output.Length != snapshot.Length)
                {
                    throw new IOException(
                        "A staged file changed length while it was copied.");
                }
                await output.FlushAsync(token);
            }

            sourceHash = hasher.GetHashAndReset();
            await using var stagedInput = new FileStream(
                destination,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] stagedHash = await SHA256.HashDataAsync(
                stagedInput,
                token);
            if (!CryptographicOperations.FixedTimeEquals(
                sourceHash,
                stagedHash))
            {
                throw new IOException(
                    "A staged file did not match its opened source handle.");
            }
        }
        File.SetLastWriteTimeUtc(destination, snapshot.LastWriteTimeUtc);
        File.SetAttributes(
            destination,
            snapshot.Attributes & ~FileAttributes.ReparsePoint);
    }

    private static SafeFileHandle OpenVerifiedEntry(
        string path,
        string sourceRoot,
        bool? expectDirectory)
    {
        SafeFileHandle handle = OpenHandle(
            path,
            FileReadData | FileReadAttributes,
            directory: true);
        try
        {
            FileSnapshot snapshot = Snapshot(handle);
            if ((snapshot.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Reparse points are not allowed in staged client inputs.");
            }
            if (expectDirectory is not null
                && snapshot.IsDirectory != expectDirectory.Value)
            {
                throw new InvalidOperationException(
                    "A staged source entry changed type while it was opened.");
            }
            string finalPath = FinalPath(handle);
            if (!IsWithin(sourceRoot, finalPath))
            {
                throw new InvalidOperationException(
                    "An opened staged source escaped the canonical client root.");
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenVerifiedDirectory(
        string path,
        string expectedPath)
    {
        SafeFileHandle handle = OpenHandle(
            path,
            FileReadAttributes,
            directory: true);
        try
        {
            FileSnapshot snapshot = Snapshot(handle);
            if (!snapshot.IsDirectory
                || (snapshot.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Source and staging directories cannot be reparse points.");
            }
            string finalPath = FinalPath(handle);
            if (!finalPath.Equals(
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "An opened source or staging directory resolved to a different path.");
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenHandle(
        string path,
        uint access,
        bool directory)
    {
        SafeFileHandle handle = CreateFile(
            path,
            access,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint
                | (directory ? FileFlagBackupSemantics : 0),
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3)
            {
                throw new DirectoryNotFoundException(
                    $"Unable to open a required staged directory: {path}");
            }
            throw new IOException(
                $"Unable to open a staged path without following reparse points: {path}",
                new Win32Exception(error));
        }
        return handle;
    }

    private static FileSnapshot Snapshot(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            throw new IOException(
                "Unable to inspect an opened staged path.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        long length = ((long)info.FileSizeHigh << 32)
            | info.FileSizeLow;
        long fileTime = ((long)info.LastWriteTimeHigh << 32)
            | info.LastWriteTimeLow;
        return new FileSnapshot(
            (FileAttributes)info.FileAttributes,
            length,
            DateTime.FromFileTimeUtc(fileTime));
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Capacity),
            0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new IOException(
                "Unable to resolve the final path of an opened staged entry.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        string path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }
        return Path.GetFullPath(path);
    }

    private static string CanonicalDirectory(string path)
    {
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
    }

    private static bool IsForbiddenFile(
        string name,
        bool allowClient)
    {
        string extension = Path.GetExtension(name);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && (!allowClient
                || !name.Equals("CLIENT.EXE", StringComparison.OrdinalIgnoreCase))
            || extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msix", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".appx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".t2capture", StringComparison.OrdinalIgnoreCase)
            || name.Contains("setup", StringComparison.OrdinalIgnoreCase)
            || name.Contains("installer", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("output", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForbiddenDirectory(string name) =>
        name.Equals("Pack", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Dump", StringComparison.OrdinalIgnoreCase)
        || name.Contains("setup", StringComparison.OrdinalIgnoreCase)
        || name.Contains("installer", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("output", StringComparison.OrdinalIgnoreCase);

    private static void RejectOverlap(string left, string right)
    {
        if (IsWithin(left, right) || IsWithin(right, left))
        {
            throw new InvalidOperationException(
                "Source and staging roots must not overlap.");
        }
    }

    private static bool IsWithin(string root, string candidate) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void ClearReadOnly(string stage)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(
            stage,
            "*",
            SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(
                    entry,
                    attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle handle,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed class PathLock : IDisposable
    {
        private readonly List<SafeFileHandle> handles;
        private PathLock(List<SafeFileHandle> handles) => this.handles = handles;

        public static PathLock Acquire(string path)
        {
            var components = new Stack<string>();
            for (DirectoryInfo? current = new(path);
                current is not null;
                current = current.Parent)
            {
                components.Push(current.FullName);
            }
            var handles = new List<SafeFileHandle>();
            try
            {
                while (components.TryPop(out string? component))
                {
                    handles.Add(OpenVerifiedDirectory(
                        component,
                        component));
                }
                return new PathLock(handles);
            }
            catch
            {
                foreach (SafeFileHandle handle in handles)
                {
                    handle.Dispose();
                }
                throw;
            }
        }

        public void Dispose()
        {
            for (int index = handles.Count - 1; index >= 0; index--)
            {
                handles[index].Dispose();
            }
        }
    }

    private sealed class CopyBudget
    {
        public long Total;
        public int Count;
    }

    private sealed record FileSnapshot(
        FileAttributes Attributes,
        long Length,
        DateTime LastWriteTimeUtc)
    {
        public bool IsDirectory =>
            (Attributes & FileAttributes.Directory) != 0;
    }
}
