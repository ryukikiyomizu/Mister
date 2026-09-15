using Mister.Core.Diagnostics;
using Mister.Core.Formats;

namespace Mister.Core.T3;

public static class T3EntryCodec
{
    public static ToolResult<ArchiveEntry> Decode(
        ReadOnlySpan<byte> rawRecord,
        byte recordSeed,
        ReadOnlySpan<byte> pakKey,
        ReadOnlySpan<byte> clientTable,
        int ordinal,
        long recordOffset)
    {
        if (rawRecord.Length != T3Constants.EntrySize)
        {
            return Failure(
                DiagnosticCode.RecordChainOverflow,
                $"T3 record {ordinal} is not exactly {T3Constants.EntrySize} bytes.");
        }

        if (pakKey.Length != T3Constants.PakKeySize)
        {
            return Failure(
                DiagnosticCode.PartialPakKey,
                $"The T3 pakkey must be exactly {T3Constants.PakKeySize} bytes.");
        }

        if (clientTable.Length != T3Constants.RecordXorTableSize)
        {
            return Failure(
                DiagnosticCode.MissingClientTable,
                $"The T3 client record table must be exactly {T3Constants.RecordXorTableSize} bytes.");
        }

        Span<byte> record = stackalloc byte[T3Constants.EntrySize];
        rawRecord.CopyTo(record);
        Span<byte> preserved = stackalloc byte[8];
        record.Slice(4, preserved.Length).CopyTo(preserved);

        uint lowIndex = BinaryPrimitivesEx.ReadUInt32(record, 4);
        uint highIndex = BinaryPrimitivesEx.ReadUInt32(record, 8);
        int keyIndex = checked((int)(((ulong)highIndex << 32 | lowIndex)
            % T3Constants.EffectivePakKeySize));
        byte pakKeyByte = pakKey[keyIndex];

        for (int offset = 0; offset < record.Length; offset += sizeof(uint))
        {
            record[offset] ^= pakKeyByte;
        }

        preserved.CopyTo(record[4..12]);
        for (int offset = 0; offset < record.Length; offset += sizeof(uint))
        {
            record[offset] ^=
                clientTable[(recordSeed + offset) & byte.MaxValue];
        }

        uint fileKey = BinaryPrimitivesEx.ReadUInt32(record, 0);
        Span<byte> pathField = stackalloc byte[T3Constants.PathSize];
        record.Slice(T3Constants.PathOffset, T3Constants.PathSize)
            .CopyTo(pathField);
        TrigWordMask.Subtract(pathField, fileKey);

        ToolResult<string> pathResult =
            Cp949PathDecoder.Decode(
                pathField,
                requireCcFiller: true,
                allowPatternRoot: true);
        if (!pathResult.IsSuccess)
        {
            return ToolResult<ArchiveEntry>.Failure(pathResult.Error!);
        }

        ReadOnlySpan<byte> tail = record.Slice(T3Constants.TailOffset, 8);
        uint storedSize =
            tail[3]
            | ((uint)tail[5] << 8)
            | ((uint)tail[1] << 16)
            | ((uint)tail[7] << 24);
        uint decodedSize =
            tail[6]
            | ((uint)tail[0] << 8)
            | ((uint)tail[4] << 16)
            | ((uint)tail[2] << 24);
        if (storedSize == 0 || decodedSize == 0)
        {
            return Failure(
                DiagnosticCode.RecordChainOverflow,
                $"T3 record {ordinal} contains a zero file size.");
        }

        long payloadOffset = checked(recordOffset + T3Constants.EntrySize);
        return ToolResult<ArchiveEntry>.Success(
            new ArchiveEntry(
                ordinal,
                pathResult.Value!,
                recordOffset,
                payloadOffset,
                storedSize,
                decodedSize,
                fileKey,
                keyIndex));
    }

    private static ToolResult<ArchiveEntry> Failure(
        DiagnosticCode code,
        string message) =>
        ToolResult<ArchiveEntry>.Failure(new ToolDiagnostic(code, message));
}
