using System.Windows.Media.Imaging;
using Mister.Core.Preview;

namespace Mister.App.ViewModels;

public sealed record VcePlaybackViewModel(
    VceAnimation Animation,
    IReadOnlyList<VcePlaybackLayerViewModel> Layers,
    int MissingTextureCount)
{
    public string Summary =>
        $"{Animation.Layers.Count} layers · "
        + $"{Animation.MaximumFrame + 1:N0} frames · "
        + $"{Animation.FramesPerSecond} FPS"
        + (MissingTextureCount == 0
            ? string.Empty
            : $" · {MissingTextureCount} texture"
              + (MissingTextureCount == 1 ? string.Empty : "s")
              + " unavailable");
}

public sealed record VcePlaybackLayerViewModel(
    VceLayer Layer,
    IReadOnlyList<VcePlaybackTextureViewModel> Textures);

public sealed record VcePlaybackTextureViewModel(
    VceTexture Definition,
    BitmapSource? Image);
