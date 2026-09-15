using Mister.Core.Diagnostics;

namespace Mister.Core.T3;

public static class T3PakKeyResolver
{
    public static string BaseKeyName(string archiveName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);
        string fileName = Path.GetFileName(archiveName);
        string extension = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int suffixStart = stem.Length;
        while (suffixStart > 0 && char.IsAsciiDigit(stem[suffixStart - 1]))
        {
            suffixStart--;
        }

        return stem[..suffixStart] + extension;
    }

    public static ToolResult<byte[]> Resolve(
        string archiveName,
        string pakKeyRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pakKeyRoot);

        string fileName = Path.GetFileName(archiveName);
        try
        {
            if (!Directory.Exists(pakKeyRoot))
            {
                return Missing(fileName, pakKeyRoot);
            }

            string[] files = Directory.GetFiles(pakKeyRoot);
            ToolResult<string?> exact = FindUnique(files, fileName);
            if (!exact.IsSuccess)
            {
                return ToolResult<byte[]>.Failure(exact.Error!);
            }

            string? selected = exact.Value;
            if (selected is null)
            {
                string baseName = BaseKeyName(fileName);
                ToolResult<string?> baseKey = FindUnique(files, baseName);
                if (!baseKey.IsSuccess)
                {
                    return ToolResult<byte[]>.Failure(baseKey.Error!);
                }

                selected = baseKey.Value;
            }

            if (selected is null)
            {
                return Missing(fileName, pakKeyRoot);
            }

            byte[] bytes = File.ReadAllBytes(selected);
            if (bytes.Length != T3Constants.PakKeySize)
            {
                return ToolResult<byte[]>.Failure(
                    new ToolDiagnostic(
                        DiagnosticCode.PartialPakKey,
                        $"The supplied T3 pakkey must be exactly {T3Constants.PakKeySize} bytes; received {bytes.Length}.",
                        selected));
            }

            return ToolResult<byte[]>.Success(bytes);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return ToolResult<byte[]>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.MissingPakKey,
                    $"The supplied T3 pakkey could not be read: {exception.Message}",
                    pakKeyRoot));
        }
    }

    private static ToolResult<string?> FindUnique(
        IEnumerable<string> files,
        string name)
    {
        string[] matches = files
            .Where(path => string.Equals(
                Path.GetFileName(path),
                name,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
        {
            return ToolResult<string?>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.ConflictingPakKey,
                    $"Multiple supplied T3 pakkeys match {name}.",
                    name));
        }

        return ToolResult<string?>.Success(matches.SingleOrDefault());
    }

    private static ToolResult<byte[]> Missing(
        string fileName,
        string pakKeyRoot) =>
        ToolResult<byte[]>.Failure(
            new ToolDiagnostic(
                DiagnosticCode.MissingPakKey,
                $"No exact or numeric-overlay base pakkey matches {fileName}.",
                pakKeyRoot));
}
