using System.Globalization;
using System.Text.RegularExpressions;
using Mister.Core.Diagnostics;

namespace Mister.Core.Extraction;

public sealed record OutputPathCollision(
    string RequestedPath,
    string ExistingPath,
    string ResolvedPath,
    int Ordinal);

public sealed record ResolvedArchivePath(
    string FullPath,
    string RelativePath,
    OutputPathCollision? Collision);

public static partial class SafeArchivePath
{
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "CONIN$",
            "CONOUT$",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "COM\u00b9",
            "COM\u00b2",
            "COM\u00b3",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "LPT\u00b9",
            "LPT\u00b2",
            "LPT\u00b3"
        };

    public static ToolResult<string> Resolve(
        string outputRoot,
        string archivePath)
    {
        ToolResult<string[]> partsResult = ValidateAndSplit(archivePath);
        if (!partsResult.IsSuccess)
        {
            return ToolResult<string>.Failure(partsResult.Error!);
        }

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
            string canonicalRoot = Path.GetFullPath(outputRoot);
            string candidate = Path.GetFullPath(
                Path.Combine([canonicalRoot, .. partsResult.Value!]));
            string rootPrefix = Path.EndsInDirectorySeparator(canonicalRoot)
                ? canonicalRoot
                : canonicalRoot + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Unsafe(
                    archivePath,
                    "The archive path resolves outside the output root.");
            }

            return ToolResult<string>.Success(candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Unsafe(
                archivePath,
                $"The output path cannot be resolved safely: {exception.Message}");
        }
    }

    internal static ToolResult<string[]> ValidateAndSplit(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return ToolResult<string[]>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.UnsafeOutputPath,
                    "Archive output paths cannot be empty.",
                    archivePath));
        }

        if (archivePath[0] is '\\' or '/'
            || WindowsDrivePrefix().IsMatch(archivePath))
        {
            return ToolResult<string[]>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.UnsafeOutputPath,
                    "Rooted and drive-relative archive paths are not allowed.",
                    archivePath));
        }

        string[] parts = archivePath.Split(['\\', '/']);
        foreach (string part in parts)
        {
            if (part.Length == 0 || part is "." or "..")
            {
                return InvalidPart(archivePath, part);
            }

            if (part[^1] is ' ' or '.')
            {
                return InvalidPart(archivePath, part);
            }

            foreach (char character in part)
            {
                if (char.IsControl(character)
                    || character is '<' or '>' or ':' or '"'
                        or '|' or '?' or '*')
                {
                    return InvalidPart(archivePath, part);
                }
            }

            string deviceStem = part.Split('.', 2)[0].TrimEnd(' ');
            if (ReservedNames.Contains(deviceStem))
            {
                return InvalidPart(archivePath, part);
            }
        }

        return ToolResult<string[]>.Success(parts);
    }

    private static ToolResult<string[]> InvalidPart(
        string archivePath,
        string part) =>
        ToolResult<string[]>.Failure(
            new ToolDiagnostic(
                DiagnosticCode.UnsafeOutputPath,
                $"The archive path contains an unsafe Windows path component: '{part}'.",
                archivePath));

    private static ToolResult<string> Unsafe(
        string? subject,
        string message) =>
        ToolResult<string>.Failure(
            new ToolDiagnostic(
                DiagnosticCode.UnsafeOutputPath,
                message,
                subject));

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePrefix();
}

public sealed partial class SafeArchivePathRegistry
{
    private readonly string _outputRoot;
    private readonly Dictionary<string, string> _claimed =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public SafeArchivePathRegistry(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        _outputRoot = Path.GetFullPath(outputRoot);
    }

    public ToolResult<ResolvedArchivePath> Resolve(
        string archivePath,
        int ordinal)
    {
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                "Archive ordinals cannot be negative.");
        }

        ToolResult<string> safe =
            SafeArchivePath.Resolve(_outputRoot, archivePath);
        if (!safe.IsSuccess)
        {
            return ToolResult<ResolvedArchivePath>.Failure(safe.Error!);
        }

        string[] parts = SafeArchivePath.ValidateAndSplit(archivePath).Value!;
        string relativePath = string.Join('\\', parts);
        lock (_gate)
        {
            if (HasPrefixConflict(relativePath))
            {
                return PrefixConflict(relativePath);
            }

            if (_claimed.TryAdd(relativePath, relativePath))
            {
                return Success(safe.Value!, relativePath, null);
            }

            string existingPath = _claimed[relativePath];
            string collisionPath = AddOrdinalSuffix(relativePath, ordinal);
            int collisionOrdinal = 0;
            while (_claimed.ContainsKey(collisionPath)
                || HasPrefixConflict(collisionPath))
            {
                collisionOrdinal++;
                collisionPath = AddCollisionSuffix(
                    AddOrdinalSuffix(relativePath, ordinal),
                    collisionOrdinal);
            }

            ToolResult<string> collision =
                SafeArchivePath.Resolve(_outputRoot, collisionPath);
            if (!collision.IsSuccess)
            {
                return ToolResult<ResolvedArchivePath>.Failure(
                    collision.Error!);
            }

            _claimed.Add(collisionPath, collisionPath);
            var collisionDetails = new OutputPathCollision(
                relativePath,
                existingPath,
                collisionPath,
                ordinal);
            return Success(
                collision.Value!,
                collisionPath,
                collisionDetails);
        }
    }

    private static ToolResult<ResolvedArchivePath> Success(
        string fullPath,
        string relativePath,
        OutputPathCollision? collision) =>
        ToolResult<ResolvedArchivePath>.Success(
            new ResolvedArchivePath(fullPath, relativePath, collision));

    private static string AddOrdinalSuffix(string relativePath, int ordinal)
    {
        int separator = relativePath.LastIndexOf('\\');
        string directory = separator >= 0
            ? relativePath[..(separator + 1)]
            : string.Empty;
        string fileName = separator >= 0
            ? relativePath[(separator + 1)..]
            : relativePath;
        string extension = Path.GetExtension(fileName);
        string stem = fileName[..^extension.Length];
        stem = ExistingOrdinalSuffix().Replace(stem, string.Empty);
        string suffix = ordinal.ToString("D5", CultureInfo.InvariantCulture);
        return $"{directory}{stem}__ordinal_{suffix}{extension}";
    }

    private static string AddCollisionSuffix(
        string relativePath,
        int collisionOrdinal)
    {
        int separator = relativePath.LastIndexOf('\\');
        string directory = separator >= 0
            ? relativePath[..(separator + 1)]
            : string.Empty;
        string fileName = separator >= 0
            ? relativePath[(separator + 1)..]
            : relativePath;
        string extension = Path.GetExtension(fileName);
        string stem = fileName[..^extension.Length];
        string suffix = collisionOrdinal.ToString(
            "D5",
            CultureInfo.InvariantCulture);
        return $"{directory}{stem}__collision_{suffix}{extension}";
    }

    private bool HasPrefixConflict(string relativePath)
    {
        foreach (string claimedPath in _claimed.Keys)
        {
            if (IsProperPrefix(claimedPath, relativePath)
                || IsProperPrefix(relativePath, claimedPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProperPrefix(string prefix, string candidate) =>
        candidate.Length > prefix.Length
        && candidate.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase)
        && candidate[prefix.Length] == '\\';

    private static ToolResult<ResolvedArchivePath> PrefixConflict(
        string relativePath) =>
        ToolResult<ResolvedArchivePath>.Failure(
            new ToolDiagnostic(
                DiagnosticCode.OutputCollision,
                "The archive path conflicts with a claimed file/directory prefix.",
                relativePath));

    [GeneratedRegex(
        @"__ordinal_\d{5}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExistingOrdinalSuffix();
}
