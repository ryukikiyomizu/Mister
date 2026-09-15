namespace Mister.Core.T3;

internal static class T3Constants
{
    internal const int HeaderOffset = 0x20;
    internal const int HeaderSize = 17;
    internal const int EntrySize = 0x98;
    internal const int PathOffset = 0x0C;
    internal const int PathSize = 0x80;
    internal const int TailOffset = 0x8C;
    internal const int PakKeySize = 128;
    internal const int EffectivePakKeySize = 125;
    internal const int RecordXorTableSize = 256;
}
