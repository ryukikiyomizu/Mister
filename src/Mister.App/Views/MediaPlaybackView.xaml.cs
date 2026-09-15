using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Mister.App.Views;

public partial class MediaPlaybackView : UserControl, IDisposable
{
    public static readonly DependencyProperty SourcePathProperty =
        DependencyProperty.Register(
            nameof(SourcePath),
            typeof(string),
            typeof(MediaPlaybackView),
            new PropertyMetadata(null, OnSourcePathChanged));

    private LibVLC? libVlc;
    private MediaPlayer? mediaPlayer;
    private Media? media;
    private bool seeking;
    private bool disposed;
    private bool isAudioSource = true;
    private uint videoWidth = 16;
    private uint videoHeight = 9;

    public MediaPlaybackView()
    {
        InitializeComponent();
    }

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    internal bool ControlsAreOutsideVideoSurface =>
        ReferenceEquals(PlaybackControls.Parent, VideoSurface.Parent)
        && Grid.GetRow(VideoSurface) == 0
        && Grid.GetRow(PlaybackControls) == 1;

    internal static double AdaptiveHeight(
        double availableWidth,
        uint sourceWidth,
        uint sourceHeight,
        double controlsHeight)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return 480;
        }
        double ratio = sourceWidth > 0 && sourceHeight > 0
            ? sourceWidth / (double)sourceHeight
            : 16d / 9d;
        return Math.Ceiling(availableWidth / ratio + Math.Max(0, controlsHeight));
    }

    public void StopAndRelease()
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.Stop();
            mediaPlayer.Media = null;
        }
        media?.Dispose();
        media = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SetOverlayVisible(false);
        StopAndRelease();
        if (mediaPlayer is not null)
        {
            mediaPlayer.TimeChanged -= OnTimeChanged;
            mediaPlayer.LengthChanged -= OnLengthChanged;
            mediaPlayer.Playing -= OnPlaying;
            mediaPlayer.Paused -= OnPaused;
            mediaPlayer.Stopped -= OnStopped;
            mediaPlayer.EndReached -= OnEndReached;
            mediaPlayer.EncounteredError -= OnEncounteredError;
        }
        VideoSurface.MediaPlayer = null;
        mediaPlayer?.Dispose();
        libVlc?.Dispose();
        mediaPlayer = null;
        libVlc = null;
    }

    private static void OnSourcePathChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is MediaPlaybackView view
            && view.IsLoaded
            && view.IsVisible)
        {
            view.LoadSource(args.NewValue as string);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetOverlayVisible(IsVisible);
        if (IsVisible)
        {
            LoadSource(SourcePath);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SetOverlayVisible(false);
        StopAndRelease();
        VideoSurface.MediaPlayer = null;
    }

    private void OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (IsVisible)
        {
            SetOverlayVisible(true);
            LoadSource(SourcePath);
        }
        else
        {
            SetOverlayVisible(false);
            StopAndRelease();
            VideoSurface.MediaPlayer = null;
        }
    }

    private void SetOverlayVisible(bool visible)
    {
        VideoOverlay.Visibility =
            visible ? Visibility.Visible : Visibility.Collapsed;
        VideoOverlay.IsHitTestVisible = visible;
    }

    private void EnsurePlayer()
    {
        if (mediaPlayer is not null)
        {
            VideoSurface.MediaPlayer = mediaPlayer;
            return;
        }
        _ = LibVlcBootstrap.Initialize();
        libVlc = new LibVLC(
            "--no-video-title-show",
            "--quiet");
        mediaPlayer = new MediaPlayer(libVlc);
        mediaPlayer.TimeChanged += OnTimeChanged;
        mediaPlayer.LengthChanged += OnLengthChanged;
        mediaPlayer.Playing += OnPlaying;
        mediaPlayer.Paused += OnPaused;
        mediaPlayer.Stopped += OnStopped;
        mediaPlayer.EndReached += OnEndReached;
        mediaPlayer.EncounteredError += OnEncounteredError;
        mediaPlayer.Volume = (int)VolumeSlider.Value;
        VideoSurface.MediaPlayer = mediaPlayer;
    }

    private void LoadSource(string? path)
    {
        StopAndRelease();
        PositionSlider.Value = 0;
        PositionSlider.Maximum = 1;
        ElapsedText.Text = "00:00";
        DurationText.Text = "00:00";
        PlayPauseButton.Content = "Play";
        MediaNameText.Text = string.Empty;
        videoWidth = 16;
        videoHeight = 9;
        SetAudioStage(isAudio: true);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PlaybackStatusText.Text = "No playable media is selected.";
            return;
        }

        try
        {
            EnsurePlayer();
            media = new Media(libVlc!, new Uri(path));
            mediaPlayer!.Media = media;
            MediaNameText.Text = Path.GetFileName(path);
            SetAudioStage(IsAudio(path));
            PlaybackStatusText.Text = "Ready to play.";
        }
        catch (Exception exception) when (
            exception is VLCException
                or UriFormatException
                or InvalidOperationException)
        {
            PlaybackStatusText.Text =
                $"Player initialization failed: {exception.Message}";
        }
    }

    private void SetAudioStage(bool isAudio)
    {
        isAudioSource = isAudio;
        AudioPlaceholder.Visibility =
            isAudio ? Visibility.Visible : Visibility.Collapsed;
        MediaBackdrop.Background = isAudio
            ? new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17))
            : new WpfSolidColorBrush(WpfColor.FromArgb(1, 0, 0, 0));
        ApplyAdaptiveHeight();
    }

    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ApplyAdaptiveHeight();
    }

    private void ApplyAdaptiveHeight()
    {
        if (!IsLoaded) return;
        if (isAudioSource)
        {
            Height = 480;
            return;
        }

        double width = ActualWidth > 0 ? ActualWidth : 640;
        double controlsHeight =
            PlaybackControls.ActualHeight > 0
                ? PlaybackControls.ActualHeight
                : 92;
        double height = AdaptiveHeight(
            width,
            videoWidth,
            videoHeight,
            controlsHeight);
        if (Math.Abs(Height - height) > 0.5) Height = height;
    }

    private void RefreshVideoDimensions()
    {
        if (mediaPlayer is null || isAudioSource) return;
        uint width = 0;
        uint height = 0;
        if (!mediaPlayer.Size(0, ref width, ref height)
            || width == 0
            || height == 0)
        {
            return;
        }
        videoWidth = width;
        videoHeight = height;
        ApplyAdaptiveHeight();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (mediaPlayer?.Media is null) return;
        if (mediaPlayer.IsPlaying)
        {
            mediaPlayer.Pause();
        }
        else
        {
            _ = mediaPlayer.Play();
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        mediaPlayer?.Stop();
        if (mediaPlayer is not null) mediaPlayer.Time = 0;
    }

    private void OnSeekStarted(object sender, MouseButtonEventArgs e) =>
        seeking = true;

    private void OnSeekCompleted(object sender, MouseButtonEventArgs e)
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.Time = (long)PositionSlider.Value;
        }
        seeking = false;
    }

    private void OnVolumeChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.Volume = (int)e.NewValue;
        }
    }

    private void OnTimeChanged(
        object? sender,
        MediaPlayerTimeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!seeking) PositionSlider.Value = Math.Max(0, e.Time);
            ElapsedText.Text = FormatTime(e.Time);
        });

    private void OnLengthChanged(
        object? sender,
        MediaPlayerLengthChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            PositionSlider.Maximum = Math.Max(1, e.Length);
            DurationText.Text = FormatTime(e.Length);
        });

    private void OnPlaying(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            RefreshVideoDimensions();
            PlayPauseButton.Content = "Pause";
            PlaybackStatusText.Text = "Playing";
        });

    private void OnPaused(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            PlayPauseButton.Content = "Play";
            PlaybackStatusText.Text = "Paused";
        });

    private void OnStopped(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            PlayPauseButton.Content = "Play";
            PlaybackStatusText.Text = "Stopped";
        });

    private void OnEndReached(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (mediaPlayer is null) return;
            if (LoopCheckBox.IsChecked == true)
            {
                mediaPlayer.Stop();
                mediaPlayer.Time = 0;
                _ = mediaPlayer.Play();
            }
            else
            {
                PlayPauseButton.Content = "Play";
                PlaybackStatusText.Text = "Finished";
            }
        });

    private void OnEncounteredError(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            PlayPauseButton.Content = "Play";
            PlaybackStatusText.Text =
                "This media could not be decoded by the bundled player.";
        });

    private static bool IsAudio(string path) =>
        Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".m4a", StringComparison.OrdinalIgnoreCase);

    private static string FormatTime(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }
}

internal static class LibVlcBootstrap
{
    private static readonly object Sync = new();
    private static string? nativeDirectory;

    public static string Initialize()
    {
        lock (Sync)
        {
            if (nativeDirectory is not null) return nativeDirectory;
            nativeDirectory = FindNativeDirectory()
                ?? throw new VLCException(
                    "The bundled LibVLC runtime could not be located.");
            LibVLCSharp.Shared.Core.Initialize(nativeDirectory);
            return nativeDirectory;
        }
    }

    internal static string? FindNativeDirectory()
    {
        foreach (string root in CandidateRoots())
        {
            string direct = Path.Combine(root, "libvlc", "win-x64");
            if (File.Exists(Path.Combine(direct, "libvlc.dll")))
            {
                return direct;
            }
            if (File.Exists(Path.Combine(root, "libvlc.dll")))
            {
                return root;
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? candidate in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(Environment.ProcessPath)
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && seen.Add(candidate))
            {
                yield return candidate;
            }
        }

        string? nativeSearchDirectories =
            AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string;
        if (string.IsNullOrWhiteSpace(nativeSearchDirectories)) yield break;
        foreach (string candidate in nativeSearchDirectories.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(candidate)) yield return candidate;
        }
    }
}
