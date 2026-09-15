using Mister.Core.Formats;

namespace Mister.Core.T3;

public static class TrigWordMask
{
    public static void Subtract(Span<byte> bytes, uint key)
    {
        Subtract(bytes, key, wordOffset: 0);
    }

    public static void Subtract(
        Span<byte> bytes,
        uint key,
        long wordOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wordOffset);
        int wordCount = bytes.Length / sizeof(uint);
        for (int index = 0; index < wordCount; index++)
        {
            double angle = (double)key + wordOffset + index;
            float maskValue = (float)((key & 1) == 0
                ? Math.Sin(angle)
                : Math.Cos(angle));
            uint mask = BitConverter.SingleToUInt32Bits(maskValue);
            int offset = index * sizeof(uint);
            uint encoded = BinaryPrimitivesEx.ReadUInt32(bytes, offset);
            BinaryPrimitivesEx.WriteUInt32(
                bytes,
                offset,
                unchecked(encoded - mask));
        }
    }
}
