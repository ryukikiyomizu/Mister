using Mister.Core.Diagnostics;

namespace Mister.Core.Formats;

public enum GameFamily
{
    Unknown,
    Technika2,
    Technika3
}

public enum ArchiveHealth
{
    Valid,
    Warning,
    Failed
}

public sealed record ArchiveEntry(
    int Ordinal,
    string DecodedPath,
    long RecordOffset,
    long PayloadOffset,
    long StoredSize,
    long DecodedSize,
    uint FileKey,
    int KeyIndex);

public sealed record ArchiveIndex(
    string ArchivePath,
    GameFamily Family,
    long ArchiveSize,
    IReadOnlyList<ArchiveEntry> Entries,
    long FinalOffset,
    ArchiveHealth Health,
    IReadOnlyList<ToolDiagnostic> Diagnostics);
