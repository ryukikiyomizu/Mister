namespace Mister.App.Services;

public interface IInputDiscoveryService
{
    IEnumerable<string> DiscoverAdjacentPacks(string clientPath);

    IEnumerable<string> DiscoverPaksInDirectory(string directory);
}

public enum DroppedInputKind
{
    Unsupported,
    Client,
    Pak,
    PakFolder,
    PakKeyDirectory,
    CaptureBundle
}

public sealed class InputDiscoveryService : IInputDiscoveryService
{
    private readonly Func<string, FileAttributes> getAttributes;

    public InputDiscoveryService(Func<string, FileAttributes>? getAttributes = null)
    {
        this.getAttributes = getAttributes ?? File.GetAttributes;
    }

    public IEnumerable<string> DiscoverAdjacentPacks(string clientPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);
        string fullClientPath = Path.GetFullPath(clientPath);
        string? clientDirectory = Path.GetDirectoryName(fullClientPath);
        if (!IsSafeDirectory(clientDirectory)) return [];

        var candidates = new List<string>();
        AddPaks(clientDirectory!, candidates);
        foreach (string directory in EnumerateDirectories(clientDirectory!))
        {
            if (string.Equals(Path.GetFileName(directory), "Pack", StringComparison.OrdinalIgnoreCase))
            {
                AddPaks(directory, candidates);
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IEnumerable<string> DiscoverPaksInDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string fullDirectory = Path.GetFullPath(directory);
        if (!IsSafeDirectory(fullDirectory)) return [];
        var candidates = new List<string>();
        AddPaks(fullDirectory, candidates);
        foreach (string child in EnumerateDirectories(fullDirectory))
        {
            if (string.Equals(
                    Path.GetFileName(child),
                    "Pack",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddPaks(child, candidates);
            }
        }
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static DroppedInputKind ClassifyDropPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Directory.Exists(path)) return DirectoryContainsPaks(path)
            ? DroppedInputKind.PakFolder
            : DroppedInputKind.PakKeyDirectory;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".exe" => DroppedInputKind.Client,
            ".pak" => DroppedInputKind.Pak,
            ".ttcapture" => DroppedInputKind.CaptureBundle,
            _ => DroppedInputKind.Unsupported
        };
    }

    public static bool DirectoryContainsPaks(string directory) =>
        new InputDiscoveryService().DiscoverPaksInDirectory(directory).Any();

    private bool IsSafeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        try
        {
            return (getAttributes(directory) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IEnumerable<string> EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory)
                .Where(IsSafeDirectory)
                .Where(path => IsDescendantOf(directory, path))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void AddPaks(string directory, ICollection<string> candidates)
    {
        if (!IsSafeDirectory(directory)) return;
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                if (!IsDescendantOf(directory, path) || IsReparsePoint(path)) continue;
                if (string.Equals(Path.GetExtension(path), ".pak", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.GetFullPath(path));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Input discovery is advisory. A protected or changing folder is skipped.
        }
    }

    private bool IsReparsePoint(string path)
    {
        try
        {
            return (getAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsDescendantOf(string root, string candidate)
    {
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
