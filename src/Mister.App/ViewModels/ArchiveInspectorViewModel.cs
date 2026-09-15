using System.Collections.ObjectModel;
using Mister.Core.Formats;

namespace Mister.App.ViewModels;

public sealed class ArchiveInspectorViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ArchiveTreeNodeViewModel> allEntries;
    private string searchText = string.Empty;
    private string extensionFilter = string.Empty;
    private ArchiveTreeNodeViewModel? selectedEntry;

    public ArchiveInspectorViewModel(IEnumerable<ArchiveEntry> entries)
    {
        allEntries = entries.Select(static entry => new ArchiveTreeNodeViewModel(entry)).ToArray();
        VisibleEntries = new ObservableCollection<ArchiveTreeNodeViewModel>();
        Details = new EntryDetailsViewModel();
        ApplyFilter();
    }

    public ArchiveInspectorViewModel(IEnumerable<ArchiveEntrySource> entries)
    {
        allEntries = entries.Select(static entry => new ArchiveTreeNodeViewModel(entry)).ToArray();
        VisibleEntries = new ObservableCollection<ArchiveTreeNodeViewModel>();
        Details = new EntryDetailsViewModel();
        ApplyFilter();
    }

    public ObservableCollection<ArchiveTreeNodeViewModel> VisibleEntries { get; }
    public EntryDetailsViewModel Details { get; }

    public string SearchText
    {
        get => searchText;
        set { if (SetProperty(ref searchText, value ?? string.Empty)) ApplyFilter(); }
    }

    public string ExtensionFilter
    {
        get => extensionFilter;
        set { if (SetProperty(ref extensionFilter, value ?? string.Empty)) ApplyFilter(); }
    }

    public ArchiveTreeNodeViewModel? SelectedEntry
    {
        get => selectedEntry;
        set { if (SetProperty(ref selectedEntry, value)) Details.Show(value); }
    }

    private void ApplyFilter()
    {
        IEnumerable<ArchiveTreeNodeViewModel> entries = allEntries;
        if (!string.IsNullOrWhiteSpace(searchText))
            entries = entries.Where(item => item.DecodedPath.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(extensionFilter))
            entries = entries.Where(item => string.Equals(item.Extension, extensionFilter, StringComparison.OrdinalIgnoreCase));
        VisibleEntries.Clear();
        foreach (ArchiveTreeNodeViewModel entry in entries) VisibleEntries.Add(entry);
        if (selectedEntry is not null && !VisibleEntries.Contains(selectedEntry)) SelectedEntry = null;
    }

    public void ApplyPreview(Mister.Core.Preview.EntryPreview preview)
    {
        if (preview.Metadata.TryGetValue("signature", out string? signature))
        {
            Details.ShowSignature(signature);
        }
    }
}
