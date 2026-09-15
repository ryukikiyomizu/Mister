using Mister.Core.Diagnostics;

namespace Mister.Core.Extraction;

public sealed record ExtractionProgress(
    string ArchiveName,
    int EntryOrdinal,
    int EntryCount,
    long CompletedDecodedBytes,
    long TotalDecodedBytes,
    int FailureCount);

public enum ExtractionStatus
{
    Completed,
    CompletedWithFailures,
    Cancelled
}

public sealed record ExtractionSummary(
    ExtractionStatus Status,
    int ExtractedEntries,
    int FailedEntries,
    long DecodedBytes,
    string ManifestPath,
    string FailuresPath,
    string SummaryPath)
{
    public IReadOnlyList<ToolDiagnostic> Diagnostics { get; init; } = [];
}
