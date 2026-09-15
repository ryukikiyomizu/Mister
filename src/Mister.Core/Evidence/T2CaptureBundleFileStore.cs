using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mister.Core.Diagnostics;
using Mister.Core.Extraction;
using Mister.Core.Formats;
using Mister.Core.T2;

namespace Mister.Core.Evidence;

public static class T2CaptureBundleFileStore
{
    internal const int MaximumBundleBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async ValueTask SaveAsync(
        string path,
        T2CaptureBundle bundle,
        CancellationToken cancellationToken = default)
        => await SaveAsync(path, bundle, cancellationToken, afterFlush: null)
            .ConfigureAwait(false);

    internal static async ValueTask SaveAsync(
        string path,
        T2CaptureBundle bundle,
        CancellationToken cancellationToken,
        Func<FileStream, Task>? afterFlush)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bundle);
        EnsureBundleExtension(path);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bytes = Utf8.GetBytes(
            T2CaptureBundleSerializer.Serialize(bundle));
        if (bytes.Length > MaximumBundleBytes)
        {
            throw new InvalidDataException(
                $"The T2 capture bundle exceeds the {MaximumBundleBytes}-byte limit.");
        }

        await using AtomicOutputFile output =
            await AtomicOutputFile.CreateVerifiedAsync(
                path,
                beforePartCreate: null,
                beforeRelativePartCreate: null,
                static (handle, expectedPath) =>
                    OpenedFileHandle.Capture(handle, expectedPath),
                cancellationToken);
        await output.Stream.WriteAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
        await output.Stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
        if (output.Stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
            if (afterFlush is not null) await afterFlush(file).ConfigureAwait(false);
            file.Position = 0;
            byte[] persisted = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(persisted, cancellationToken).ConfigureAwait(false);
            ToolResult<T2CaptureBundle> verified = T2CaptureBundleSerializer.Deserialize(Utf8.GetString(persisted));
            if (!verified.IsSuccess) throw new InvalidDataException(verified.Error!.Message);
        }
        await output.PublishAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static ValueTask<ToolResult<ClientEvidence>> LoadVerifiedAsync(
        string path,
        string currentClientSha256,
        string currentValidationArchiveSha256,
        CancellationToken cancellationToken = default) =>
        LoadVerifiedCoreAsync(
            path,
            currentClientSha256,
            currentValidationArchiveSha256,
            validationPakPath: null,
            validationPakStream: null,
            cancellationToken);

    public static async ValueTask<ToolResult<ClientEvidence>> LoadVerifiedAsync(
        string path,
        string currentClientSha256,
        string currentValidationArchiveSha256,
        string validationPakPath,
        CancellationToken cancellationToken = default)
        => await LoadVerifiedAsync(
            path, currentClientSha256, currentValidationArchiveSha256,
            validationPakPath, cancellationToken, afterPakHash: null)
            .ConfigureAwait(false);

    internal static async ValueTask<ToolResult<ClientEvidence>> LoadVerifiedAsync(
        string path,
        string currentClientSha256,
        string currentValidationArchiveSha256,
        string validationPakPath,
        CancellationToken cancellationToken,
        Func<string, Task>? afterPakHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationPakPath);
        try
        {
            string fullPakPath = Path.GetFullPath(validationPakPath);
            using SafeFileHandle handle = File.OpenHandle(
                fullPakPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            OpenedFileProof opened = OpenedFileHandle.Capture(handle, fullPakPath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(fullPakPath, opened.FinalPath))
            {
                return Failure(
                    DiagnosticCode.UnsupportedClient,
                    "The selected validation PAK resolves through a reparse point.",
                    fullPakPath);
            }
            await using var pakStream = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 64 * 1024,
                isAsync: true);
            string pakHash = await FileHasher.Sha256Async(
                pakStream,
                cancellationToken).ConfigureAwait(false);
            if (!FixedTimeSha256Equals(
                    currentValidationArchiveSha256,
                    pakHash))
            {
                return Failure(
                    DiagnosticCode.UnsupportedClient,
                    "The selected validation PAK changed before it could be indexed.",
                    fullPakPath);
            }
            if (afterPakHash is not null) await afterPakHash(fullPakPath).ConfigureAwait(false);
            pakStream.Position = 0;
            return await LoadVerifiedCoreAsync(
                path,
                currentClientSha256,
                pakHash,
                fullPakPath,
                pakStream,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "T2 capture-bundle loading was cancelled.",
                path);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                $"The validation PAK could not be verified: {exception.Message}",
                validationPakPath);
        }
    }

    private static async ValueTask<ToolResult<ClientEvidence>>
        LoadVerifiedCoreAsync(
            string path,
            string currentClientSha256,
            string currentValidationArchiveSha256,
            string? validationPakPath,
            FileStream? validationPakStream,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentClientSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentValidationArchiveSha256);
        EnsureBundleExtension(path);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await ReadBoundedBundleAsync(path, cancellationToken)
                .ConfigureAwait(false);
            ToolResult<T2CaptureBundle> bundle =
                T2CaptureBundleSerializer.Deserialize(
                    Utf8.GetString(bytes));
            if (!bundle.IsSuccess)
            {
                return ToolResult<ClientEvidence>.Failure(bundle.Error!);
            }

            ToolResult<ClientEvidence> evidence =
                T2CaptureBundleSerializer.ToEvidence(
                    bundle.Value!,
                    currentClientSha256,
                    currentValidationArchiveSha256);
            if (!evidence.IsSuccess || validationPakPath is null)
            {
                return evidence;
            }

            var reader = new T2ArchiveReader(evidence.Value!);
            ToolResult<ArchiveIndex> index = await reader.IndexAsync(
                validationPakStream!,
                validationPakPath,
                cancellationToken).ConfigureAwait(false);
            return index.IsSuccess
                ? ToolResult<ClientEvidence>.Success(reader.ClientEvidence)
                : ToolResult<ClientEvidence>.Failure(index.Error!);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "T2 capture-bundle loading was cancelled.",
                path);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or DecoderFallbackException)
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                $"The T2 capture bundle could not be loaded safely: {exception.Message}",
                path);
        }
    }

    private static async ValueTask<byte[]> ReadBoundedBundleAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string expectedPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
        using SafeFileHandle handle = File.OpenHandle(
            expectedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        OpenedFileProof opened = OpenedFileHandle.Capture(handle, expectedPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                expectedPath,
                opened.FinalPath))
        {
            throw new IOException(
                "The capture-bundle path resolves through a reparse point.");
        }

        await using var stream = new FileStream(
            handle,
            FileAccess.Read,
            bufferSize: 4096,
            isAsync: true);
        long length = stream.Length;
        if (length is < 1 or > MaximumBundleBytes)
        {
            throw new InvalidDataException(
                $"The T2 capture bundle must be between 1 and {MaximumBundleBytes} bytes.");
        }

        byte[] bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
        OpenedFileProof current = OpenedFileHandle.Capture(handle, expectedPath);
        if (!OpenedFileHandle.RepresentsSameObject(opened, current))
        {
            throw new IOException(
                "The capture-bundle file changed identity while it was being read.");
        }

        return bytes;
    }

    private static async ValueTask<string> Sha256FileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void EnsureBundleExtension(string path)
    {
        if (!string.Equals(
                Path.GetExtension(path),
                ".ttcapture",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "T2 capture bundles must use the .ttcapture extension.",
                nameof(path));
        }
    }

    private static ToolResult<ClientEvidence> Failure(
        DiagnosticCode code,
        string message,
        string subject) =>
        ToolResult<ClientEvidence>.Failure(
            new ToolDiagnostic(code, message, subject));

    private static bool FixedTimeSha256Equals(string left, string right)
    {
        try
        {
            if (left.Length != 64 || right.Length != 64)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
