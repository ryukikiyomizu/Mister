using System.Runtime.CompilerServices;
using Mister.Core.Diagnostics;

namespace Mister.Core.Formats;

public sealed record CatalogItem(
    int SelectionOrdinal,
    string SourcePath,
    DetectionResult Detection);

public sealed class ArchiveCatalog
{
    private readonly ArchiveDetector _detector;

    public ArchiveCatalog(ArchiveDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
    }

    public async IAsyncEnumerable<CatalogItem> ScanAsync(
        IEnumerable<string> files,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        string[] selection = files.ToArray();
        int workerLimit = Math.Max(1, Environment.ProcessorCount);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var gate = new SemaphoreSlim(workerLimit, workerLimit);
        Task<CatalogItem>[] pending = selection
            .Select((path, ordinal) => DetectOneAsync(
                ordinal,
                path,
                gate,
                stop.Token))
            .ToArray();

        try
        {
            for (int ordinal = 0; ordinal < pending.Length; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return await pending[ordinal].WaitAsync(
                    cancellationToken);
            }
        }
        finally
        {
            stop.Cancel();
            try
            {
                await Task.WhenAll(pending);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                // DetectOneAsync converts per-file failures to catalog items.
            }
        }
    }

    private async Task<CatalogItem> DetectOneAsync(
        int ordinal,
        string path,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            DetectionResult detection;
            try
            {
                detection = await _detector.DetectAsync(
                    path,
                    DetectionDepth.Full,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                detection = new DetectionResult(
                    GameFamily.Unknown,
                    null,
                    [
                        new ToolDiagnostic(
                            DiagnosticCode.UnknownPakFormat,
                            $"Archive detection failed: {exception.Message}",
                            path)
                    ]);
            }

            return new CatalogItem(ordinal, path, detection);
        }
        finally
        {
            gate.Release();
        }
    }
}
