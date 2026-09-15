using System.Text.Json;
using System.Text.Json.Serialization;
using Mister.Core.Diagnostics;

namespace Mister.Core.Extraction;

public static class ManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async ValueTask<ToolResult<string>> WriteAsync(
        string outputPath,
        ExtractionManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(manifest);

        string canonicalPath = Path.GetFullPath(outputPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractionManifest ordered = Order(manifest);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                ordered,
                JsonOptions);
            await using AtomicOutputFile output =
                await AtomicOutputFile.CreateAsync(
                    canonicalPath,
                    cancellationToken);
            await output.Stream.WriteAsync(bytes, cancellationToken);
            await output.PublishAsync(cancellationToken);
            return ToolResult<string>.Success(canonicalPath);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "Manifest writing was cancelled.",
                canonicalPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticCode code =
                File.Exists(canonicalPath) || Directory.Exists(canonicalPath)
                    ? DiagnosticCode.OutputCollision
                    : DiagnosticCode.OutputPermissionFailure;
            return Failure(
                code,
                $"The extraction manifest could not be published: {exception.Message}",
                canonicalPath);
        }
    }

    private static ExtractionManifest Order(ExtractionManifest manifest) =>
        manifest with
        {
            Entries = manifest.Entries
                .OrderBy(entry => entry.Ordinal)
                .ThenBy(entry => entry.ArchivePath, StringComparer.Ordinal)
                .Select(
                    entry => entry with
                    {
                        Diagnostics = Order(entry.Diagnostics)
                    })
                .ToArray(),
            Diagnostics = Order(manifest.Diagnostics)
        };

    private static ToolDiagnostic[] Order(
        IReadOnlyList<ToolDiagnostic> diagnostics) =>
        diagnostics
            .OrderBy(item => item.Code)
            .ThenBy(item => item.Subject, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .ToArray();

    private static ToolResult<string> Failure(
        DiagnosticCode code,
        string message,
        string subject) =>
        ToolResult<string>.Failure(new ToolDiagnostic(code, message, subject));

    internal static async ValueTask WriteCsvRowAsync(
        TextWriter writer,
        IReadOnlyList<string> fields,
        IReadOnlySet<int> untrustedTextFields)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(untrustedTextFields);
        await writer.WriteLineAsync(
            string.Join(
                ',',
                fields.Select(
                    (value, index) => EscapeCsv(
                        SpreadsheetSafe(
                            value,
                            untrustedTextFields.Contains(index))))))
            .ConfigureAwait(false);
    }

    // Prefixing formula-leading untrusted text with an apostrophe is the
    // spreadsheet-safe encoding used by Excel-compatible CSV consumers.
    // RFC-style quoting is applied afterwards for CSV structure.
    private static string SpreadsheetSafe(string value, bool untrusted) =>
        untrusted
            && value.Length > 0
            && value[0] is '=' or '+' or '-' or '@'
                ? $"'{value}"
                : value;

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
