using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Mister.App.Services;
using Mister.App.ViewModels;

namespace Mister.App.Views;

public partial class MainWindow : Window
{
    private bool shutdownStarted;
    private bool shutdownComplete;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    private async void OnBrowseClient(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Client executable (*.exe)|*.exe|All files (*.*)|*.*", Title = "Select the DJMAX Technika client" };
        if (picker.ShowDialog(this) == true) await ViewModel.SelectClientAsync(picker.FileName);
    }

    private async void OnBrowsePaks(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "PAK archives (*.pak)|*.pak|All files (*.*)|*.*", Multiselect = true, Title = "Add PAK archives" };
        if (picker.ShowDialog(this) == true) await ViewModel.AddArchivesAsync(picker.FileNames);
    }

    private async void OnBrowsePakFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select a folder containing PAK archives" };
        if (picker.ShowDialog(this) == true) await ViewModel.AddPakFolderAsync(picker.FolderName);
    }

    private async void OnBrowsePakKeys(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select the folder containing T3 pakkeys" };
        if (picker.ShowDialog(this) == true) await ViewModel.SetPakKeyDirectoryAsync(picker.FolderName);
    }

    private async void OnRecoverAllT3PakKeys(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose an empty folder for recovered T3 pakkeys and coverage reports"
        };
        if (picker.ShowDialog(this) == true)
        {
            await ViewModel.RecoverAllT3PakKeysAsync(picker.FolderName);
        }
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Select a local work directory for T2 capture bundles" };
        if (picker.ShowDialog(this) == true) ViewModel.SetOutputDirectory(picker.FolderName);
    }

    private async void OnUseCaptureBundle(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Mister capture bundle (*.ttcapture)|*.ttcapture", Title = "Use an existing T2 capture bundle" };
        if (picker.ShowDialog(this) == true) await ViewModel.UseCaptureBundleAsync(picker.FileName);
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        var dialog = new ExtractionDialog(
            ViewModel.PackGroups,
            ViewModel.SelectedPack)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            await ViewModel.ExtractAsync(
                dialog.OutputDirectory,
                dialog.BuildMergedView,
                dialog.SelectedPackSeries);
        }
    }

    private async void OnRecoverPakKey(object sender, RoutedEventArgs e)
    {
        await ViewModel.RecoverPakKeyCommand.ExecuteAsync();
        if (ViewModel.RecoveryCoverage is null) return;
        var dialog = new PakKeyRecoveryDialog(ViewModel.RecoveryCoverage)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true
            && dialog.ExportDirectory is not null)
        {
            await ViewModel.ExportRecoveredPakKeyAsync(dialog.ExportDirectory);
        }
    }

    private void OnPreviewFocusChanged(object sender, RoutedEventArgs e)
    {
        bool focused = FocusPreviewToggle.IsChecked == true;
        LibraryPanel.Visibility = focused ? Visibility.Collapsed : Visibility.Visible;
        LibraryColumn.Width = focused ? new GridLength(0) : new GridLength(350);
        LibraryGapColumn.Width = focused ? new GridLength(0) : new GridLength(18);
    }

    private void OnPixelPerfectImageLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Image image) ApplyPixelPerfectSize(image);
    }

    private void OnPixelPerfectImageTargetUpdated(
        object sender,
        DataTransferEventArgs e)
    {
        if (sender is Image image) ApplyPixelPerfectSize(image);
    }

    private static void ApplyPixelPerfectSize(Image image)
    {
        if (image.Source is not BitmapSource bitmap)
        {
            image.ClearValue(WidthProperty);
            image.ClearValue(HeightProperty);
            return;
        }

        Size size = PixelPerfectSize(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            VisualTreeHelper.GetDpi(image));
        image.Width = size.Width;
        image.Height = size.Height;
    }

    internal static Size PixelPerfectSize(
        int pixelWidth,
        int pixelHeight,
        DpiScale dpi) =>
        new(
            pixelWidth / Math.Max(dpi.DpiScaleX, double.Epsilon),
            pixelHeight / Math.Max(dpi.DpiScaleY, double.Epsilon));

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete) return;
        e.Cancel = true;
        if (shutdownStarted) return;
        shutdownStarted = true;
        IsEnabled = false;
        MediaPlayerPreview.StopAndRelease();
        VcePlayerPreview.StopAndRelease();
        await ViewModel.ShutdownAsync();
        shutdownComplete = true;
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        MediaPlayerPreview.Dispose();
        VcePlayerPreview.StopAndRelease();
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        try
        {
            foreach (string path in paths)
            {
                switch (InputDiscoveryService.ClassifyDropPath(path))
                {
                    case DroppedInputKind.Client:
                        await ViewModel.SelectClientAsync(path);
                        break;
                    case DroppedInputKind.Pak:
                        await ViewModel.AddArchivesAsync([path]);
                        break;
                    case DroppedInputKind.PakFolder:
                        await ViewModel.AddPakFolderAsync(path);
                        break;
                    case DroppedInputKind.PakKeyDirectory:
                        await ViewModel.SetPakKeyDirectoryAsync(path);
                        break;
                    case DroppedInputKind.CaptureBundle:
                        await ViewModel.UseCaptureBundleAsync(path);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ViewModel.ReportInputError($"The dropped input could not be read safely: {exception.Message}");
        }
    }
}
