using Mister.Core.Formats;

namespace Mister.App.ViewModels;

public sealed class EntryDetailsViewModel : ViewModelBase
{
    private ArchiveEntry? entry;
    private string sourceArchive = "—";
    private string signature = "Not previewed";

    public string DecodedPath => entry?.DecodedPath ?? "Select an archive entry";
    public string Ordinal => entry?.Ordinal.ToString() ?? "—";
    public string RecordOffset => entry?.RecordOffset.ToString() ?? "—";
    public string PayloadOffset => entry?.PayloadOffset.ToString() ?? "—";
    public string StoredSize => entry?.StoredSize.ToString() ?? "—";
    public string DecodedSize => entry?.DecodedSize.ToString() ?? "—";
    public string FileType => entry is null ? "—" : Path.GetExtension(entry.DecodedPath);
    public string SourceArchive => sourceArchive;

    public string Signature
    {
        get => signature;
        private set => SetProperty(ref signature, value);
    }

    public string KeyEvidence => entry is null
        ? "—"
        : $"{entry.FileKey:X8} / index {entry.KeyIndex}";

    public void Show(ArchiveEntry? value)
    {
        entry = value;
        sourceArchive = value is null ? "—" : "Selected archive";
        NotifyEntryChanged();
    }

    public void Show(ArchiveTreeNodeViewModel? value)
    {
        entry = value?.Entry;
        sourceArchive = value?.SourceArchiveName ?? "—";
        NotifyEntryChanged();
    }

    public void ShowSignature(string value) => Signature = value;

    private void NotifyEntryChanged()
    {
        OnPropertyChanged(nameof(DecodedPath));
        OnPropertyChanged(nameof(Ordinal));
        OnPropertyChanged(nameof(RecordOffset));
        OnPropertyChanged(nameof(PayloadOffset));
        OnPropertyChanged(nameof(StoredSize));
        OnPropertyChanged(nameof(DecodedSize));
        OnPropertyChanged(nameof(FileType));
        OnPropertyChanged(nameof(SourceArchive));
        OnPropertyChanged(nameof(KeyEvidence));
        Signature = "Not previewed";
    }
}
