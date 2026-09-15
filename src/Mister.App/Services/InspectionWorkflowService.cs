using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Extraction;
using Mister.Core.Formats;
using Mister.Core.Preview;
using Mister.Core.T2;
using Mister.Core.T3;

namespace Mister.App.Services;

internal interface IInspectionWorkflowService
{
    ValueTask<EntryPreview> PreviewAsync(
        ArchiveIndex archive,
        ArchiveEntry entry,
        ClientEvidence evidence,
        CancellationToken cancellationToken);

    ValueTask<ToolResult<PlaybackFile>> MaterializePlaybackAsync(
        ArchiveIndex archive,
        ArchiveEntry entry,
        ClientEvidence evidence,
        string outputDirectory,
        CancellationToken cancellationToken);

    ValueTask<ExtractionSummary> ExtractAsync(
        IReadOnlyList<ArchiveIndex> archives,
        ClientEvidence evidence,
        string outputDirectory,
        bool buildMergedView,
        IProgress<ExtractionProgress> progress,
        CancellationToken cancellationToken);

    ValueTask<ToolResult<PakKeyCoverage>> RecoverPakKeyAsync(
        IReadOnlyList<string> archives,
        ClientEvidence evidence,
        CancellationToken cancellationToken);

    ValueTask<ToolResult<PakKeyExport>> ExportPakKeyAsync(
        PakKeyCoverage coverage,
        ClientEvidence evidence,
        string outputDirectory,
        CancellationToken cancellationToken);
}

internal sealed class InspectionWorkflowService : IInspectionWorkflowService
{
    public ValueTask<EntryPreview> PreviewAsync(
        ArchiveIndex archive,
        ArchiveEntry entry,
        ClientEvidence evidence,
        CancellationToken cancellationToken) =>
        new ArchivePreviewService(Decoders(evidence))
            .PreviewEntryAsync(archive, entry, cancellationToken);

    public ValueTask<ToolResult<PlaybackFile>> MaterializePlaybackAsync(
        ArchiveIndex archive,
        ArchiveEntry entry,
        ClientEvidence evidence,
        string outputDirectory,
        CancellationToken cancellationToken) =>
        PlaybackMaterializer.MaterializeAsync(
            Decoders(evidence)[archive.Family],
            archive.ArchivePath,
            entry,
            outputDirectory,
            cancellationToken);

    public ValueTask<ExtractionSummary> ExtractAsync(
        IReadOnlyList<ArchiveIndex> archives,
        ClientEvidence evidence,
        string outputDirectory,
        bool buildMergedView,
        IProgress<ExtractionProgress> progress,
        CancellationToken cancellationToken) =>
        new ArchiveExtractor(Decoders(evidence)).ExtractAsync(
            archives,
            new ExtractionOptions(outputDirectory, buildMergedView)
            {
                InputPaths = archives.Select(static archive => archive.ArchivePath).ToArray()
            },
            progress,
            cancellationToken);

    public ValueTask<ToolResult<PakKeyCoverage>> RecoverPakKeyAsync(
        IReadOnlyList<string> archives,
        ClientEvidence evidence,
        CancellationToken cancellationToken) =>
        new T3PakKeyRecovery().RecoverFamilyAsync(
            archives, evidence, cancellationToken);

    public ValueTask<ToolResult<PakKeyExport>> ExportPakKeyAsync(
        PakKeyCoverage coverage,
        ClientEvidence evidence,
        string outputDirectory,
        CancellationToken cancellationToken) =>
        T3PakKeyExporter.ExportAsync(
            coverage, evidence, outputDirectory, cancellationToken);

    private static IReadOnlyDictionary<GameFamily, IPayloadDecoder> Decoders(
        ClientEvidence evidence) =>
        evidence.Family switch
        {
            GameFamily.Technika2 => new Dictionary<GameFamily, IPayloadDecoder>
            {
                [GameFamily.Technika2] = new T2PayloadDecoder(evidence)
            },
            GameFamily.Technika3 => new Dictionary<GameFamily, IPayloadDecoder>
            {
                [GameFamily.Technika3] = new T3PayloadDecoder()
            },
            _ => throw new ArgumentException(
                "Preview and extraction require validated T2 or T3 evidence.",
                nameof(evidence))
        };
}
