using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mister.Windows.Sandbox;

[SupportedOSPlatform("windows")]
internal sealed class SandboxPathGuard : IDisposable
{
    private const uint FileListDirectory = 0x0001;
    private const uint FileReadData = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private readonly List<LockedPath> paths;
    private readonly string? treeRoot;
    private readonly HashSet<string>? treePaths;
    private bool disposed;

    private SandboxPathGuard(
        List<LockedPath> paths,
        string? treeRoot = null,
        HashSet<string>? treePaths = null)
    {
        this.paths = paths;
        this.treeRoot = treeRoot;
        this.treePaths = treePaths;
    }

    public static SandboxPathGuard LockDirectory(string directory)
    {
        string root = Canonical(directory);
        var paths = new List<LockedPath>();
        try
        {
            foreach (string component in Components(root))
            {
                paths.Add(OpenDirectory(component));
            }
            return new SandboxPathGuard(paths);
        }
        catch
        {
            DisposeAll(paths);
            throw;
        }
    }

    public static SandboxPathGuard LockTree(string root)
    {
        string canonicalRoot = Canonical(root);
        var paths = new List<LockedPath>();
        try
        {
            foreach (string component in Components(canonicalRoot))
            {
                paths.Add(OpenDirectory(component));
            }
            LockChildren(canonicalRoot, canonicalRoot, paths);
            var expected = paths
                .Select(static path => path.FinalPath)
                .Where(path => IsWithin(canonicalRoot, path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var guard = new SandboxPathGuard(
                paths,
                canonicalRoot,
                expected);
            guard.VerifyUnchanged();
            return guard;
        }
        catch
        {
            DisposeAll(paths);
            throw;
        }
    }

    public static LockedSandboxFile OpenBoundedFile(
        string root,
        string path,
        int maximumBytes)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes));
        }
        string canonicalRoot = Canonical(root);
        string expected = Path.GetFullPath(path);
        if (!IsWithin(canonicalRoot, expected))
        {
            throw new IOException(
                "A sandbox handoff escaped its verified result directory.");
        }
        LockedPath locked = OpenFile(expected);
        try
        {
            if (!locked.FinalPath.Equals(
                    expected,
                    StringComparison.OrdinalIgnoreCase)
                || locked.Snapshot.Length is < 1
                    or > int.MaxValue
                || locked.Snapshot.Length > maximumBytes)
            {
                throw new IOException(
                    "A sandbox handoff was empty, oversized, replaced, or redirected.");
            }
            return new LockedSandboxFile(
                locked,
                checked((int)locked.Snapshot.Length));
        }
        catch
        {
            locked.Dispose();
            throw;
        }
    }

    public void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (LockedPath path in paths)
        {
            path.Verify();
        }
        if (treeRoot is not null
            && treePaths is not null
            && !treePaths.SetEquals(
                EnumerateTreePaths(treeRoot)))
        {
            throw new IOException(
                "A locked sandbox mapping tree changed membership.");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        DisposeAll(paths);
    }

    private static void LockChildren(
        string root,
        string directory,
        List<LockedPath> paths)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(
            directory))
        {
            string expected = Path.GetFullPath(entry);
            if (!IsWithin(root, expected))
            {
                throw new IOException(
                    "A sandbox mapping entry escaped its verified root.");
            }
            FileAttributes attributes = File.GetAttributes(entry);
            LockedPath locked =
                (attributes & FileAttributes.Directory) != 0
                    ? OpenDirectory(expected)
                    : OpenFile(expected);
            paths.Add(locked);
            if (locked.Snapshot.IsDirectory)
            {
                LockChildren(root, expected, paths);
            }
        }
    }

    private static LockedPath OpenDirectory(string path) =>
        Open(
            path,
            FileListDirectory | FileReadAttributes,
            directory: true);

    private static LockedPath OpenFile(string path) =>
        Open(
            path,
            FileReadData | FileReadAttributes,
            directory: false);

    private static LockedPath Open(
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
            throw new IOException(
                $"Unable to lock sandbox path: {path}",
                new Win32Exception(error));
        }
        try
        {
            FileSnapshot snapshot = Snapshot(handle);
            if (snapshot.IsDirectory != directory
                || (snapshot.Attributes & FileAttributes.ReparsePoint)
                    != 0)
            {
                throw new IOException(
                    "Sandbox mappings and handoffs cannot be reparse points.");
            }
            string finalPath = FinalPath(handle);
            if (!finalPath.Equals(
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "A sandbox path resolved to a different object.");
            }
            return new LockedPath(
                handle,
                finalPath,
                snapshot);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static IEnumerable<string> Components(string path)
    {
        var components = new Stack<string>();
        for (DirectoryInfo? current = new(path);
            current is not null;
            current = current.Parent)
        {
            components.Push(current.FullName);
        }
        return components;
    }

    private static HashSet<string> EnumerateTreePaths(string root)
    {
        var paths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            root
        };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            foreach (string entry in
                Directory.EnumerateFileSystemEntries(directory))
            {
                string full = Path.GetFullPath(entry);
                paths.Add(full);
                FileAttributes attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.Directory) != 0
                    && (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(full);
                }
            }
        }
        return paths;
    }

    private static FileSnapshot Snapshot(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(
                handle,
                out ByHandleFileInformation information))
        {
            throw new IOException(
                "Unable to inspect a locked sandbox path.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return new FileSnapshot(
            (FileAttributes)information.FileAttributes,
            ((long)information.FileSizeHigh << 32)
                | information.FileSizeLow,
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32)
                | information.FileIndexLow);
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
                "Unable to resolve a locked sandbox path.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        string path = buffer.ToString();
        if (path.StartsWith(
            @"\\?\UNC\",
            StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(
            @"\\?\",
            StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }
        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
    }

    private static string Canonical(string path) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));

    private static bool IsWithin(
        string root,
        string candidate) =>
        candidate.Equals(
            root,
            StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void DisposeAll(IReadOnlyList<LockedPath> paths)
    {
        for (int index = paths.Count - 1; index >= 0; index--)
        {
            paths[index].Dispose();
        }
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
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

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
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

    internal sealed class LockedPath : IDisposable
    {
        public LockedPath(
            SafeFileHandle handle,
            string finalPath,
            FileSnapshot snapshot)
        {
            Handle = handle;
            FinalPath = finalPath;
            Snapshot = snapshot;
        }

        public SafeFileHandle Handle { get; }
        public string FinalPath { get; }
        public FileSnapshot Snapshot { get; }

        public void Verify()
        {
            FileSnapshot current =
                SandboxPathGuard.Snapshot(Handle);
            if (Snapshot.VolumeSerialNumber
                    != current.VolumeSerialNumber
                || Snapshot.FileIndex != current.FileIndex
                || Snapshot.IsDirectory != current.IsDirectory
                || (current.Attributes & FileAttributes.ReparsePoint)
                    != 0
                || !Snapshot.IsDirectory
                    && Snapshot.Length != current.Length
                || !FinalPath.Equals(
                    SandboxPathGuard.FinalPath(Handle),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "A locked sandbox path changed identity.");
            }
        }

        public void Dispose() => Handle.Dispose();
    }

    internal sealed record FileSnapshot(
        FileAttributes Attributes,
        long Length,
        uint VolumeSerialNumber,
        ulong FileIndex)
    {
        public bool IsDirectory =>
            (Attributes & FileAttributes.Directory) != 0;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class LockedSandboxFile : IDisposable
{
    private readonly SandboxPathGuard.LockedPath locked;
    private readonly int length;
    private bool disposed;

    internal LockedSandboxFile(
        SandboxPathGuard.LockedPath locked,
        int length)
    {
        this.locked = locked;
        this.length = length;
    }

    internal SafeFileHandle Handle => locked.Handle;
    internal int Length => length;

    public async Task<byte[]> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] content = new byte[length];
        int offset = 0;
        while (offset < content.Length)
        {
            int read = await RandomAccess.ReadAsync(
                locked.Handle,
                content.AsMemory(offset),
                offset,
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "A locked sandbox handoff ended during its bounded read.");
            }
            offset += read;
        }
        locked.Verify();
        if (RandomAccess.GetLength(locked.Handle) != length)
        {
            throw new IOException(
                "A locked sandbox handoff changed length.");
        }
        return content;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        locked.Dispose();
    }
}
