using System.Buffers.Binary;

namespace Mister.Core.Formats;

public static class BinaryPrimitivesEx
{
    public static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        EnsureAvailable(bytes.Length, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));
    }

    public static void WriteUInt32(Span<byte> bytes, int offset, uint value)
    {
        EnsureAvailable(bytes.Length, offset, sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)), value);
    }

    private static void EnsureAvailable(int length, int offset, int count)
    {
        if (offset < 0 || offset > length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }
}
