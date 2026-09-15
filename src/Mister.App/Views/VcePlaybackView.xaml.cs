using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mister.App.ViewModels;
using Mister.Core.Preview;

namespace Mister.App.Views;

public partial class VcePlaybackView : UserControl
{
    public static readonly DependencyProperty PlaybackProperty =
        DependencyProperty.Register(
            nameof(Playback),
            typeof(VcePlaybackViewModel),
            typeof(VcePlaybackView),
            new PropertyMetadata(null, OnPlaybackChanged));

    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = new();
    private readonly List<LayerRuntime> layers = [];
    private int currentFrame;
    private int startingFrame;
    private bool playing;
    private bool seeking;
    private bool internalSliderUpdate;
    private double speed = 1;

    public VcePlaybackView()
    {
        timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            OnTick,
            Dispatcher);
        timer.Stop();
        InitializeComponent();
    }

    public VcePlaybackViewModel? Playback
    {
        get => (VcePlaybackViewModel?)GetValue(PlaybackProperty);
        set => SetValue(PlaybackProperty, value);
    }

    public void StopAndRelease()
    {
        Pause();
        AnimationCanvas.Children.Clear();
        layers.Clear();
    }

    private static void OnPlaybackChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is VcePlaybackView view)
        {
            view.Build(args.NewValue as VcePlaybackViewModel);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Playback is not null && layers.Count == 0) Build(Playback);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Pause();

    private void Build(VcePlaybackViewModel? playback)
    {
        StopAndRelease();
        currentFrame = 0;
        FrameSlider.Value = 0;
        if (playback is null)
        {
            AnimationStatusText.Text = "No VCE animation is selected.";
            return;
        }

        FrameSlider.Maximum = Math.Max(
            1,
            playback.Animation.MaximumFrame);
        MaximumFrameText.Text =
            playback.Animation.MaximumFrame.ToString(
                CultureInfo.InvariantCulture);
        AnimationStatusText.Text = playback.Summary;
        for (int index = 0; index < playback.Layers.Count; index++)
        {
            VcePlaybackLayerViewModel layer = playback.Layers[index];
            if (layer.Layer.Keyframes.Count == 0
                || layer.Textures.Count == 0)
            {
                continue;
            }
            var image = new Image
            {
                Stretch = Stretch.Fill,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true
            };
            Panel.SetZIndex(image, index);
            AnimationCanvas.Children.Add(image);
            layers.Add(new LayerRuntime(
                image,
                layer.Textures,
                BuildFrames(
                    layer.Layer,
                    playback.Animation.MaximumFrame)));
        }

        ShowFrame(0);
        Play();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        VcePlaybackViewModel? playback = Playback;
        if (!playing || playback is null) return;
        int frameCount = playback.Animation.MaximumFrame + 1;
        double elapsedFrames =
            clock.Elapsed.TotalSeconds
            * playback.Animation.FramesPerSecond
            * speed;
        int frame = startingFrame + (int)elapsedFrames;
        if (frame >= frameCount)
        {
            if (LoopCheckBox.IsChecked == true)
            {
                frame %= frameCount;
                startingFrame = frame;
                clock.Restart();
            }
            else
            {
                frame = playback.Animation.MaximumFrame;
                Pause();
            }
        }
        ShowFrame(frame);
    }

    private void ShowFrame(int frame)
    {
        if (Playback is null) return;
        currentFrame = Math.Clamp(
            frame,
            0,
            Playback.Animation.MaximumFrame);
        foreach (LayerRuntime layer in layers)
        {
            VceFrameState state = layer.Frames[currentFrame];
            int textureIndex = state.TextureIndex;
            BitmapSource? source =
                textureIndex >= 0
                && textureIndex < layer.Textures.Count
                    ? layer.Textures[textureIndex].Image
                    : null;
            if (!state.Visible || source is null)
            {
                layer.Image.Visibility = Visibility.Collapsed;
                continue;
            }

            double minX = state.PointX.Min();
            double maxX = state.PointX.Max();
            double minY = state.PointY.Min();
            double maxY = state.PointY.Max();
            double width = maxX - minX;
            double height = maxY - minY;
            if (width <= 0) width = source.PixelWidth;
            if (height <= 0) height = source.PixelHeight;
            layer.Image.Source = source;
            layer.Image.Width = width;
            layer.Image.Height = height;
            layer.Image.Opacity = Math.Clamp(state.Alpha / 255d, 0, 1);
            layer.Image.RenderTransform = new RotateTransform(
                state.Rotation);
            Canvas.SetLeft(layer.Image, state.PositionX + minX);
            Canvas.SetTop(layer.Image, state.PositionY + minY);
            layer.Image.Visibility = Visibility.Visible;
        }

        internalSliderUpdate = true;
        FrameSlider.Value = currentFrame;
        internalSliderUpdate = false;
        CurrentFrameText.Text =
            currentFrame.ToString(CultureInfo.InvariantCulture);
    }

    private void Play()
    {
        if (Playback is null) return;
        startingFrame = currentFrame;
        clock.Restart();
        playing = true;
        timer.Start();
        PlayPauseButton.Content = "Pause";
    }

    private void Pause()
    {
        if (playing)
        {
            double elapsedFrames =
                clock.Elapsed.TotalSeconds
                * (Playback?.Animation.FramesPerSecond ?? 0)
                * speed;
            currentFrame = Math.Min(
                Playback?.Animation.MaximumFrame ?? 0,
                startingFrame + (int)elapsedFrames);
        }
        playing = false;
        timer.Stop();
        clock.Stop();
        PlayPauseButton.Content = "Play";
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (playing) Pause();
        else Play();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        bool resume = playing;
        Pause();
        ShowFrame(0);
        if (resume) Play();
    }

    private void OnSeekStarted(object sender, MouseButtonEventArgs e)
    {
        seeking = true;
        Pause();
    }

    private void OnSeekCompleted(object sender, MouseButtonEventArgs e)
    {
        ShowFrame((int)Math.Round(FrameSlider.Value));
        seeking = false;
    }

    private void OnFrameChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!internalSliderUpdate && seeking)
        {
            ShowFrame((int)Math.Round(e.NewValue));
        }
    }

    private void OnSpeedChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (SpeedPicker.SelectedItem is ComboBoxItem item
            && item.Tag is string text
            && double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value))
        {
            bool resume = playing;
            Pause();
            speed = value;
            if (resume) Play();
        }
    }

    private void OnZoomChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ZoomPicker.SelectedItem is ComboBoxItem item
            && item.Tag is string text
            && double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value))
        {
            AnimationCanvas.RenderTransform =
                new ScaleTransform(value, value);
        }
    }

    private static IReadOnlyList<VceFrameState> BuildFrames(
        VceLayer layer,
        int maximumFrame)
    {
        var frames = new VceFrameState[maximumFrame + 1];
        var state = MutableFrameState.Hidden();
        var increment = MutableFrameState.Zero();
        int outputFrame = 0;
        bool hasAbsoluteState = false;
        foreach (VceKeyframe keyframe in layer.Keyframes)
        {
            int keyFrame = Math.Clamp(
                keyframe.Frame,
                0,
                maximumFrame);
            if (keyframe.Kind == 0)
            {
                while (outputFrame < keyFrame)
                {
                    if (hasAbsoluteState) state.Advance(increment, layer.Textures.Count);
                    frames[outputFrame++] = state.Snapshot(hasAbsoluteState);
                }
                state.ApplyAbsolute(keyframe);
                hasAbsoluteState = true;
                frames[keyFrame] = state.Snapshot(visible: true);
                outputFrame = Math.Max(outputFrame, keyFrame + 1);
            }
            else
            {
                increment.ApplyIncrement(keyframe);
            }
        }
        while (outputFrame <= maximumFrame)
        {
            if (hasAbsoluteState) state.Advance(increment, layer.Textures.Count);
            frames[outputFrame++] = state.Snapshot(hasAbsoluteState);
        }
        for (int frame = 0; frame < frames.Length; frame++)
        {
            frames[frame] ??= VceFrameState.Hidden;
        }
        return frames;
    }

    private sealed record LayerRuntime(
        Image Image,
        IReadOnlyList<VcePlaybackTextureViewModel> Textures,
        IReadOnlyList<VceFrameState> Frames);

    private sealed record VceFrameState(
        bool Visible,
        double PositionX,
        double PositionY,
        IReadOnlyList<double> PointX,
        IReadOnlyList<double> PointY,
        int TextureIndex,
        double Rotation,
        double Alpha)
    {
        public static VceFrameState Hidden { get; } = new(
            false,
            0,
            0,
            [0, 0, 0, 0],
            [0, 0, 0, 0],
            -1,
            0,
            0);
    }

    private sealed class MutableFrameState
    {
        public double PositionX;
        public double PositionY;
        public double[] PointX = new double[4];
        public double[] PointY = new double[4];
        public int TextureIndex = -1;
        public uint TextureMode;
        public double TextureStep;
        public double TextureAccumulator;
        public double Rotation;
        public double Red;
        public double Blue;
        public double Green;
        public double Alpha;

        public static MutableFrameState Hidden() => new();
        public static MutableFrameState Zero() => new();

        public void ApplyAbsolute(VceKeyframe keyframe)
        {
            PositionX = keyframe.PositionX;
            PositionY = keyframe.PositionY;
            PointX = keyframe.PointX.Select(static value => (double)value).ToArray();
            PointY = keyframe.PointY.Select(static value => (double)value).ToArray();
            TextureIndex = (int)Math.Round(keyframe.TextureId);
            TextureMode = keyframe.TextureMode;
            TextureStep = keyframe.TextureStep;
            TextureAccumulator = 0;
            Rotation = -keyframe.Rotation;
            Red = keyframe.Red;
            Blue = keyframe.Blue;
            Green = keyframe.Green;
            Alpha = keyframe.Alpha;
        }

        public void ApplyIncrement(VceKeyframe keyframe)
        {
            PositionX = keyframe.PositionX;
            PositionY = keyframe.PositionY;
            PointX = keyframe.PointX.Select(static value => (double)value).ToArray();
            PointY = keyframe.PointY.Select(static value => (double)value).ToArray();
            TextureIndex = (int)Math.Round(keyframe.TextureId);
            TextureMode = keyframe.TextureMode;
            TextureStep = keyframe.TextureStep;
            Rotation = keyframe.Rotation;
            Red = keyframe.Red;
            Blue = keyframe.Blue;
            Green = keyframe.Green;
            Alpha = keyframe.Alpha;
        }

        public void Advance(
            MutableFrameState increment,
            int textureCount)
        {
            PositionX += increment.PositionX;
            PositionY += increment.PositionY;
            for (int index = 0; index < 4; index++)
            {
                PointX[index] += increment.PointX[index];
                PointY[index] += increment.PointY[index];
            }
            Rotation -= increment.Rotation;
            Red = Math.Clamp(Red + increment.Red, 0, 255);
            Blue = Math.Clamp(Blue + increment.Blue, 0, 255);
            Green = Math.Clamp(Green + increment.Green, 0, 255);
            Alpha = Math.Clamp(Alpha + increment.Alpha, 0, 255);
            TextureAccumulator += TextureStep;
            if (textureCount <= 0 || TextureAccumulator <= 1) return;
            TextureAccumulator -= 1;
            if (TextureMode == 3)
            {
                TextureIndex = (TextureIndex + 1 + textureCount) % textureCount;
            }
            else if (TextureMode == 4)
            {
                TextureIndex = (TextureIndex - 1 + textureCount) % textureCount;
            }
        }

        public VceFrameState Snapshot(bool visible) => new(
            visible && Alpha > 0 && TextureIndex >= 0,
            PositionX,
            PositionY,
            PointX.ToArray(),
            PointY.ToArray(),
            TextureIndex,
            Rotation,
            Alpha);
    }
}
