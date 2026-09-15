using System.Windows;
using Microsoft.Win32;
using Mister.Core.T3;

namespace Mister.App.Views;

public partial class PakKeyRecoveryDialog : Window
{
    public PakKeyRecoveryDialog(PakKeyCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        InitializeComponent();
        RecoveredCount = coverage.Recovered.Count;
        CoverageLabel = coverage.HasCompleteEffectiveCoverage
            ? "Complete effective coverage"
            : coverage.IsUsable
                ? "Usable for the selected archives; effective coverage is partial"
                : "Not usable; unresolved or conflicting positions remain";
        DataContext = this;
    }

    public int RecoveredCount { get; }
    public string CoverageLabel { get; }
    public string? ExportDirectory { get; private set; }

    private void OnChooseExport(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose where to export the recovered pakkey and coverage report"
        };
        if (picker.ShowDialog(this) != true) return;
        ExportDirectory = picker.FolderName;
        DialogResult = true;
    }
}
