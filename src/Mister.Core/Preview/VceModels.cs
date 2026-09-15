namespace Mister.Core.Preview;

public sealed record VceAnimation(
    int FramesPerSecond,
    int MaximumFrame,
    int DocumentType,
    bool WasMasked,
    IReadOnlyList<VceLayer> Layers)
{
    public double DurationSeconds =>
        FramesPerSecond <= 0
            ? 0
            : MaximumFrame / (double)FramesPerSecond;
}

public sealed record VceLayer(
    IReadOnlyList<VceTexture> Textures,
    IReadOnlyList<VceKeyframe> Keyframes);

public sealed record VceTexture(
    string Name,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight);

public sealed record VceKeyframe(
    int Frame,
    ushort Kind,
    ushort Flags,
    float PositionX,
    float PositionY,
    IReadOnlyList<float> TextureU,
    IReadOnlyList<float> TextureV,
    IReadOnlyList<float> PointX,
    IReadOnlyList<float> PointY,
    float TextureId,
    uint TextureMode,
    float TextureStep,
    float Rotation,
    float Red,
    float Blue,
    float Green,
    float Alpha,
    uint SourceBlend,
    uint DestinationBlend,
    uint MultiTextureMode);
