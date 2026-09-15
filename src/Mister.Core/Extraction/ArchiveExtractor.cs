using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mister.Core.Diagnostics;
using Mister.Core.Formats;

namespace Mister.Core.Extraction;

internal enum ExtractionReportKind
{
    Manifest,
    Failures,
    Summary
}

internal enum ExtractionReportStage
{
    Create,
    Append,
    Flush,
    Close,
    Publish
}

internal interface IExtractionReportObserver
{
    void OnStage(
        ExtractionReportKind kind,
        ExtractionReportStage stage);
}

public sealed class ArchiveExtractor
{
    private const string ManifestFileName = "_decrypted_file_manifest.csv";
    private const string FailuresFileName = "_failed_entries.csv";
    private const string SummaryFileName = "_extraction_summary.json";

    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IReadOnlyDictionary<GameFamily, IPayloadDecoder> _decoders;
    private readonly IExtractionReportObserver? _reportObserver;

    public ArchiveExtractor(
        IReadOnlyDictionary<GameFamily, IPayloadDecoder> decoders)
        : this(decoders, reportObserver: null)
    {
    }

    internal ArchiveExtractor(
        IReadOnlyDictionary<GameFamily, IPayloadDecoder> decoders,
        IExtractionReportObserver? reportObserver)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        _decoders = new Dictionary<GameFamily, IPayloadDecoder>(decoders);
        _reportObserver = reportObserver;
    }

    public async ValueTask<ExtractionSummary> ExtractAsync(
        IReadOnlyList<ArchiveIndex> archives,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(options);

        string[] allInputs = options.InputPaths
            .Concat(archives.Select(archive => archive.ArchivePath))
            .ToArray();
        ToolResult<ExtractionOptions> validated = ExtractionOptions.Validate(
            options.OutputRoot,
            options.BuildMergedView,
            allInputs);
        if (!validated.IsSuccess)
        {
            throw new ArgumentException(
                validated.Error!.Message,
                nameof(options));
        }

        string outputRoot = validated.Value!.OutputRoot;
        string manifestPath = Path.Combine(outputRoot, ManifestFileName);
        string failuresPath = Path.Combine(outputRoot, FailuresFileName);
        string summaryPath = Path.Combine(outputRoot, SummaryFileName);
        Directory.CreateDirectory(outputRoot);

        var reportDiagnostics = new List<ToolDiagnostic>();
        int extractedEntries = 0;
        int failedEntries = 0;
        long decodedBytes = 0;
        long totalDecodedBytes = SumDeclaredBytes(archives);
        bool cancelled = cancellationToken.IsCancellationRequested;

        await using IncrementalReports reports =
            await IncrementalReports.CreateAsync(
                manifestPath,
                failuresPath,
                outputRoot,
                validated.Value.InputPaths,
                _reportObserver).ConfigureAwait(false);

        var archivePathRegistry = new SafeArchivePathRegistry(
            Path.Combine(outputRoot, "by_pack"));
        try
        {
            for (int archiveIndex = 0;
                 archiveIndex < archives.Count && !cancelled;
                 archiveIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                ArchiveIndex archive = archives[archiveIndex];
                string archiveName = Path.GetFileName(archive.ArchivePath);
                progress?.Report(
                    new ExtractionProgress(
                        archiveName,
                        EntryOrdinal: -1,
                        archive.Entries.Count,
                        decodedBytes,
                        totalDecodedBytes,
                        failedEntries));

                if (!_decoders.TryGetValue(
                        archive.Family,
                        out IPayloadDecoder? decoder))
                {
                    ToolDiagnostic diagnostic = new(
                        DiagnosticCode.UnknownPakFormat,
                        $"No payload decoder is registered for {archive.Family}.",
                        archive.ArchivePath);
                    cancelled = await RecordArchiveFailuresAsync(
                        archive,
                        archiveName,
                        sourceSha256: string.Empty,
                        diagnostic).ConfigureAwait(false);
                    if (cancelled)
                    {
                        break;
                    }

                    continue;
                }

                string sourceSha256;
                try
                {
                    sourceSha256 = await FileHasher.Sha256Async(
                        archive.ArchivePath,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception exception) when (IsEntryException(exception))
                {
                    ToolDiagnostic diagnostic = DiagnosticFor(
                        exception,
                        archive.ArchivePath);
                    cancelled = await RecordArchiveFailuresAsync(
                        archive,
                        archiveName,
                        sourceSha256: string.Empty,
                        diagnostic).ConfigureAwait(false);
                    if (cancelled)
                    {
                        break;
                    }

                    continue;
                }

                ToolResult<ResolvedArchivePath> archiveDirectoryResult =
                    archivePathRegistry.Resolve(archiveName, archiveIndex);
                if (!archiveDirectoryResult.IsSuccess)
                {
                    ToolDiagnostic diagnostic =
                        archiveDirectoryResult.Error!;
                    cancelled = await RecordArchiveFailuresAsync(
                        archive,
                        archiveName,
                        sourceSha256,
                        diagnostic).ConfigureAwait(false);
                    if (cancelled)
                    {
                        break;
                    }

                    continue;
                }

                ResolvedArchivePath archiveDirectory =
                    archiveDirectoryResult.Value!;
                var entryPathRegistry = new SafeArchivePathRegistry(
                    archiveDirectory.FullPath);
                foreach (ArchiveEntry entry in archive.Entries)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    ToolResult<ResolvedArchivePath> outputResult =
                        entryPathRegistry.Resolve(
                            entry.DecodedPath,
                            entry.Ordinal);
                    if (!outputResult.IsSuccess)
                    {
                        failedEntries++;
                        ToolDiagnostic diagnostic = outputResult.Error!;
                        await reports.AppendFailureAsync(
                            archive,
                            entry,
                            outputPath: string.Empty,
                            diagnostic).ConfigureAwait(false);
                        await reports.AppendManifestAsync(
                            ManifestRow.Failed(
                                archive,
                                sourceSha256,
                                entry,
                                outputPath: string.Empty,
                                diagnostic)).ConfigureAwait(false);
                        ReportProgress();
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        continue;
                    }

                    ResolvedArchivePath resolved = outputResult.Value!;
                    string relativeOutputPath = JoinWindowsPath(
                        "by_pack",
                        archiveDirectory.RelativePath,
                        resolved.RelativePath);
                    try
                    {
                        ToolResult<string> safeDestination =
                            ExtractionOptions.ValidateDestination(
                                outputRoot,
                                validated.Value.InputPaths,
                                resolved.FullPath);
                        if (!safeDestination.IsSuccess)
                        {
                            throw new EntryDiagnosticException(
                                safeDestination.Error!);
                        }

                        await using AtomicOutputFile output =
                            await AtomicOutputFile.CreateAsync(
                                safeDestination.Value!,
                                cancellationToken).ConfigureAwait(false);
                        using var observer =
                            new ObservingWriteStream(output.Stream);
                        PayloadResult decoderResult =
                            await decoder.DecodeAsync(
                                archive.ArchivePath,
                                entry,
                                observer,
                                cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();

                        ObservedPayload observed = observer.Complete();
                        ToolDiagnostic? verificationFailure =
                            VerifyDecoderResult(
                                entry,
                                decoderResult,
                                observed);
                        if (verificationFailure is not null)
                        {
                            throw new EntryDiagnosticException(
                                verificationFailure);
                        }

                        await output.Stream.FlushAsync(cancellationToken)
                            .ConfigureAwait(false);
                        ToolResult<SignatureVerification> signature;
                        await using (var payload = new FileStream(
                            output.PartPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            bufferSize: 64 * 1024,
                            FileOptions.Asynchronous
                                | FileOptions.SequentialScan))
                        {
                            signature = await SignatureVerifier.VerifyAsync(
                                    entry.DecodedPath,
                                    payload,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        if (!signature.IsSuccess)
                        {
                            throw new EntryDiagnosticException(
                                signature.Error!);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        await output.PublishAsync(cancellationToken)
                            .ConfigureAwait(false);

                        extractedEntries++;
                        decodedBytes = SaturatingAdd(
                            decodedBytes,
                            observed.DecodedBytes);
                        var manifestEntry = new ManifestEntry(
                            entry.Ordinal,
                            entry.DecodedPath,
                            relativeOutputPath,
                            entry.StoredSize,
                            observed.DecodedBytes,
                            observed.Sha256,
                            signature.Value!.Signature,
                            [],
                            resolved.Collision);
                        await reports.AppendManifestAsync(
                            ManifestRow.Completed(
                                archive,
                                sourceSha256,
                                entry,
                                manifestEntry)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    catch (EntryDiagnosticException exception)
                    {
                        failedEntries++;
                        await reports.AppendFailureAsync(
                            archive,
                            entry,
                            relativeOutputPath,
                            exception.Diagnostic).ConfigureAwait(false);
                        await reports.AppendManifestAsync(
                            ManifestRow.Failed(
                                archive,
                                sourceSha256,
                                entry,
                                relativeOutputPath,
                                exception.Diagnostic,
                                resolved.Collision)).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        IsEntryException(exception))
                    {
                        failedEntries++;
                        ToolDiagnostic diagnostic = DiagnosticFor(
                            exception,
                            relativeOutputPath);
                        await reports.AppendFailureAsync(
                            archive,
                            entry,
                            relativeOutputPath,
                            diagnostic).ConfigureAwait(false);
                        await reports.AppendManifestAsync(
                            ManifestRow.Failed(
                                archive,
                                sourceSha256,
                                entry,
                                relativeOutputPath,
                                diagnostic,
                                resolved.Collision)).ConfigureAwait(false);
                    }

                    ReportProgress();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    void ReportProgress() =>
                        progress?.Report(
                            new ExtractionProgress(
                                archiveName,
                                entry.Ordinal,
                                archive.Entries.Count,
                                decodedBytes,
                                totalDecodedBytes,
                                failedEntries));
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        cancelled |= cancellationToken.IsCancellationRequested;
        await reports.PublishManifestAsync().ConfigureAwait(false);
        cancelled |= cancellationToken.IsCancellationRequested;
        await reports.AppendPendingReportFailuresAsync().ConfigureAwait(false);
        cancelled |= cancellationToken.IsCancellationRequested;
        await reports.PublishFailuresAsync().ConfigureAwait(false);
        cancelled |= cancellationToken.IsCancellationRequested;
        reportDiagnostics.AddRange(reports.Diagnostics);
        bool canPublishSummary = true;
        try
        {
            _reportObserver?.OnStage(
                ExtractionReportKind.Summary,
                ExtractionReportStage.Create);
        }
        catch (Exception exception) when (
            IsContainableReportException(exception))
        {
            canPublishSummary = false;
            reportDiagnostics.Add(
                ReportDiagnostic(exception, summaryPath));
        }

        cancelled |= cancellationToken.IsCancellationRequested;

        ExtractionStatus status = cancelled
            ? ExtractionStatus.Cancelled
            : failedEntries > 0 || reportDiagnostics.Count > 0
                ? ExtractionStatus.CompletedWithFailures
                : ExtractionStatus.Completed;
        var summary = new ExtractionSummary(
            status,
            extractedEntries,
            failedEntries,
            decodedBytes,
            manifestPath,
            failuresPath,
            summaryPath)
        {
            Diagnostics = reportDiagnostics.ToArray()
        };

        SummaryPublication summaryPublication = canPublishSummary
            ? await PublishSummaryAsync(summary).ConfigureAwait(false)
            : new SummaryPublication(summary, reportDiagnostics[^1]);
        summary = summaryPublication.Summary;
        cancelled |= summary.Status == ExtractionStatus.Cancelled;
        ToolDiagnostic? summaryFailure = summaryPublication.Diagnostic;
        if (summaryFailure is not null && canPublishSummary)
        {
            reportDiagnostics.Add(summaryFailure);
            summary = summary with
            {
                Status = cancelled
                    ? ExtractionStatus.Cancelled
                    : ExtractionStatus.CompletedWithFailures,
                Diagnostics = reportDiagnostics.ToArray()
            };
        }

        return summary;

        async ValueTask<bool> RecordArchiveFailuresAsync(
            ArchiveIndex archive,
            string archiveName,
            string sourceSha256,
            ToolDiagnostic diagnostic)
        {
            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return true;
                }

                failedEntries++;
                await reports.AppendFailureAsync(
                    archive,
                    entry,
                    outputPath: string.Empty,
                    diagnostic).ConfigureAwait(false);
                await reports.AppendManifestAsync(
                    ManifestRow.Failed(
                        archive,
                        sourceSha256,
                        entry,
                        outputPath: string.Empty,
                        diagnostic)).ConfigureAwait(false);
                progress?.Report(
                    new ExtractionProgress(
                        archiveName,
                        entry.Ordinal,
                        archive.Entries.Count,
                        decodedBytes,
                        totalDecodedBytes,
                        failedEntries));
                if (cancellationToken.IsCancellationRequested)
                {
                    return true;
                }
            }

            return false;
        }

        async ValueTask<SummaryPublication> PublishSummaryAsync(
            ExtractionSummary value)
        {
            ExtractionSummary current = value;
            try
            {
                ToolResult<string> safeDestination =
                    ExtractionOptions.ValidateDestination(
                        outputRoot,
                        validated.Value.InputPaths,
                        summaryPath);
                if (!safeDestination.IsSuccess)
                {
                    return new SummaryPublication(
                        ObserveSummaryCancellation(current),
                        safeDestination.Error!);
                }

                await using AtomicOutputFile output =
                    await AtomicOutputFile.CreateAsync(
                        safeDestination.Value!,
                        CancellationToken.None).ConfigureAwait(false);
                _reportObserver?.OnStage(
                    ExtractionReportKind.Summary,
                    ExtractionReportStage.Append);
                current = ObserveSummaryCancellation(current);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                    current,
                    SummaryJsonOptions);
                await output.Stream.WriteAsync(bytes, CancellationToken.None)
                    .ConfigureAwait(false);
                _reportObserver?.OnStage(
                    ExtractionReportKind.Summary,
                    ExtractionReportStage.Flush);
                current = await RefreshCancelledSummaryAsync(
                    output,
                    current).ConfigureAwait(false);
                await output.Stream.FlushAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                _reportObserver?.OnStage(
                    ExtractionReportKind.Summary,
                    ExtractionReportStage.Close);
                current = await RefreshCancelledSummaryAsync(
                    output,
                    current).ConfigureAwait(false);
                _reportObserver?.OnStage(
                    ExtractionReportKind.Summary,
                    ExtractionReportStage.Publish);
                current = await RefreshCancelledSummaryAsync(
                    output,
                    current).ConfigureAwait(false);
                await output.PublishAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                return new SummaryPublication(current, null);
            }
            catch (Exception exception) when (
                IsContainableReportException(exception))
            {
                return new SummaryPublication(
                    ObserveSummaryCancellation(current),
                    ReportDiagnostic(exception, summaryPath));
            }

            ExtractionSummary ObserveSummaryCancellation(
                ExtractionSummary candidate) =>
                cancellationToken.IsCancellationRequested
                    && candidate.Status != ExtractionStatus.Cancelled
                    ? candidate with { Status = ExtractionStatus.Cancelled }
                    : candidate;

            async ValueTask<ExtractionSummary> RefreshCancelledSummaryAsync(
                AtomicOutputFile output,
                ExtractionSummary candidate)
            {
                ExtractionSummary refreshed =
                    ObserveSummaryCancellation(candidate);
                if (ReferenceEquals(refreshed, candidate))
                {
                    return candidate;
                }

                output.Stream.Position = 0;
                output.Stream.SetLength(0);
                byte[] refreshedBytes = JsonSerializer.SerializeToUtf8Bytes(
                    refreshed,
                    SummaryJsonOptions);
                await output.Stream.WriteAsync(
                    refreshedBytes,
                    CancellationToken.None).ConfigureAwait(false);
                await output.Stream.FlushAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                return refreshed;
            }
        }
    }

    private static ToolDiagnostic? VerifyDecoderResult(
        ArchiveEntry entry,
        PayloadResult decoder,
        ObservedPayload observed)
    {
        if (decoder.DecodedBytes != observed.DecodedBytes)
        {
            return new ToolDiagnostic(
                DiagnosticCode.DecodedSizeMismatch,
                $"Decoder reported {decoder.DecodedBytes} bytes but streamed {observed.DecodedBytes}.",
                entry.DecodedPath);
        }

        if (entry.DecodedSize != observed.DecodedBytes)
        {
            return new ToolDiagnostic(
                DiagnosticCode.DecodedSizeMismatch,
                $"Entry declares {entry.DecodedSize} decoded bytes but streamed {observed.DecodedBytes}.",
                entry.DecodedPath);
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                decoder.Sha256,
                observed.Sha256))
        {
            return new ToolDiagnostic(
                DiagnosticCode.DecodedSizeMismatch,
                "Decoder-reported SHA-256 does not match the streamed payload.",
                entry.DecodedPath);
        }

        return null;
    }

    private static ToolDiagnostic DiagnosticFor(
        Exception exception,
        string subject) =>
        exception switch
        {
            IOException or UnauthorizedAccessException =>
                ReportDiagnostic(exception, subject),
            InvalidDataException =>
                new ToolDiagnostic(
                    DiagnosticCode.LzoCorruption,
                    $"Payload decoding failed: {exception.Message}",
                    subject),
            _ => new ToolDiagnostic(
                DiagnosticCode.InvalidHeader,
                $"Payload extraction failed: {exception.Message}",
                subject)
        };

    private static ToolDiagnostic ReportDiagnostic(
        Exception exception,
        string subject)
    {
        DiagnosticCode code =
            File.Exists(subject) || Directory.Exists(subject)
                ? DiagnosticCode.OutputCollision
                : DiagnosticCode.OutputPermissionFailure;
        return new ToolDiagnostic(
            code,
            $"Output could not be atomically published: {exception.Message}",
            subject);
    }

    private static bool IsEntryException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException;

    private static long SumDeclaredBytes(
        IReadOnlyList<ArchiveIndex> archives)
    {
        long total = 0;
        foreach (ArchiveEntry entry in archives.SelectMany(
                     archive => archive.Entries))
        {
            total = SaturatingAdd(total, Math.Max(0, entry.DecodedSize));
        }

        return total;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right
            ? long.MaxValue
            : left + right;

    private static string JoinWindowsPath(params string[] components) =>
        string.Join('\\', components);

    private sealed class EntryDiagnosticException(
        ToolDiagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public ToolDiagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed record ObservedPayload(long DecodedBytes, string Sha256);

    private sealed record SummaryPublication(
        ExtractionSummary Summary,
        ToolDiagnostic? Diagnostic);

    private sealed class ObservingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _completed;
        private long _decodedBytes;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _decodedBytes;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            inner.Write(buffer, offset, count);
            Observe(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Observe(buffer);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            Observe(buffer.Span);
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteArrayAsync(buffer, offset, count, cancellationToken);

        public ObservedPayload Complete()
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _completed = true;
            return new ObservedPayload(
                _decodedBytes,
                Convert.ToHexString(_hash.GetHashAndReset())
                    .ToLowerInvariant());
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private async Task WriteArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(
                buffer.AsMemory(offset, count),
                cancellationToken).ConfigureAwait(false);
            Observe(buffer.AsSpan(offset, count));
        }

        private void Observe(ReadOnlySpan<byte> bytes)
        {
            if (_completed)
            {
                throw new ObjectDisposedException(
                    nameof(ObservingWriteStream));
            }

            _hash.AppendData(bytes);
            _decodedBytes = SaturatingAdd(_decodedBytes, bytes.Length);
        }
    }

    private sealed record ManifestRow(
        string SourcePakPath,
        long SourcePakSize,
        string SourcePakSha256,
        GameFamily Game,
        int Ordinal,
        string DecodedPath,
        long RecordOffset,
        long PayloadOffset,
        long StoredSize,
        long DeclaredDecodedSize,
        long ActualDecodedSize,
        uint FileKey,
        int KeyIndex,
        string OutputPath,
        string OutputSha256,
        PayloadSignature Signature,
        string Status,
        DiagnosticCode? DiagnosticCode,
        string DiagnosticMessage,
        string CollisionPath)
    {
        public static ManifestRow Completed(
            ArchiveIndex archive,
            string sourceSha256,
            ArchiveEntry source,
            ManifestEntry entry) =>
            new(
                archive.ArchivePath,
                archive.ArchiveSize,
                sourceSha256,
                archive.Family,
                source.Ordinal,
                source.DecodedPath,
                source.RecordOffset,
                source.PayloadOffset,
                source.StoredSize,
                source.DecodedSize,
                entry.DecodedSize,
                source.FileKey,
                source.KeyIndex,
                entry.OutputPath,
                entry.Sha256,
                entry.Signature,
                "Completed",
                null,
                string.Empty,
                entry.Collision?.ResolvedPath ?? string.Empty);

        public static ManifestRow Failed(
            ArchiveIndex archive,
            string sourceSha256,
            ArchiveEntry source,
            string outputPath,
            ToolDiagnostic diagnostic,
            OutputPathCollision? collision = null) =>
            new(
                archive.ArchivePath,
                archive.ArchiveSize,
                sourceSha256,
                archive.Family,
                source.Ordinal,
                source.DecodedPath,
                source.RecordOffset,
                source.PayloadOffset,
                source.StoredSize,
                source.DecodedSize,
                0,
                source.FileKey,
                source.KeyIndex,
                outputPath,
                string.Empty,
                PayloadSignature.Unknown,
                "Failed",
                diagnostic.Code,
                diagnostic.Message,
                collision?.ResolvedPath ?? string.Empty);
    }

    private sealed class IncrementalReports : IAsyncDisposable
    {
        private const string ManifestHeader =
            "SourcePakPath,SourcePakSize,SourcePakSha256,Game,Ordinal,DecodedPath,RecordOffset,PayloadOffset,StoredSize,DeclaredDecodedSize,ActualDecodedSize,FileKey,KeyIndex,OutputPath,OutputSha256,Signature,Status,DiagnosticCode,DiagnosticMessage,CollisionPath";
        private const string FailuresHeader =
            "SourcePakPath,Game,Ordinal,DecodedPath,OutputPath,DiagnosticCode,Message";

        private static readonly IReadOnlySet<int> ManifestUntrustedFields =
            new HashSet<int> { 0, 5, 13, 18, 19 };
        private static readonly IReadOnlySet<int> FailureUntrustedFields =
            new HashSet<int> { 0, 3, 4, 6 };

        private readonly List<ToolDiagnostic> _diagnostics;
        private readonly IncrementalReport _manifest;
        private readonly IncrementalReport _failures;
        private int _reportedDiagnosticCount;

        private IncrementalReports(
            List<ToolDiagnostic> diagnostics,
            IncrementalReport manifest,
            IncrementalReport failures)
        {
            _diagnostics = diagnostics;
            _manifest = manifest;
            _failures = failures;
        }

        public IReadOnlyList<ToolDiagnostic> Diagnostics =>
            _diagnostics.ToArray();

        public static async ValueTask<IncrementalReports> CreateAsync(
            string manifestPath,
            string failuresPath,
            string outputRoot,
            IReadOnlyList<string> inputPaths,
            IExtractionReportObserver? observer)
        {
            var diagnostics = new List<ToolDiagnostic>();
            void Record(ToolDiagnostic diagnostic) =>
                diagnostics.Add(diagnostic);

            IncrementalReport manifest =
                await IncrementalReport.CreateAsync(
                    ExtractionReportKind.Manifest,
                    manifestPath,
                    ManifestHeader,
                    outputRoot,
                    inputPaths,
                    observer,
                    Record).ConfigureAwait(false);
            IncrementalReport failures =
                await IncrementalReport.CreateAsync(
                    ExtractionReportKind.Failures,
                    failuresPath,
                    FailuresHeader,
                    outputRoot,
                    inputPaths,
                    observer,
                    Record).ConfigureAwait(false);
            return new IncrementalReports(
                diagnostics,
                manifest,
                failures);
        }

        public ValueTask AppendManifestAsync(ManifestRow row)
        {
            string[] fields =
            [
                row.SourcePakPath,
                Invariant(row.SourcePakSize),
                row.SourcePakSha256,
                row.Game.ToString(),
                Invariant(row.Ordinal),
                row.DecodedPath,
                Invariant(row.RecordOffset),
                Invariant(row.PayloadOffset),
                Invariant(row.StoredSize),
                Invariant(row.DeclaredDecodedSize),
                Invariant(row.ActualDecodedSize),
                Invariant(row.FileKey),
                Invariant(row.KeyIndex),
                row.OutputPath,
                row.OutputSha256,
                row.Signature.ToString(),
                row.Status,
                row.DiagnosticCode?.ToString() ?? string.Empty,
                row.DiagnosticMessage,
                row.CollisionPath
            ];
            return _manifest.AppendAsync(
                fields,
                ManifestUntrustedFields);
        }

        public ValueTask AppendFailureAsync(
            ArchiveIndex archive,
            ArchiveEntry entry,
            string outputPath,
            ToolDiagnostic diagnostic)
        {
            string[] fields =
            [
                archive.ArchivePath,
                archive.Family.ToString(),
                Invariant(entry.Ordinal),
                entry.DecodedPath,
                outputPath,
                diagnostic.Code.ToString(),
                diagnostic.Message
            ];
            return _failures.AppendAsync(
                fields,
                FailureUntrustedFields);
        }

        public async ValueTask AppendPendingReportFailuresAsync()
        {
            while (_reportedDiagnosticCount < _diagnostics.Count)
            {
                ToolDiagnostic diagnostic =
                    _diagnostics[_reportedDiagnosticCount++];
                if (StringComparer.OrdinalIgnoreCase.Equals(
                        diagnostic.Subject,
                        _failures.Path))
                {
                    continue;
                }

                string[] fields =
                [
                    string.Empty,
                    GameFamily.Unknown.ToString(),
                    "-1",
                    string.Empty,
                    diagnostic.Subject ?? string.Empty,
                    diagnostic.Code.ToString(),
                    diagnostic.Message
                ];
                await _failures.AppendAsync(
                    fields,
                    FailureUntrustedFields).ConfigureAwait(false);
            }
        }

        public ValueTask PublishManifestAsync() =>
            _manifest.PublishAsync();

        public ValueTask PublishFailuresAsync() =>
            _failures.PublishAsync();

        public async ValueTask DisposeAsync()
        {
            await _manifest.DisposeAsync().ConfigureAwait(false);
            await _failures.DisposeAsync().ConfigureAwait(false);
        }

        private static string Invariant<T>(T value)
            where T : IFormattable =>
            value.ToString(null, CultureInfo.InvariantCulture);
    }

    private sealed class IncrementalReport : IAsyncDisposable
    {
        private readonly ExtractionReportKind _kind;
        private readonly string _outputRoot;
        private readonly IReadOnlyList<string> _inputPaths;
        private readonly IExtractionReportObserver? _observer;
        private readonly Action<ToolDiagnostic> _recordDiagnostic;
        private AtomicOutputFile? _output;
        private StreamWriter? _writer;
        private bool _failed;
        private bool _published;

        private IncrementalReport(
            ExtractionReportKind kind,
            string path,
            string outputRoot,
            IReadOnlyList<string> inputPaths,
            IExtractionReportObserver? observer,
            Action<ToolDiagnostic> recordDiagnostic)
        {
            _kind = kind;
            Path = path;
            _outputRoot = outputRoot;
            _inputPaths = inputPaths;
            _observer = observer;
            _recordDiagnostic = recordDiagnostic;
        }

        public string Path { get; }

        public static async ValueTask<IncrementalReport> CreateAsync(
            ExtractionReportKind kind,
            string path,
            string header,
            string outputRoot,
            IReadOnlyList<string> inputPaths,
            IExtractionReportObserver? observer,
            Action<ToolDiagnostic> recordDiagnostic)
        {
            var report = new IncrementalReport(
                kind,
                path,
                outputRoot,
                inputPaths,
                observer,
                recordDiagnostic);
            await report.InitializeAsync(header).ConfigureAwait(false);
            return report;
        }

        public async ValueTask AppendAsync(
            IReadOnlyList<string> fields,
            IReadOnlySet<int> untrustedFields)
        {
            if (_failed || _published || _writer is null)
            {
                return;
            }

            try
            {
                _observer?.OnStage(
                    _kind,
                    ExtractionReportStage.Append);
                await ManifestWriter.WriteCsvRowAsync(
                    _writer,
                    fields,
                    untrustedFields).ConfigureAwait(false);
                _observer?.OnStage(
                    _kind,
                    ExtractionReportStage.Flush);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsContainableReportException(exception))
            {
                await FailAsync(
                    ReportDiagnostic(exception, Path)).ConfigureAwait(false);
            }
        }

        public async ValueTask PublishAsync()
        {
            if (_failed || _published)
            {
                return;
            }

            try
            {
                _observer?.OnStage(
                    _kind,
                    ExtractionReportStage.Close);
                await CloseWriterAsync().ConfigureAwait(false);
                _observer?.OnStage(
                    _kind,
                    ExtractionReportStage.Publish);
                await _output!.PublishAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                _published = true;
            }
            catch (Exception exception) when (
                IsContainableReportException(exception))
            {
                await FailAsync(
                    ReportDiagnostic(exception, Path)).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await CloseWriterAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsContainableReportException(exception))
            {
                RecordOnce(ReportDiagnostic(exception, Path));
            }

            if (_output is not null)
            {
                try
                {
                    await _output.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    IsContainableReportException(exception))
                {
                    RecordOnce(ReportDiagnostic(exception, Path));
                }
            }
        }

        private async ValueTask InitializeAsync(string header)
        {
            try
            {
                _observer?.OnStage(
                    _kind,
                    ExtractionReportStage.Create);
                ToolResult<string> safeDestination =
                    ExtractionOptions.ValidateDestination(
                        _outputRoot,
                        _inputPaths,
                        Path);
                if (!safeDestination.IsSuccess)
                {
                    await FailAsync(safeDestination.Error!)
                        .ConfigureAwait(false);
                    return;
                }

                _output = await AtomicOutputFile.CreateAsync(
                    safeDestination.Value!,
                    CancellationToken.None).ConfigureAwait(false);
                _writer = new StreamWriter(
                    _output.Stream,
                    new UTF8Encoding(false),
                    4096,
                    leaveOpen: true)
                {
                    NewLine = "\n"
                };
                await _writer.WriteLineAsync(header).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsContainableReportException(exception))
            {
                await FailAsync(
                    ReportDiagnostic(exception, Path)).ConfigureAwait(false);
            }
        }

        private async ValueTask FailAsync(ToolDiagnostic diagnostic)
        {
            RecordOnce(diagnostic);
            try
            {
                await CloseWriterAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsContainableReportException(exception))
            {
            }

            if (_output is not null)
            {
                try
                {
                    await _output.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    IsContainableReportException(exception))
                {
                }

                _output = null;
            }
        }

        private void RecordOnce(ToolDiagnostic diagnostic)
        {
            if (_failed)
            {
                return;
            }

            _failed = true;
            _recordDiagnostic(diagnostic);
        }

        private async ValueTask CloseWriterAsync()
        {
            StreamWriter? writer = _writer;
            _writer = null;
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static bool IsContainableReportException(Exception exception) =>
        exception is not (
            OutOfMemoryException
                or StackOverflowException
                or AccessViolationException);
}
