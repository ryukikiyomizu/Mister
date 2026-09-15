using System.Security.Cryptography;
using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Formats;

namespace Mister.Core.T2;

public sealed class T2PayloadDecoder : IPayloadDecoder
{
    private const int BufferSize = 64 * 1024;

    private static readonly HashSet<string> FullTransformExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt",
            ".ini",
            ".crc",
            ".csv",
            ".xml"
        };

    private readonly uint[] _subtractTable;

    public T2PayloadDecoder(ClientEvidence clientEvidence)
    {
        ArgumentNullException.ThrowIfNull(clientEvidence);
        if (clientEvidence.Family != GameFamily.Technika2)
        {
            throw new ArgumentException(
                "T2 payload decoding requires Technika 2 client evidence.",
                nameof(clientEvidence));
        }

        if (clientEvidence.T2SubtractTable is null
            || clientEvidence.T2SubtractTable.Length
                != T2Constants.SubtractTableSize)
        {
            throw new ArgumentException(
                $"The T2 subtraction table must contain exactly {T2Constants.SubtractTableSize} dwords.",
                nameof(clientEvidence));
        }

        _subtractTable = clientEvidence.T2SubtractTable.ToArray();
    }

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
            || entry.StoredSize < 0
            || entry.DecodedSize < 0)
        {
            throw new InvalidDataException(
                "The archive entry has invalid payload bounds.");
        }

        if (entry.StoredSize != entry.DecodedSize)
        {
            throw new InvalidDataException(
                $"{DiagnosticCode.UnsupportedT2Compression}: T2 payload decoding requires equal stored and decoded sizes, but {entry.DecodedPath} declares {entry.StoredSize} stored bytes and {entry.DecodedSize} decoded bytes.");
        }

        await using var source = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (entry.PayloadOffset > source.Length
            || entry.StoredSize > source.Length - entry.PayloadOffset)
        {
            throw new InvalidDataException(
                "The archive entry payload extends beyond the archive.");
        }

        source.Position = entry.PayloadOffset;
        long transformSize = FullTransformExtensions.Contains(
            Path.GetExtension(entry.DecodedPath))
            ? entry.DecodedSize
            : Math.Min(
                entry.DecodedSize,
                T2Constants.PayloadPrefixTransformSize);
        long transformedBytes = transformSize & ~3L;
        int tableIndex = checked((int)(transformSize & byte.MaxValue));
        long remaining = entry.StoredSize;
        long emitted = 0;
        bool isAllZero = true;
        byte[] buffer = new byte[BufferSize];
        using IncrementalHash digest =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = checked((int)Math.Min(buffer.Length, remaining));
            await source.ReadExactlyAsync(
                buffer.AsMemory(0, count),
                cancellationToken);

            int transformCount = checked((int)Math.Min(
                transformedBytes,
                count));
            for (int offset = 0;
                offset < transformCount;
                offset += sizeof(uint))
            {
                uint word = BinaryPrimitivesEx.ReadUInt32(buffer, offset);
                BinaryPrimitivesEx.WriteUInt32(
                    buffer,
                    offset,
                    unchecked(word - _subtractTable[tableIndex]));
                tableIndex = (tableIndex + 1) & byte.MaxValue;
            }

            transformedBytes -= transformCount;
            ReadOnlyMemory<byte> decoded = buffer.AsMemory(0, count);
            if (isAllZero && decoded.Span.IndexOfAnyExcept((byte)0) >= 0)
            {
                isAllZero = false;
            }

            digest.AppendData(decoded.Span);
            await destination.WriteAsync(decoded, cancellationToken);
            emitted += count;
            remaining -= count;
        }

        string sha256 = Convert.ToHexString(digest.GetHashAndReset())
            .ToLowerInvariant();
        return new PayloadResult(emitted, sha256, isAllZero);
    }
}
