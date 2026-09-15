using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mister.Core.Diagnostics;

namespace Mister.Core.Extraction;

public sealed record CompletedByPackManifestRow(
    string ArchiveName,
    int ArchiveSelectionOrdinal,
    ManifestEntry Entry)
{
    public int Ordinal => Entry.Ordinal;

    public string DecodedPath => Entry.ArchivePath;

    public string OutputPath => Entry.OutputPath;

    public long DecodedSize => Entry.DecodedSize;

    public string Sha256 => Entry.Sha256;

    public PayloadSignature Signature => Entry.Signature;
}

public sealed record MergedReplacement(
    string DecodedPath,
    string WinnerArchive,
    int WinnerOrdinal,
    string ReplacedArchive,
    int ReplacedOrdinal,
    string WinnerSha256,
    string ReplacedSha256,
    int MergeOrdinal);

public enum MergedViewStatus
{
    Completed,
    CompletedWithFailures,
    Cancelled
}

public sealed record MergedViewResult(
    int PublishedFiles,
    IReadOnlyList<MergedReplacement> Replacements,
    string ReplacementsCsv)
{
    public MergedViewStatus Status { get; init; } = MergedViewStatus.Completed;

    public IReadOnlyList<ToolDiagnostic> Diagnostics { get; init; } = [];
}

internal enum MergedViewStage
{
    BeforeSourceOpen,
    BeforePartCreate,
    BeforeRelativePartCreate,
    BeforeFilePublish,
    AfterFilePublish,
    ReportCreate,
    ReportAppend,
    ReportFlush,
    ReportClose,
    BeforeReportPublish
}

internal interface IMergedViewObserver
{
    void OnStage(MergedViewStage stage, string subject);
}

public sealed class MergedViewBuilder
{
    private const string ReplacementsFileName = "_merged_replacements.csv";
    private const string ReplacementsHeader =
        "DecodedPath,WinnerArchive,WinnerOrdinal,ReplacedArchive,ReplacedOrdinal,WinnerSha256,ReplacedSha256,MergeOrdinal";

    private static readonly IReadOnlySet<int> ReplacementUntrustedFields =
        new HashSet<int> { 0, 1, 3 };

    private readonly IMergedViewObserver? _observer;

    public MergedViewBuilder()
        : this(observer: null)
    {
    }

    internal MergedViewBuilder(IMergedViewObserver? observer)
    {
        _observer = observer;
    }

    public async ValueTask<MergedViewResult> BuildAsync(
        IReadOnlyList<CompletedByPackManifestRow> rows,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        string canonicalRoot = ExtractionOptions.Canonicalize(outputRoot);
        var diagnostics = new List<ToolDiagnostic>();
        string byPackRoot = ExtractionOptions.Canonicalize(
            Path.Combine(canonicalRoot, "by_pack"));
        if (!ExtractionOptions.IsSameOrDescendant(
                byPackRoot,
                canonicalRoot)
            || StringComparer.OrdinalIgnoreCase.Equals(
                byPackRoot,
                canonicalRoot))
        {
            diagnostics.Add(
                new ToolDiagnostic(
                    DiagnosticCode.UnsafeOutputPath,
                    "The by-pack tree resolves outside the selected output.",
                    byPackRoot));
        }

        string mergedRoot = Path.Combine(canonicalRoot, "merged");
        string replacementsPath = Path.Combine(
            canonicalRoot,
            ReplacementsFileName);
        var replacements = new List<MergedReplacement>();
        var publishedTargets = new List<MergeTarget>();
        int publishedFiles = 0;
        bool cancelled = cancellationToken.IsCancellationRequested;

        IReadOnlyList<MergeTarget> targets = CreateTargets(
            rows,
            mergedRoot,
            diagnostics);
        bool pathsAreSafe = diagnostics.Count == 0;
        if (pathsAreSafe)
        {
            foreach (MergeTarget target in targets)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                CompletedByPackManifestRow winner = target.Rows[^1];
                string? sourcePath = ResolveSource(
                    canonicalRoot,
                    byPackRoot,
                    winner,
                    diagnostics);
                if (sourcePath is null)
                {
                    continue;
                }

                bool published = await CopyVerifiedAsync(
                    winner,
                    sourcePath,
                    target.DestinationPath,
                    canonicalRoot,
                    byPackRoot,
                    mergedRoot,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                if (!published)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    continue;
                }

                publishedFiles++;
                publishedTargets.Add(target);

                _observer?.OnStage(
                    MergedViewStage.AfterFilePublish,
                    target.DestinationPath);
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
            }
        }

        foreach (ReplacementEvent replacement in publishedTargets
                     .SelectMany(target => target.ReplacementEvents)
                     .OrderBy(item => item.ApplicationOrdinal))
        {
            replacements.Add(
                new MergedReplacement(
                    replacement.DecodedPath,
                    replacement.Winner.ArchiveName,
                    replacement.Winner.Ordinal,
                    replacement.Replaced.ArchiveName,
                    replacement.Replaced.Ordinal,
                    replacement.Winner.Sha256,
                    replacement.Replaced.Sha256,
                    replacements.Count));
        }

        cancelled |= cancellationToken.IsCancellationRequested;
        if (cancelled)
        {
            diagnostics.Add(
                new ToolDiagnostic(
                    DiagnosticCode.Cancelled,
                    "Merged-view construction was cancelled.",
                    canonicalRoot));
        }

        await PublishReplacementsAsync(
            replacementsPath,
            canonicalRoot,
            byPackRoot,
            replacements,
            diagnostics).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested && !cancelled)
        {
            cancelled = true;
            diagnostics.Add(
                new ToolDiagnostic(
                    DiagnosticCode.Cancelled,
                    "Merged-view construction was cancelled.",
                    canonicalRoot));
        }

        return new MergedViewResult(
            publishedFiles,
            replacements.ToArray(),
            replacementsPath)
        {
            Status = cancelled
                ? MergedViewStatus.Cancelled
                : diagnostics.Count > 0
                    ? MergedViewStatus.CompletedWithFailures
                    : MergedViewStatus.Completed,
            Diagnostics = diagnostics.ToArray()
        };
    }

    private static IReadOnlyList<MergeTarget> CreateTargets(
        IReadOnlyList<CompletedByPackManifestRow> rows,
        string mergedRoot,
        List<ToolDiagnostic> diagnostics)
    {
        var ordered = rows.Select(
                (row, inputOrdinal) =>
                {
                    ArgumentNullException.ThrowIfNull(row);
                    return new OrderedRow(row, inputOrdinal);
                })
            .OrderBy(item => item, OrderedRowComparer.Instance)
            .ToArray();
        var groups = new Dictionary<string, MergeTarget>(
            StringComparer.OrdinalIgnoreCase);
        var targets = new List<MergeTarget>();
        int applicationOrdinal = 0;
        foreach (OrderedRow item in ordered)
        {
            ToolResult<string[]> parts =
                SafeArchivePath.ValidateAndSplit(item.Row.DecodedPath);
            if (!parts.IsSuccess)
            {
                diagnostics.Add(parts.Error!);
                continue;
            }

            string normalized = string.Join('\\', parts.Value!);
            if (!groups.TryGetValue(normalized, out MergeTarget? target))
            {
                target = new MergeTarget(normalized);
                groups.Add(normalized, target);
                targets.Add(target);
            }
            else
            {
                target.ReplacementEvents.Add(
                    new ReplacementEvent(
                        target.DecodedPath,
                        item.Row,
                        target.Rows[^1],
                        applicationOrdinal++));
            }

            target.Rows.Add(item.Row);
        }

        var registry = new SafeArchivePathRegistry(mergedRoot);
        for (int index = 0; index < targets.Count; index++)
        {
            MergeTarget target = targets[index];
            ToolResult<ResolvedArchivePath> resolved =
                registry.Resolve(target.DecodedPath, index);
            if (!resolved.IsSuccess)
            {
                diagnostics.Add(resolved.Error!);
                continue;
            }

            if (resolved.Value!.Collision is not null)
            {
                diagnostics.Add(
                    new ToolDiagnostic(
                        DiagnosticCode.OutputCollision,
                        "Merged paths collide under Windows path semantics.",
                        target.DecodedPath));
                continue;
            }

            target.DestinationPath = resolved.Value.FullPath;
        }

        if (diagnostics.Any(
                item => item.Code is DiagnosticCode.UnsafeOutputPath
                    or DiagnosticCode.OutputCollision))
        {
            return [];
        }

        return targets;
    }

    private static string? ResolveSource(
        string canonicalRoot,
        string byPackRoot,
        CompletedByPackManifestRow row,
        List<ToolDiagnostic> diagnostics)
    {
        ToolResult<string[]> outputParts =
            SafeArchivePath.ValidateAndSplit(row.OutputPath);
        if (!outputParts.IsSuccess)
        {
            diagnostics.Add(outputParts.Error!);
            return null;
        }

        string[] parts = outputParts.Value!;
        if (parts.Length < 2
            || !StringComparer.OrdinalIgnoreCase.Equals(parts[0], "by_pack"))
        {
            diagnostics.Add(
                new ToolDiagnostic(
                    DiagnosticCode.UnsafeOutputPath,
                    "A merged source must be a verified by-pack output.",
                    row.OutputPath));
            return null;
        }

        ToolResult<string> lexical =
            SafeArchivePath.Resolve(canonicalRoot, row.OutputPath);
        if (!lexical.IsSuccess)
        {
            diagnostics.Add(lexical.Error!);
            return null;
        }

        try
        {
            string canonicalSource =
                ExtractionOptions.Canonicalize(lexical.Value!);
            if (!ExtractionOptions.IsSameOrDescendant(
                    canonicalSource,
                    byPackRoot)
                || StringComparer.OrdinalIgnoreCase.Equals(
                    canonicalSource,
                    byPackRoot))
            {
                diagnostics.Add(
                    new ToolDiagnostic(
                        DiagnosticCode.UnsafeOutputPath,
                        "The by-pack source resolves outside the verified by-pack tree.",
                        row.OutputPath));
                return null;
            }

            return canonicalSource;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException)
        {
            diagnostics.Add(SourceIoDiagnostic(exception, row.OutputPath));
            return null;
        }
    }

    private async ValueTask<bool> CopyVerifiedAsync(
        CompletedByPackManifestRow row,
        string sourcePath,
        string destinationPath,
        string canonicalRoot,
        string byPackRoot,
        string mergedRoot,
        List<ToolDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            _observer?.OnStage(
                MergedViewStage.BeforeSourceOpen,
                sourcePath);
            source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            diagnostics.Add(SourceIoDiagnostic(exception, row.OutputPath));
            return false;
        }

        await using (source)
        {
            try
            {
                OpenedFileProof openedSource = VerifyOpenedHandle(
                    source.SafeFileHandle,
                    sourcePath,
                    byPackRoot,
                    forbiddenRoot: null);
                if (source.Length != row.DecodedSize)
                {
                    diagnostics.Add(
                        new ToolDiagnostic(
                            DiagnosticCode.DecodedSizeMismatch,
                            $"Verified by-pack size {row.DecodedSize} does not match current size {source.Length}.",
                            row.OutputPath));
                    return false;
                }

                string currentHash =
                    await Sha256Async(source, cancellationToken)
                        .ConfigureAwait(false);
                if (!StringComparer.OrdinalIgnoreCase.Equals(
                        currentHash,
                        row.Sha256))
                {
                    diagnostics.Add(
                        new ToolDiagnostic(
                            DiagnosticCode.DecodedSizeMismatch,
                            "Verified by-pack SHA-256 does not match the current source.",
                            row.OutputPath));
                    return false;
                }

                source.Position = 0;
                ToolResult<SignatureVerification> signature =
                    await SignatureVerifier.VerifyAsync(
                        row.DecodedPath,
                        source,
                        cancellationToken).ConfigureAwait(false);
                if (!signature.IsSuccess)
                {
                    diagnostics.Add(signature.Error! with
                    {
                        Subject = row.OutputPath
                    });
                    return false;
                }

                if (signature.Value!.Signature != row.Signature)
                {
                    diagnostics.Add(
                        new ToolDiagnostic(
                            DiagnosticCode.SignatureMismatch,
                            $"Verified signature {row.Signature} does not match current signature {signature.Value.Signature}.",
                            row.OutputPath));
                    return false;
                }

                source.Position = 0;
                OpenedFileProof sourceBeforeCopy = VerifyOpenedHandle(
                    source.SafeFileHandle,
                    sourcePath,
                    byPackRoot,
                    forbiddenRoot: null);
                if (!OpenedFileHandle.RepresentsSameObject(
                        openedSource,
                        sourceBeforeCopy))
                {
                    throw UnsafeOpenedHandle(
                        row.OutputPath,
                        "The verified by-pack source identity changed while it was open.");
                }

                ToolResult<string> safeDestination =
                    ExtractionOptions.ValidateDestination(
                        canonicalRoot,
                        [byPackRoot],
                        destinationPath);
                if (!safeDestination.IsSuccess)
                {
                    diagnostics.Add(safeDestination.Error!);
                    return false;
                }

                await using AtomicOutputFile output =
                    await AtomicOutputFile.CreateVerifiedAsync(
                        safeDestination.Value!,
                        partPath => _observer?.OnStage(
                            MergedViewStage.BeforePartCreate,
                            partPath),
                        partPath => _observer?.OnStage(
                            MergedViewStage.BeforeRelativePartCreate,
                            partPath),
                        (handle, expectedPath) => VerifyOpenedHandle(
                            handle,
                            expectedPath,
                            mergedRoot,
                            byPackRoot),
                        cancellationToken).ConfigureAwait(false);
                await source.CopyToAsync(
                    output.Stream,
                    64 * 1024,
                    cancellationToken).ConfigureAwait(false);
                _observer?.OnStage(
                    MergedViewStage.BeforeFilePublish,
                    safeDestination.Value!);
                cancellationToken.ThrowIfCancellationRequested();
                await output.PublishAsync(cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (OpenedHandleContainmentException exception)
            {
                diagnostics.Add(exception.Diagnostic);
                return false;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                diagnostics.Add(OutputDiagnostic(exception, destinationPath));
                return false;
            }
        }
    }

    private async ValueTask PublishReplacementsAsync(
        string replacementsPath,
        string canonicalRoot,
        string byPackRoot,
        IReadOnlyList<MergedReplacement> replacements,
        List<ToolDiagnostic> diagnostics)
    {
        AtomicOutputFile? output = null;
        StreamWriter? writer = null;
        try
        {
            _observer?.OnStage(
                MergedViewStage.ReportCreate,
                replacementsPath);
            ToolResult<string> destination =
                ExtractionOptions.ValidateDestination(
                    canonicalRoot,
                    [byPackRoot],
                    replacementsPath);
            if (!destination.IsSuccess)
            {
                diagnostics.Add(destination.Error!);
                return;
            }

            output = await AtomicOutputFile.CreateVerifiedAsync(
                destination.Value!,
                beforePartCreate: null,
                partPath => _observer?.OnStage(
                    MergedViewStage.BeforeRelativePartCreate,
                    partPath),
                (handle, expectedPath) => VerifyOpenedHandle(
                    handle,
                    expectedPath,
                    canonicalRoot,
                    byPackRoot),
                CancellationToken.None).ConfigureAwait(false);
            writer = new StreamWriter(
                output.Stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 4096,
                leaveOpen: true)
            {
                NewLine = "\r\n"
            };
            _observer?.OnStage(
                MergedViewStage.ReportAppend,
                replacementsPath);
            await ManifestWriter.WriteCsvRowAsync(
                writer,
                ReplacementsHeader.Split(','),
                new HashSet<int>()).ConfigureAwait(false);
            foreach (MergedReplacement replacement in replacements)
            {
                _observer?.OnStage(
                    MergedViewStage.ReportAppend,
                    replacementsPath);
                await ManifestWriter.WriteCsvRowAsync(
                    writer,
                    ReplacementFields(replacement),
                    ReplacementUntrustedFields).ConfigureAwait(false);
            }

            _observer?.OnStage(
                MergedViewStage.ReportFlush,
                replacementsPath);
            await writer.FlushAsync(CancellationToken.None)
                .ConfigureAwait(false);
            _observer?.OnStage(
                MergedViewStage.ReportClose,
                replacementsPath);
            await writer.DisposeAsync().ConfigureAwait(false);
            writer = null;
            _observer?.OnStage(
                MergedViewStage.BeforeReportPublish,
                replacementsPath);
            await output.PublishAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OpenedHandleContainmentException exception)
        {
            diagnostics.Add(exception.Diagnostic);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            diagnostics.Add(OutputDiagnostic(exception, replacementsPath));
        }
        finally
        {
            if (writer is not null)
            {
                try
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or ObjectDisposedException)
                {
                }
            }

            if (output is not null)
            {
                try
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or ObjectDisposedException)
                {
                }
            }
        }
    }

    private static string[] ReplacementFields(
        MergedReplacement replacement) =>
    [
        replacement.DecodedPath,
        replacement.WinnerArchive,
        Invariant(replacement.WinnerOrdinal),
        replacement.ReplacedArchive,
        Invariant(replacement.ReplacedOrdinal),
        replacement.WinnerSha256,
        replacement.ReplacedSha256,
        Invariant(replacement.MergeOrdinal)
    ];

    private static async ValueTask<string> Sha256Async(
        Stream source,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static ToolDiagnostic SourceIoDiagnostic(
        Exception exception,
        string subject) =>
        new(
            DiagnosticCode.OutputPermissionFailure,
            $"Verified by-pack source could not be read: {exception.Message}",
            subject);

    private static OpenedFileProof VerifyOpenedHandle(
        SafeFileHandle handle,
        string expectedPath,
        string allowedRoot,
        string? forbiddenRoot)
    {
        OpenedFileProof proof =
            OpenedFileHandle.Capture(handle, expectedPath);
        string expected = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(expectedPath));
        bool exactPath = StringComparer.OrdinalIgnoreCase.Equals(
            proof.FinalPath,
            expected);
        bool allowed = ExtractionOptions.IsSameOrDescendant(
            proof.FinalPath,
            allowedRoot);
        bool forbidden = forbiddenRoot is not null
            && ExtractionOptions.IsSameOrDescendant(
                proof.FinalPath,
                forbiddenRoot);
        if (!exactPath || !allowed || forbidden)
        {
            throw UnsafeOpenedHandle(
                expectedPath,
                "The opened file handle resolves outside its verified output boundary.");
        }

        return proof;
    }

    private static OpenedHandleContainmentException UnsafeOpenedHandle(
        string subject,
        string message) =>
        new(
            new ToolDiagnostic(
                DiagnosticCode.UnsafeOutputPath,
                message,
                subject));

    private static ToolDiagnostic OutputDiagnostic(
        Exception exception,
        string subject)
    {
        DiagnosticCode code =
            File.Exists(subject) || Directory.Exists(subject)
                ? DiagnosticCode.OutputCollision
                : DiagnosticCode.OutputPermissionFailure;
        return new ToolDiagnostic(
            code,
            $"Merged output could not be atomically published: {exception.Message}",
            subject);
    }

    private static string Invariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private sealed record OrderedRow(
        CompletedByPackManifestRow Row,
        int InputOrdinal);

    private sealed class OrderedRowComparer : IComparer<OrderedRow>
    {
        public static OrderedRowComparer Instance { get; } = new();

        public int Compare(OrderedRow? left, OrderedRow? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int comparison = PackFamilyOrder.Compare(
                left.Row,
                right.Row);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Row.Ordinal.CompareTo(right.Row.Ordinal);
            return comparison != 0
                ? comparison
                : left.InputOrdinal.CompareTo(right.InputOrdinal);
        }
    }

    private sealed class MergeTarget(string decodedPath)
    {
        public string DecodedPath { get; } = decodedPath;

        public List<CompletedByPackManifestRow> Rows { get; } = [];

        public List<ReplacementEvent> ReplacementEvents { get; } = [];

        public string DestinationPath { get; set; } = string.Empty;
    }

    private sealed record ReplacementEvent(
        string DecodedPath,
        CompletedByPackManifestRow Winner,
        CompletedByPackManifestRow Replaced,
        int ApplicationOrdinal);

    private sealed class OpenedHandleContainmentException(
        ToolDiagnostic diagnostic) : IOException(diagnostic.Message)
    {
        public ToolDiagnostic Diagnostic { get; } = diagnostic;
    }
}
