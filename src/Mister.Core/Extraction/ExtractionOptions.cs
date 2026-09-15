using Mister.Core.Diagnostics;

namespace Mister.Core.Extraction;

public sealed record ExtractionOptions(
    string OutputRoot,
    bool BuildMergedView)
{
    public IReadOnlyList<string> InputPaths { get; init; } = [];

    public static ToolResult<ExtractionOptions> Validate(
        string outputRoot,
        bool buildMergedView,
        IReadOnlyList<string> inputPaths)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        try
        {
            string output = Canonicalize(outputRoot);
            if (File.Exists(output))
            {
                return Unsafe(outputRoot);
            }

            var inputs = new string[inputPaths.Count];
            for (int index = 0; index < inputPaths.Count; index++)
            {
                string input = Canonicalize(inputPaths[index]);
                inputs[index] = input;

                bool equal = PathsEqual(output, input);
                bool outputInsideInputDirectory =
                    Directory.Exists(input)
                    && IsSameOrDescendant(output, input);
                bool inputInsideOutput =
                    IsSameOrDescendant(input, output);
                if (equal
                    || outputInsideInputDirectory
                    || inputInsideOutput)
                {
                    return Unsafe(outputRoot);
                }
            }

            return ToolResult<ExtractionOptions>.Success(
                new ExtractionOptions(output, buildMergedView)
                {
                    InputPaths = Array.AsReadOnly(inputs)
                });
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException)
        {
            return ToolResult<ExtractionOptions>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.UnsafeOutputPath,
                    $"Output and input paths could not be canonicalized: {exception.Message}",
                    outputRoot));
        }
    }

    internal static string Canonicalize(string path)
    {
        return Canonicalize(
            path,
            static candidate =>
                Directory.Exists(candidate) || File.Exists(candidate),
            static candidate =>
            {
                FileSystemInfo existing = Directory.Exists(candidate)
                    ? new DirectoryInfo(candidate)
                    : new FileInfo(candidate);
                return existing.ResolveLinkTarget(returnFinalTarget: true)
                    ?.FullName;
            });
    }

    internal static string Canonicalize(
        string path,
        Func<string, bool> exists,
        Func<string, string?> resolveFinalTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(resolveFinalTarget);
        string fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        string remainder = fullPath[root.Length..];
        string current = root;
        string[] components = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < components.Length; index++)
        {
            string candidate = Path.Combine(current, components[index]);
            if (!exists(candidate))
            {
                for (; index < components.Length; index++)
                {
                    current = Path.Combine(current, components[index]);
                }

                break;
            }

            string? target = resolveFinalTarget(candidate);
            current = target is null
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(target);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    internal static ToolResult<string> ValidateDestination(
        string canonicalOutputRoot,
        IReadOnlyList<string> canonicalInputPaths,
        string destination)
    {
        try
        {
            string canonicalDestination = Canonicalize(destination);
            if (!IsSameOrDescendant(
                    canonicalDestination,
                    canonicalOutputRoot))
            {
                return UnsafeDestination(destination);
            }

            foreach (string input in canonicalInputPaths)
            {
                if (IsSameOrDescendant(canonicalDestination, input)
                    || IsSameOrDescendant(input, canonicalDestination))
                {
                    return UnsafeDestination(destination);
                }
            }

            return ToolResult<string>.Success(canonicalDestination);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException)
        {
            return ToolResult<string>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.UnsafeOutputPath,
                    $"The destination path could not be canonicalized safely: {exception.Message}",
                    destination));
        }
    }

    internal static bool IsSameOrDescendant(
        string candidate,
        string directory)
    {
        if (PathsEqual(candidate, directory))
        {
            return true;
        }

        string prefix = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return candidate.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right));

    private static ToolResult<ExtractionOptions> Unsafe(string outputRoot) =>
        ToolResult<ExtractionOptions>.Failure(
            new ToolDiagnostic(
                DiagnosticCode.UnsafeOutputPath,
                "Output overlaps an input path.",
                outputRoot));

    private static ToolResult<string> UnsafeDestination(
        string destination) =>
        ToolResult<string>.Failure(
            new ToolDiagnostic(
                DiagnosticCode.UnsafeOutputPath,
                "The destination resolves outside the selected output or overlaps an input.",
                destination));
}
