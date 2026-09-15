using System.Text.Json;
using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Formats;

namespace Mister.Core.T3;

public sealed record PakKeyExport(
    string PakKeyPath,
    string CoverageJsonPath);

public static class T3PakKeyExporter
{
    public static byte[] BuildBytes(PakKeyCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (!coverage.IsUsable)
        {
            throw new InvalidOperationException(
                "T3 pakkey export requires unique recovery for every consulted index.");
        }

        byte[] bytes = new byte[T3Constants.PakKeySize];
        foreach ((int index, byte value) in coverage.Recovered)
        {
            bytes[index] = value;
        }

        return bytes;
    }

    public static async ValueTask<ToolResult<PakKeyExport>> ExportAsync(
        PakKeyCoverage coverage,
        ClientEvidence client,
        string exportDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);

        if (!coverage.IsUsable)
        {
            DiagnosticCode code = coverage.HasConflicts
                ? DiagnosticCode.ConflictingPakKey
                : DiagnosticCode.PartialPakKey;
            return Failure(
                code,
                "T3 pakkey export is blocked until every consulted index has one unique recovered byte.",
                exportDirectory);
        }

        if (client.Family != GameFamily.Technika3)
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                "T3 pakkey export requires Technika 3 client evidence.",
                client.ClientPath);
        }

        string outputRoot = Path.GetFullPath(exportDirectory);
        string pakKeyPath = Path.Combine(
            outputRoot,
            Path.GetFileName(coverage.FamilyName));
        string coverageJsonPath =
            Path.ChangeExtension(pakKeyPath, ".coverage.json");
        string transactionId = Guid.NewGuid().ToString("N");
        string pakKeyPartPath =
            $"{pakKeyPath}.{transactionId}.part";
        string coverageJsonPartPath =
            $"{coverageJsonPath}.{transactionId}.part";
        bool pakKeyPublished = false;
        bool coverageJsonPublished = false;
        bool committed = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputRoot);
            if (File.Exists(pakKeyPath)
                || Directory.Exists(pakKeyPath)
                || File.Exists(coverageJsonPath)
                || Directory.Exists(coverageJsonPath))
            {
                return Failure(
                    DiagnosticCode.OutputCollision,
                    "The recovered T3 pakkey or coverage report already exists.",
                    outputRoot);
            }

            byte[] keyBytes = BuildBytes(coverage);
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
                BuildReport(coverage, client),
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
            await File.WriteAllBytesAsync(
                coverageJsonPartPath,
                jsonBytes,
                cancellationToken);
            await File.WriteAllBytesAsync(
                pakKeyPartPath,
                keyBytes,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            File.Move(
                coverageJsonPartPath,
                coverageJsonPath,
                overwrite: false);
            coverageJsonPublished = true;
            File.Move(
                pakKeyPartPath,
                pakKeyPath,
                overwrite: false);
            pakKeyPublished = true;
            committed = true;
            return ToolResult<PakKeyExport>.Success(
                new PakKeyExport(pakKeyPath, coverageJsonPath));
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "T3 pakkey export was cancelled.",
                outputRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                DiagnosticCode.OutputPermissionFailure,
                $"The recovered T3 pakkey could not be exported: {exception.Message}",
                outputRoot);
        }
        finally
        {
            TryDeleteFile(pakKeyPartPath);
            TryDeleteFile(coverageJsonPartPath);
            if (!committed)
            {
                if (pakKeyPublished)
                {
                    TryDeleteFile(pakKeyPath);
                }

                if (coverageJsonPublished)
                {
                    TryDeleteFile(coverageJsonPath);
                }
            }
        }
    }

    private static object BuildReport(
        PakKeyCoverage coverage,
        ClientEvidence client)
    {
        var coveredArchives = coverage.Evidence
            .Select(item => item.ArchivePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourcePaks = coverage.Evidence
            .GroupBy(
                item => item.ArchivePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                ArchivePath = group.Key,
                Sha256 = group.Select(item => item.ArchiveSha256)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Single()
            })
            .OrderBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var indexes = Enumerable
            .Range(0, T3Constants.PakKeySize)
            .Select(index => new
            {
                Index = index,
                RecoveredValue = coverage.Recovered.TryGetValue(
                    index,
                    out byte value)
                    ? (int?)value
                    : null,
                EvidenceCount = coverage.Evidence.Count(
                    item => item.KeyIndex == index)
            })
            .ToArray();
        var conflicts = coverage.Conflicts
            .OrderBy(item => item.Key)
            .Select(item => new
            {
                Index = item.Key,
                Candidates = item.Value.Order().Select(value => (int)value),
                Evidence = coverage.Evidence
                    .Where(evidence => evidence.KeyIndex == item.Key)
                    .OrderBy(evidence => evidence.ArchivePath)
                    .ThenBy(evidence => evidence.Ordinal)
                    .ThenBy(evidence => evidence.Candidate)
            })
            .ToArray();

        return new
        {
            coverage.FamilyName,
            SourceClientSha256 = client.ClientSha256,
            SourcePaks = sourcePaks,
            Indexes = indexes,
            UnresolvedEffectiveIndexes =
                coverage.UnresolvedEffectiveIndexes.Order(),
            UnconsultedIndexes = coverage.UnconsultedIndexes.Order(),
            Conflicts = conflicts,
            CoveredArchives = coveredArchives,
            coverage.IsUsable,
            coverage.HasCompleteEffectiveCoverage
        };
    }

    private static ToolResult<PakKeyExport> Failure(
        DiagnosticCode code,
        string message,
        string subject) =>
        ToolResult<PakKeyExport>.Failure(
            new ToolDiagnostic(code, message, subject));

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
