using System.Security.Cryptography;
using Mister.Core.Diagnostics;
using Mister.Core.Formats;

namespace Mister.App.Services;

internal sealed record PlaybackFile(
    string Path,
    long DecodedBytes,
    string Sha256);

internal static class PlaybackMaterializer
{
    public const long MaximumPlaybackBytes = 1024L * 1024 * 1024;

    public static async ValueTask<ToolResult<PlaybackFile>> MaterializeAsync(
        IPayloadDecoder decoder,
        string archivePath,
        ArchiveEntry entry,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (entry.DecodedSize is < 0 or > MaximumPlaybackBytes)
        {
            return ToolResult<PlaybackFile>.Failure(new ToolDiagnostic(
                DiagnosticCode.DecodedSizeMismatch,
                "The media payload is outside the 1 GB playback safety limit.",
                entry.DecodedPath));
        }

        string extension = SafeExtension(entry.DecodedPath);
        string fileName = $"preview-{Guid.NewGuid():N}{extension}";
        string finalPath = Path.Combine(outputDirectory, fileName);
        string partialPath = finalPath + ".part";
        try
        {
            Directory.CreateDirectory(outputDirectory);
            PayloadResult result;
            long outputLength;
            await using (var output = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous
                             | FileOptions.SequentialScan
                             | FileOptions.WriteThrough))
            {
                result = await decoder
                    .DecodeAsync(
                        archivePath,
                        entry,
                        output,
                        cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                outputLength = output.Length;
            }
            if (outputLength != entry.DecodedSize
                || result.DecodedBytes != entry.DecodedSize)
            {
                return Failure(
                    DiagnosticCode.DecodedSizeMismatch,
                    "The decoded media length does not match the archive record.",
                    entry.DecodedPath,
                    partialPath);
            }

            string sha256;
            await using (var input = new FileStream(
                             partialPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous
                             | FileOptions.SequentialScan))
            {
                sha256 = Convert.ToHexString(
                        await SHA256.HashDataAsync(input, cancellationToken)
                            .ConfigureAwait(false))
                    .ToLowerInvariant();
            }
            if (string.IsNullOrWhiteSpace(result.Sha256)
                || !string.Equals(
                    result.Sha256,
                    sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    DiagnosticCode.SignatureMismatch,
                    "The decoded media hash does not match the decoder result.",
                    entry.DecodedPath,
                    partialPath);
            }

            File.Move(partialPath, finalPath);
            return ToolResult<PlaybackFile>.Success(new PlaybackFile(
                finalPath,
                result.DecodedBytes,
                sha256));
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            TryDelete(finalPath);
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            TryDelete(partialPath);
            TryDelete(finalPath);
            return ToolResult<PlaybackFile>.Failure(new ToolDiagnostic(
                DiagnosticCode.OutputPermissionFailure,
                $"The playback file could not be prepared: {exception.Message}",
                entry.DecodedPath));
        }
    }

    private static ToolResult<PlaybackFile> Failure(
        DiagnosticCode code,
        string message,
        string subject,
        string partialPath)
    {
        TryDelete(partialPath);
        return ToolResult<PlaybackFile>.Failure(
            new ToolDiagnostic(code, message, subject));
    }

    private static string SafeExtension(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length is < 2 or > 10
            || extension.Skip(1).Any(static character =>
                !char.IsAsciiLetterOrDigit(character)))
        {
            return ".media";
        }
        return extension.ToLowerInvariant();
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
