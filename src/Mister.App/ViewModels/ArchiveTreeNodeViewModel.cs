using Mister.Core.Formats;

namespace Mister.App.ViewModels;

public sealed record ArchiveEntrySource(
    ArchiveIndex SourceArchive,
    ArchiveEntry Entry,
    string SourceArchiveName);

public sealed class ArchiveTreeNodeViewModel
{
    public ArchiveTreeNodeViewModel(ArchiveEntry entry)
    {
        Entry = entry;
        SourceArchiveName = "Selected archive";
    }

    public ArchiveTreeNodeViewModel(ArchiveEntrySource source)
    {
        Entry = source.Entry;
        SourceArchive = source.SourceArchive;
        SourceArchiveName = source.SourceArchiveName;
    }

    public ArchiveEntry Entry { get; }
    public ArchiveIndex? SourceArchive { get; }
    public string SourceArchiveName { get; }
    public string DecodedPath => Entry.DecodedPath;
    public string FileName => Path.GetFileName(DecodedPath);
    public string DirectoryName => Path.GetDirectoryName(DecodedPath) ?? string.Empty;
    public string Extension => Path.GetExtension(DecodedPath);
    public string SizeLabel => FormatSize(Entry.DecodedSize);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "Unknown size",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):0.#} GB"
    };
}
