using System.Text;

namespace Mister.Core.Preview;

public static class VceParser
{
    private const int HeaderSize = 36;
    private const int TextureNameLength = 0x70;
    private const int TextureRecordLength = 0x8c;
    private const int KeyframeRecordLength = 0x7c;
    private const int MaximumLayers = 4096;
    private const int MaximumTexturesPerLayer = 4096;
    private const int MaximumKeyframesPerLayer = 1_000_000;
    private const long MaximumPlaybackFrameStates = 5_000_000;

    private static readonly byte[] Mask = Convert.FromBase64String(
        "903bStxm8FPFf+kcijCmBZMpvy64ApQ3oRuNAJYsuhmPNaMypB6IK70HkWTySN5961HHVsB67E/ZY/XIXuRy0Uf9a/ps1kDjdc9ZrDqAFrUjmQ+eCLIkhxGrPZAGvCqJH6UzojSOGLstlwH0YthO7XvBV8ZQ6nzfSfNlWM504kHXbftq/EbQc+VfyTyqEIYlswmfDpgitBeBO60gtgyaOa8VgxKEPqgLnSexRNJo/l3Lced24FrMb/lD1eh+xFLxZ91L2kz2YMNV73mMGqA2lQO5L74okgSnMYsdsCacCqk/hROCFK44mw23IdRC+G7NW+F35nDKXP9p00V47lTCYQ==");

    public static VceAnimation Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize)
        {
            throw new InvalidDataException("The VCE header is incomplete.");
        }
        if (!payload[..3].SequenceEqual("VCM"u8))
        {
            throw new InvalidDataException("The payload does not have a VCM header.");
        }

        bool masked = payload[13] != 0;
        byte[] decoded = payload.ToArray();
        if (masked)
        {
            for (int offset = 8; offset < decoded.Length; offset++)
            {
                decoded[offset] ^= Mask[offset & 0xff];
            }
        }

        using var stream = new MemoryStream(decoded, writable: false);
        using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: false);
        byte[] magic = ReadExact(reader, 4);
        _ = ReadExact(reader, 4);
        if (!magic.AsSpan(0, 3).SequenceEqual("VCM"u8))
        {
            throw new InvalidDataException("The decoded payload does not have a VCM header.");
        }

        int documentType = ReadNonNegativeInt32(reader, "document type");
        int framesPerSecond = ReadNonNegativeInt32(reader, "frame rate");
        int maximumFrame = ReadNonNegativeInt32(reader, "maximum frame");
        int layerCount = ReadBoundedCount(reader, MaximumLayers, "layer");
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        if (framesPerSecond is <= 0 or > 1000)
        {
            throw new InvalidDataException($"The VCE frame rate {framesPerSecond} is outside the supported range.");
        }
        if (maximumFrame > 10_000_000)
        {
            throw new InvalidDataException("The VCE duration is outside the supported range.");
        }

        var layers = new VceLayer[layerCount];
        for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
        {
            int textureCount = ReadBoundedCount(
                reader,
                MaximumTexturesPerLayer,
                "texture");
            var textures = new VceTexture[textureCount];
            for (int textureIndex = 0; textureIndex < textureCount; textureIndex++)
            {
                EnsureRemaining(reader, TextureRecordLength);
                byte[] nameBytes = ReadExact(reader, TextureNameLength);
                int terminator = Array.IndexOf(nameBytes, (byte)0);
                string name = Encoding.Latin1
                    .GetString(
                        nameBytes,
                        0,
                        terminator < 0 ? nameBytes.Length : terminator)
                    .Trim();
                _ = ReadExact(reader, 16);
                short x1 = reader.ReadInt16();
                short y1 = reader.ReadInt16();
                short x2 = reader.ReadInt16();
                short y2 = reader.ReadInt16();
                _ = reader.ReadUInt32();
                textures[textureIndex] = new VceTexture(
                    name,
                    x1,
                    y1,
                    Math.Max(0, x2 - x1),
                    Math.Max(0, y2 - y1));
            }

            int keyframeCount = ReadBoundedCount(
                reader,
                MaximumKeyframesPerLayer,
                "keyframe");
            var keyframes = new VceKeyframe[keyframeCount];
            for (int keyframeIndex = 0; keyframeIndex < keyframeCount; keyframeIndex++)
            {
                EnsureRemaining(reader, KeyframeRecordLength);
                int frame = ReadNonNegativeInt32(reader, "keyframe time");
                ushort kind = reader.ReadUInt16();
                ushort flags = reader.ReadUInt16();
                float positionX = ReadFinite(reader);
                float positionY = ReadFinite(reader);
                float[] textureU = ReadFiniteArray(reader, 4);
                float[] textureV = ReadFiniteArray(reader, 4);
                float[] pointX = ReadFiniteArray(reader, 4);
                float[] pointY = ReadFiniteArray(reader, 4);
                float textureId = ReadFinite(reader);
                uint textureMode = reader.ReadUInt32();
                float textureStep = ReadFinite(reader);
                float rotation = ReadFinite(reader);
                float red = ReadFinite(reader);
                float blue = ReadFinite(reader);
                float green = ReadFinite(reader);
                float alpha = ReadFinite(reader);
                uint sourceBlend = reader.ReadUInt32();
                uint destinationBlend = reader.ReadUInt32();
                uint multiTextureMode = reader.ReadUInt32();
                if (kind > 1)
                {
                    throw new InvalidDataException(
                        $"VCE layer {layerIndex} contains unsupported keyframe kind {kind}.");
                }

                keyframes[keyframeIndex] = new VceKeyframe(
                    frame,
                    kind,
                    flags,
                    positionX,
                    positionY,
                    textureU,
                    textureV,
                    pointX,
                    pointY,
                    textureId,
                    textureMode,
                    textureStep,
                    rotation,
                    red,
                    blue,
                    green,
                    alpha,
                    sourceBlend,
                    destinationBlend,
                    multiTextureMode);
            }

            layers[layerIndex] = new VceLayer(textures, keyframes);
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                $"The VCE parser consumed {stream.Position} of {stream.Length} bytes.");
        }
        long activeLayerCount = layers.LongCount(
            static layer => layer.Keyframes.Count > 0);
        if (activeLayerCount * ((long)maximumFrame + 1)
            > MaximumPlaybackFrameStates)
        {
            throw new InvalidDataException(
                "The VCE animation is too large for safe interactive playback.");
        }

        return new VceAnimation(
            framesPerSecond,
            maximumFrame,
            documentType,
            masked,
            layers);
    }

    private static int ReadBoundedCount(
        BinaryReader reader,
        int maximum,
        string label)
    {
        int value = ReadNonNegativeInt32(reader, $"{label} count");
        if (value > maximum)
        {
            throw new InvalidDataException(
                $"The VCE {label} count {value} exceeds the supported limit.");
        }
        return value;
    }

    private static int ReadNonNegativeInt32(
        BinaryReader reader,
        string label)
    {
        EnsureRemaining(reader, sizeof(uint));
        uint value = reader.ReadUInt32();
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"The VCE {label} is invalid.");
        }
        return (int)value;
    }

    private static float ReadFinite(BinaryReader reader)
    {
        float value = reader.ReadSingle();
        if (!float.IsFinite(value))
        {
            throw new InvalidDataException("The VCE contains a non-finite numeric value.");
        }
        return value;
    }

    private static float[] ReadFiniteArray(
        BinaryReader reader,
        int count)
    {
        var values = new float[count];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = ReadFinite(reader);
        }
        return values;
    }

    private static byte[] ReadExact(
        BinaryReader reader,
        int count)
    {
        EnsureRemaining(reader, count);
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException();
        }
        return bytes;
    }

    private static void EnsureRemaining(
        BinaryReader reader,
        int count)
    {
        if (count < 0 || reader.BaseStream.Length - reader.BaseStream.Position < count)
        {
            throw new InvalidDataException("The VCE payload ends inside a record.");
        }
    }
}
