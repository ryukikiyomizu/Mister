using Mister.Core.Diagnostics;

namespace Mister.Core.Formats;

public enum DetectionDepth
{
    HeaderAndFirstRecord,
    Full
}

public sealed record DetectionResult(
    GameFamily Family,
    ArchiveIndex? Index,
    IReadOnlyList<ToolDiagnostic> Diagnostics);

public sealed class ArchiveDetector
{
    private readonly IReadOnlyList<IArchiveReader> _readers;

    public ArchiveDetector(params IArchiveReader[] readers)
        : this((IEnumerable<IArchiveReader>)readers)
    {
    }

    public ArchiveDetector(IEnumerable<IArchiveReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        IArchiveReader[] available = readers.ToArray();
        if (available.Any(static reader => reader is null))
        {
            throw new ArgumentException(
                "Archive readers cannot contain null candidates.",
                nameof(readers));
        }

        if (available.Any(static reader => reader.Family == GameFamily.Unknown))
        {
            throw new ArgumentException(
                "Archive readers must identify a concrete game family.",
                nameof(readers));
        }

        if (available
            .GroupBy(static reader => reader.Family)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Only one evidence candidate per game family can be supplied.",
                nameof(readers));
        }

        _readers = Array.AsReadOnly(available);
    }

    public async ValueTask<DetectionResult> DetectAsync(
        string path,
        DetectionDepth depth,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(depth))
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var successes = new List<(IArchiveReader Reader, ArchiveIndex Index)>();
        var diagnostics = new List<ToolDiagnostic>();
        foreach (IArchiveReader reader in _readers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolResult<ArchiveIndex> result;
            try
            {
                result = await reader.IndexAsync(path, depth, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Add(new ToolDiagnostic(
                    DiagnosticCode.UnknownPakFormat,
                    $"{reader.Family} detection failed while reading the selected file: {exception.Message}",
                    path));
                continue;
            }

            if (cancellationToken.IsCancellationRequested
                || result.Error?.Code == DiagnosticCode.Cancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!result.IsSuccess || result.Value is null)
            {
                diagnostics.Add(
                    result.Error
                    ?? new ToolDiagnostic(
                        DiagnosticCode.UnknownPakFormat,
                        $"{reader.Family} detection returned no archive index.",
                        path));
                continue;
            }

            ArchiveIndex index = result.Value;
            if (index.Family != reader.Family)
            {
                diagnostics.Add(new ToolDiagnostic(
                    DiagnosticCode.UnknownPakFormat,
                    $"{reader.Family} detection returned an index for {index.Family}.",
                    path));
                continue;
            }

            if (depth == DetectionDepth.Full
                && index.FinalOffset != index.ArchiveSize)
            {
                diagnostics.Add(new ToolDiagnostic(
                    DiagnosticCode.TrailingData,
                    $"{reader.Family} full detection ended at {index.FinalOffset}, not EOF {index.ArchiveSize}.",
                    path));
                continue;
            }

            successes.Add((reader, index));
            diagnostics.AddRange(index.Diagnostics);
        }

        if (successes.Count == 1)
        {
            (IArchiveReader reader, ArchiveIndex winner) = successes[0];
            if (depth == DetectionDepth.Full)
            {
                ArchiveValidationCoordinator.Commit(reader);
            }

            return new DetectionResult(
                winner.Family,
                winner,
                diagnostics.AsReadOnly());
        }

        string message = successes.Count == 0
            ? "No available archive reader validated the selected file structurally."
            : "More than one archive reader validated the selected file structurally.";
        diagnostics.Add(new ToolDiagnostic(
            DiagnosticCode.UnknownPakFormat,
            message,
            path));
        return new DetectionResult(
            GameFamily.Unknown,
            null,
            diagnostics.AsReadOnly());
    }
}

internal static class ArchiveValidationCoordinator
{
    internal static void Commit(IArchiveReader reader)
    {
        switch (reader)
        {
            case T2.T2ArchiveReader t2:
                t2.CommitStructuralValidation();
                break;
            case T3.T3ArchiveReader t3:
                t3.CommitStructuralValidation();
                break;
        }
    }
}
