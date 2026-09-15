namespace Mister.Core.T3;

public sealed class LzoCorruptionException : IOException
{
    public LzoCorruptionException(string message)
        : base(message)
    {
    }
}

public static class Lzo1xDecoder
{
    private const int MaximumDecodedBlockSize = 0xA0000;

    public static byte[] DecodeBlock(ReadOnlySpan<byte> data)
    {
        var decoder = new Decoder(data);
        return decoder.Decode();
    }

    private ref struct Decoder
    {
        private readonly ReadOnlySpan<byte> _input;
        private readonly List<byte> _output;
        private int _cursor;
        private int _state;

        public Decoder(ReadOnlySpan<byte> input)
        {
            _input = input;
            _output = [];
            _cursor = 0;
            _state = 0;
        }

        public byte[] Decode()
        {
            if (_input.Length < 3)
            {
                throw Corruption("LZO block is too small.");
            }

            int token = ReadByte();
            if (token > 17)
            {
                int literalCount = token - 17;
                if (literalCount < 4)
                {
                    _state = literalCount;
                    CopyLiterals(literalCount);
                }
                else
                {
                    CopyLiterals(literalCount);
                    _state = 4;
                }
            }
            else
            {
                _cursor--;
            }

            while (true)
            {
                token = ReadByte();
                int nextLiterals;
                int matchPosition;
                int matchLength;

                if (token < 16)
                {
                    if (_state == 0)
                    {
                        int literalCount = token == 0
                            ? ExtendedLength(15)
                            : token;
                        CopyLiterals(CheckedAdd(literalCount, 3));
                        _state = 4;
                        continue;
                    }

                    if (_state != 4)
                    {
                        nextLiterals = token & 3;
                        matchPosition = _output.Count
                            - 1
                            - (token >> 2)
                            - (ReadByte() << 2);
                        CopyMatch(matchPosition, 2);
                        _state = nextLiterals;
                        CopyLiterals(nextLiterals);
                        continue;
                    }

                    nextLiterals = token & 3;
                    matchPosition = _output.Count
                        - (1 + 0x800)
                        - (token >> 2)
                        - (ReadByte() << 2);
                    matchLength = 3;
                }
                else if (token >= 64)
                {
                    nextLiterals = token & 3;
                    matchPosition = _output.Count
                        - 1
                        - ((token >> 2) & 7)
                        - (ReadByte() << 3);
                    matchLength = (token >> 5) + 1;
                }
                else if (token >= 32)
                {
                    matchLength = (token & 31) + 2;
                    if (matchLength == 2)
                    {
                        matchLength = CheckedAdd(ExtendedLength(31), 2);
                    }

                    int encodedOffset = ReadByte() | (ReadByte() << 8);
                    matchPosition = _output.Count
                        - 1
                        - (encodedOffset >> 2);
                    nextLiterals = encodedOffset & 3;
                }
                else
                {
                    matchPosition = _output.Count - ((token & 8) << 11);
                    matchLength = (token & 7) + 2;
                    if (matchLength == 2)
                    {
                        matchLength = CheckedAdd(ExtendedLength(7), 2);
                    }

                    int encodedOffset = ReadByte() | (ReadByte() << 8);
                    matchPosition -= encodedOffset >> 2;
                    nextLiterals = encodedOffset & 3;
                    if (matchPosition == _output.Count)
                    {
                        if (matchLength != 3 || _cursor != _input.Length)
                        {
                            throw Corruption("Invalid LZO end marker.");
                        }

                        return _output.ToArray();
                    }

                    matchPosition -= 0x4000;
                }

                CopyMatch(matchPosition, matchLength);
                _state = nextLiterals;
                CopyLiterals(nextLiterals);
            }
        }

        private int ReadByte()
        {
            if (_cursor >= _input.Length)
            {
                throw Corruption("LZO input overrun.");
            }

            return _input[_cursor++];
        }

        private void CopyLiterals(int count)
        {
            if (count < 0 || count > _input.Length - _cursor)
            {
                throw Corruption("LZO literal overrun.");
            }

            EnsureOutputAvailable(count);
            for (int index = 0; index < count; index++)
            {
                _output.Add(_input[_cursor + index]);
            }

            _cursor += count;
        }

        private void CopyMatch(int position, int count)
        {
            if (position < 0 || position >= _output.Count)
            {
                throw Corruption("LZO lookbehind overrun.");
            }

            EnsureOutputAvailable(count);
            for (int index = 0; index < count; index++)
            {
                _output.Add(_output[position++]);
            }
        }

        private int ExtendedLength(int basis)
        {
            int extra = 0;
            while (true)
            {
                int value = ReadByte();
                if (value != 0)
                {
                    return CheckedAdd(CheckedAdd(basis, extra), value);
                }

                extra = CheckedAdd(extra, byte.MaxValue);
            }
        }

        private void EnsureOutputAvailable(int count)
        {
            if (count < 0 || count > MaximumDecodedBlockSize - _output.Count)
            {
                throw Corruption(
                    $"LZO decoded block exceeds {MaximumDecodedBlockSize} bytes.");
            }
        }

        private static int CheckedAdd(int left, int right)
        {
            try
            {
                return checked(left + right);
            }
            catch (OverflowException)
            {
                throw Corruption("LZO length overflow.");
            }
        }

        private static LzoCorruptionException Corruption(string message) =>
            new(message);
    }
}
