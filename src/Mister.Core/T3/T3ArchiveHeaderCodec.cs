using Mister.Core.Diagnostics;
using Mister.Core.Formats;

namespace Mister.Core.T3;

public sealed record T3ArchiveHeader(
    uint ArchiveKey,
    long FirstRecordOffset,
    int EntryCount,
    byte InitialSeed,
    byte CompressionFlag);

public static class T3ArchiveHeaderCodec
{
    private const uint HeaderChain = 13 ^ 0x18385868;

    public static ToolResult<T3ArchiveHeader> Decode(ReadOnlySpan<byte> rawHeader)
    {
        if (rawHeader.Length != T3Constants.HeaderSize)
        {
            return Failure(
                DiagnosticCode.InvalidHeader,
                $"The T3 archive header must be exactly {T3Constants.HeaderSize} bytes.");
        }

        uint archiveKey = BinaryPrimitivesEx.ReadUInt32(rawHeader, 0);
        Span<byte> body = stackalloc byte[T3Constants.HeaderSize - sizeof(uint)];
        rawHeader[sizeof(uint)..].CopyTo(body);
        TrigWordMask.Subtract(body, archiveKey);

        uint firstRecordOffset =
            BinaryPrimitivesEx.ReadUInt32(body, 0) ^ HeaderChain;
        BinaryPrimitivesEx.WriteUInt32(body, 0, firstRecordOffset);
        uint second =
            BinaryPrimitivesEx.ReadUInt32(body, sizeof(uint)) ^ firstRecordOffset;
        BinaryPrimitivesEx.WriteUInt32(body, sizeof(uint), second);

        uint entryCount = BinaryPrimitivesEx.ReadUInt32(body, 6);
        if (entryCount == 0 || entryCount > int.MaxValue)
        {
            return Failure(
                DiagnosticCode.InvalidHeader,
                "The T3 archive header contains an invalid entry count.");
        }

        byte compressionFlag = body[11];
        if (compressionFlag != 1)
        {
            return Failure(
                DiagnosticCode.InvalidCompressionFlag,
                $"Unsupported T3 archive compression flag {compressionFlag}.");
        }

        return ToolResult<T3ArchiveHeader>.Success(
            new T3ArchiveHeader(
                archiveKey,
                firstRecordOffset,
                checked((int)entryCount),
                body[10],
                compressionFlag));
    }

    private static ToolResult<T3ArchiveHeader> Failure(
        DiagnosticCode code,
        string message) =>
        ToolResult<T3ArchiveHeader>.Failure(new ToolDiagnostic(code, message));
}
