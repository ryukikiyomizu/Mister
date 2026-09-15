using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using Mister.App.ViewModels;

namespace Mister.App.Views;

public partial class ExtractionDialog : Window, INotifyPropertyChanged
{
    private string outputDirectory = string.Empty;
    private string outputHint = "Nothing is written until you confirm.";
    private bool buildMergedView = true;

    public ExtractionDialog(
        IEnumerable<LogicalPackViewModel> packs,
        LogicalPackViewModel? selectedPack)
    {
        Packs = new ObservableCollection<ExtractionPackOption>(
            packs
                .Where(static pack => pack.IsValid)
                .OrderByDescending(pack => ReferenceEquals(pack, selectedPack))
                .ThenBy(static pack => pack.Name, StringComparer.OrdinalIgnoreCase)
                .Select(pack => new ExtractionPackOption(
                    pack,
                    isSelected: true)));
        foreach (ExtractionPackOption pack in Packs)
        {
            pack.PropertyChanged += OnPackPropertyChanged;
        }

        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ExtractionPackOption> Packs { get; }

    public IReadOnlyList<string> SelectedPackSeries =>
        Packs
            .Where(static pack => pack.IsSelected)
            .Select(static pack => pack.SeriesName)
            .ToArray();

    public string SelectedCountLabel =>
        $"{Packs.Count(static pack => pack.IsSelected)} of {Packs.Count} selected";

    public bool CanConfirm =>
        !string.IsNullOrWhiteSpace(OutputDirectory)
        && Packs.Any(static pack => pack.IsSelected);

    public string OutputHint
    {
        get => outputHint;
        private set
        {
            if (string.Equals(outputHint, value, StringComparison.Ordinal)) return;
            outputHint = value;
            OnPropertyChanged();
        }
    }

    public string OutputDirectory
    {
        get => outputDirectory;
        set
        {
            if (string.Equals(outputDirectory, value, StringComparison.Ordinal)) return;
            outputDirectory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirm));
        }
    }

    public bool BuildMergedView
    {
        get => buildMergedView;
        set
        {
            if (buildMergedView == value) return;
            buildMergedView = value;
            OnPropertyChanged();
        }
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose the extraction output folder"
        };
        if (picker.ShowDialog(this) != true) return;
        string picked = Path.GetFullPath(picker.FolderName);
        OutputDirectory = SafeOutputDirectory(
            picked,
            Packs.SelectMany(static pack => pack.SourceArchivePaths));
        OutputHint = string.Equals(
                picked,
                OutputDirectory,
                StringComparison.OrdinalIgnoreCase)
            ? "Nothing is written until you confirm."
            : "The selected folder contains source PAKs, so output will use the Mister Extracted subfolder.";
    }

    internal static string SafeOutputDirectory(
        string selectedDirectory,
        IEnumerable<string> sourceArchivePaths)
    {
        string selected = Path.GetFullPath(selectedDirectory);
        string prefix =
            Path.TrimEndingDirectorySeparator(selected)
            + Path.DirectorySeparatorChar;
        bool containsSource = sourceArchivePaths
            .Select(Path.GetFullPath)
            .Any(path => path.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase));
        return containsSource
            ? Path.Combine(selected, "Mister Extracted")
            : selected;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (ExtractionPackOption pack in Packs)
        {
            pack.IsSelected = true;
        }
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        foreach (ExtractionPackOption pack in Packs)
        {
            pack.IsSelected = false;
        }
    }

    private void OnPackPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ExtractionPackOption.IsSelected)) return;
        OnPropertyChanged(nameof(SelectedCountLabel));
        OnPropertyChanged(nameof(SelectedPackSeries));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!CanConfirm) return;
        DialogResult = true;
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}

public sealed class ExtractionPackOption : INotifyPropertyChanged
{
    private bool isSelected;

    public ExtractionPackOption(
        LogicalPackViewModel pack,
        bool isSelected)
    {
        SeriesName = pack.SeriesName;
        Name = pack.Name;
        LayerSummary = pack.LayerSummary;
        FileCount = pack.FileCount;
        SourceArchivePaths = pack.Members
            .Select(static member => member.Path)
            .ToArray();
        this.isSelected = isSelected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SeriesName { get; }
    public string Name { get; }
    public string LayerSummary { get; }
    public int FileCount { get; }
    public IReadOnlyList<string> SourceArchivePaths { get; }
    public string FileCountLabel => $"{FileCount:N0} files";

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
