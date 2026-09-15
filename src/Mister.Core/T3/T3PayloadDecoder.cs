using System.Buffers.Binary;
using System.Security.Cryptography;
using Mister.Core.Formats;

namespace Mister.Core.T3;

public sealed class T3PayloadDecoder : IPayloadDecoder
{
    private const int MaximumCompressedBlockSize = 0xA0018;
    private const int DefaultTransformLimit = 1_000_000;

    private static readonly HashSet<string> FullTransformExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".xml",
            ".csv",
            ".crc",
            ".ini",
            ".txt"
        };

    public async ValueTask<PayloadResult> DecodeAsync(
        string archivePath,
        ArchiveEntry entry,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "The payload destination must be writable.",
                nameof(destination));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (entry.PayloadOffset < 0
            || entry.StoredSize <= 0
            || entry.DecodedSize < 0)
        {
            throw new InvalidDataException(
                "The archive entry has invalid payload bounds.");
        }

        await using var source = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (entry.PayloadOffset > source.Length
            || entry.StoredSize > source.Length - entry.PayloadOffset)
        {
            throw new InvalidDataException(
                "The archive entry payload extends beyond the archive.");
        }

        source.Position = entry.PayloadOffset;
        long compressedRemaining = entry.StoredSize;
        long transformLimit = TransformLimit(entry);
        long emitted = 0;
        bool isAllZero = true;
        byte[] pending = new byte[sizeof(uint) - 1];
        int pendingCount = 0;
        using IncrementalHash digest =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        while (compressedRemaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (compressedRemaining < sizeof(int))
            {
                throw new InvalidDataException(
                    "The compressed payload ends inside a block length.");
            }

            byte[] rawLength = new byte[sizeof(int)];
            await source.ReadExactlyAsync(rawLength, cancellationToken);
            compressedRemaining -= sizeof(int);
            uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(rawLength);
            if (blockSize is 0 or > MaximumCompressedBlockSize)
            {
                throw new InvalidDataException(
                    $"Invalid LZO block size {blockSize}.");
            }

            if (blockSize > compressedRemaining)
            {
                throw new InvalidDataException(
                    "The compressed LZO block exceeds the entry payload.");
            }

            byte[] compressed = new byte[checked((int)blockSize)];
            await source.ReadExactlyAsync(compressed, cancellationToken);
            compressedRemaining -= blockSize;
            byte[] decoded = Lzo1xDecoder.DecodeBlock(compressed);
            (emitted, pendingCount, isAllZero) = await ProcessBlockAsync(
                decoded,
                pending,
                pendingCount,
                entry,
                transformLimit,
                emitted,
                destination,
                digest,
                isAllZero,
                cancellationToken);
        }

        if (pendingCount != 0)
        {
            throw new InvalidDataException(
                "The decoded stream ended inside a transformed word.");
        }

        if (emitted != entry.DecodedSize)
        {
            throw new InvalidDataException(
                $"Decoded length mismatch: header={entry.DecodedSize}, "
                + $"actual={emitted}.");
        }

        string sha256 = Convert.ToHexString(digest.GetHashAndReset())
            .ToLowerInvariant();
        return new PayloadResult(emitted, sha256, isAllZero);
    }

    private static async ValueTask<(long Emitted, int PendingCount, bool IsAllZero)>
        ProcessBlockAsync(
            byte[] decoded,
            byte[] pending,
            int pendingCount,
            ArchiveEntry entry,
            long transformLimit,
            long emitted,
            Stream destination,
            IncrementalHash digest,
            bool isAllZero,
            CancellationToken cancellationToken)
    {
        byte[] combined = new byte[pendingCount + decoded.Length];
        pending.AsSpan(0, pendingCount).CopyTo(combined);
        decoded.CopyTo(combined, pendingCount);
        pendingCount = 0;

        int cursor = 0;
        if (emitted < transformLimit)
        {
            long transformRemaining = transformLimit - emitted;
            int transformSize = checked((int)Math.Min(
                combined.Length,
                transformRemaining));
            transformSize &= ~(sizeof(uint) - 1);
            if (transformSize != 0)
            {
                TrigWordMask.Subtract(
                    combined.AsSpan(0, transformSize),
                    entry.FileKey,
                    emitted / sizeof(uint));
                await EmitAsync(
                    combined.AsMemory(0, transformSize),
                    entry.DecodedSize,
                    destination,
                    digest,
                    emitted,
                    cancellationToken);
                isAllZero &= IsZero(combined.AsSpan(0, transformSize));
                emitted += transformSize;
                cursor = transformSize;
            }

            if (emitted < transformLimit)
            {
                int remaining = combined.Length - cursor;
                if (remaining >= sizeof(uint))
                {
                    throw new InvalidDataException(
                        "Decoded transform buffering lost dword alignment.");
                }

                combined.AsSpan(cursor).CopyTo(pending);
                return (emitted, remaining, isAllZero);
            }
        }

        if (cursor < combined.Length)
        {
            ReadOnlyMemory<byte> remainder = combined.AsMemory(cursor);
            await EmitAsync(
                remainder,
                entry.DecodedSize,
                destination,
                digest,
                emitted,
                cancellationToken);
            isAllZero &= IsZero(remainder.Span);
            emitted += remainder.Length;
        }

        return (emitted, pendingCount, isAllZero);
    }

    private static async ValueTask EmitAsync(
        ReadOnlyMemory<byte> bytes,
        long decodedSize,
        Stream destination,
        IncrementalHash digest,
        long emitted,
        CancellationToken cancellationToken)
    {
        if (bytes.Length > decodedSize - emitted)
        {
            throw new InvalidDataException(
                "The decoded payload exceeds its declared size.");
        }

        await destination.WriteAsync(bytes, cancellationToken);
        digest.AppendData(bytes.Span);
    }

    private static long TransformLimit(ArchiveEntry entry)
    {
        string extension = Path.GetExtension(entry.DecodedPath);
        long requested = FullTransformExtensions.Contains(extension)
            ? entry.DecodedSize
            : Math.Min(entry.DecodedSize, DefaultTransformLimit);
        return requested & ~(sizeof(uint) - 1L);
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }
}
