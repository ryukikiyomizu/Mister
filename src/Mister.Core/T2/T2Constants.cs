namespace Mister.Core.T2;

internal static class T2Constants
{
    internal const int HeaderOffset = 30;
    internal const int HeaderSize = 13;
    internal const int RecordSize = 0x94;
    internal const int PathOffset = 8;
    internal const int PathSize = 128;
    internal const int SubtractTableSize = 256;
    internal const int RecordXorTableSize = 256;
    internal const uint FirstRecordMask = 0x18385865;
    internal const int PayloadPrefixTransformSize = 60;
}
