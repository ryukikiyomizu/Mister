using Mister.Core.Diagnostics;
using Mister.Core.Formats;

namespace Mister.Core.Preview;

public enum PreviewKind
{
    FolderTree,
    Text,
    Image,
    Media,
    Animation,
    Metadata,
    Hex,
    Unavailable
}

public sealed record EntryPreview(
    PreviewKind Kind,
    string? Text,
    byte[]? ImageBytes,
    IReadOnlyDictionary<string, string> Metadata,
    bool IsTruncated,
    int ContentBytes)
{
    public ToolDiagnostic? Diagnostic { get; init; }
    public VceAnimation? Animation { get; init; }
}

public sealed record ArchivePreviewTree(
    IReadOnlyList<ArchivePreviewNode> Children,
    ArchiveHealth ArchiveHealth,
    IReadOnlyDictionary<string, long> ExtensionCounts,
    long TotalDecodedBytes,
    IReadOnlyList<ToolDiagnostic> Diagnostics);

public sealed record ArchivePreviewNode(
    string Name,
    IReadOnlyList<ArchivePreviewNode> Children,
    ArchiveEntry? Entry,
    bool HasFileFolderConflict);
