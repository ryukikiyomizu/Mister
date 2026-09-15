using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mister.Core.Diagnostics;
using Mister.Core.Extraction;
using Mister.Core.Formats;

namespace Mister.Core.Preview;

/// <summary>Produces in-memory, bounded previews. It never creates output files or executes payloads.</summary>
public sealed class ArchivePreviewService
{
    public const int MaxTextBytes = 256 * 1024;
    public const int MaxImageBytes = 32 * 1024 * 1024;
    public const int MaxVceBytes = 32 * 1024 * 1024;
    private const int MaxMediaHeaderBytes = 64 * 1024;
    private const int MaxHexPreviewBytes = 4 * 1024;
    private readonly IReadOnlyDictionary<GameFamily, IPayloadDecoder> _decoders;

    public ArchivePreviewService(IReadOnlyDictionary<GameFamily, IPayloadDecoder> decoders) =>
        _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));

    public ArchivePreviewTree CreateTree(ArchiveIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var root = new MutableNode("");
        var extensions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ArchiveEntry entry in index.Entries.OrderBy(static x => x.Ordinal))
        {
            AddSaturating(ref total, entry.DecodedSize);
            string extension = Path.GetExtension(entry.DecodedPath);
            extensions.TryGetValue(extension, out long count);
            extensions[extension] = count == long.MaxValue ? long.MaxValue : count + 1;
            MutableNode current = root;
            foreach (string part in entry.DecodedPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.GetOrAdd(part);
            }
            current.Entry = entry;
        }
        return new(ToNodes(root), index.Health,
            extensions.OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static x => x.Key, static x => x.Value, StringComparer.OrdinalIgnoreCase),
            total, index.Diagnostics);
    }

    public ValueTask<EntryPreview> PreviewEntryAsync(ArchiveIndex index, ArchiveEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        return PreviewEntryCoreAsync(index.ArchivePath, index.Family, index.Health, entry, cancellationToken);
    }

    public ValueTask<EntryPreview> PreviewEntryAsync(ArchiveEntry entry, CancellationToken cancellationToken = default) =>
        PreviewEntryCoreAsync("", GameFamily.Technika3, ArchiveHealth.Valid, entry, cancellationToken);

    private async ValueTask<EntryPreview> PreviewEntryCoreAsync(string archivePath, GameFamily family, ArchiveHealth health, ArchiveEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_decoders.TryGetValue(family, out IPayloadDecoder? decoder))
        {
            return Unavailable(entry, health, new ToolDiagnostic(DiagnosticCode.UnknownPakFormat, "No preview decoder is available for this archive family.", entry.DecodedPath));
        }
        if (entry.DecodedSize < 0 || entry.StoredSize < 0)
        {
            return Unavailable(entry, health, new ToolDiagnostic(DiagnosticCode.DecodedSizeMismatch, "The entry has an invalid size.", entry.DecodedPath));
        }

        string extension = Path.GetExtension(entry.DecodedPath);
        if (IsText(extension)) return await PreviewTextAsync(decoder, archivePath, entry, health, cancellationToken).ConfigureAwait(false);
        if (IsImage(extension)) return await PreviewImageAsync(decoder, archivePath, entry, health, cancellationToken).ConfigureAwait(false);
        if (IsMedia(extension)) return await PreviewMediaAsync(decoder, archivePath, entry, health, cancellationToken).ConfigureAwait(false);
        if (IsVce(extension)) return await PreviewVceAsync(decoder, archivePath, entry, health, cancellationToken).ConfigureAwait(false);
        return await PreviewHexAsync(
            decoder,
            archivePath,
            entry,
            health,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<EntryPreview> PreviewTextAsync(IPayloadDecoder decoder, string archivePath, ArchiveEntry entry, ArchiveHealth health, CancellationToken token)
    {
        var output = new BoundedCaptureStream(MaxTextBytes);
        PayloadResult? result;
        try { result = await decoder.DecodeAsync(archivePath, entry, output, token).ConfigureAwait(false); }
        catch (PreviewLimitReachedException) when (entry.DecodedSize > output.Length && output.Length == MaxTextBytes)
        {
            return TextPreview(entry, health, output.ToArray(), truncated: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Unavailable(entry, health, DecodeError(entry, ex)); }
        token.ThrowIfCancellationRequested();
        if (!ValidResult(result, entry, output)) return Unavailable(entry, health, SizeError(entry));
        return TextPreview(entry, health, output.ToArray(), truncated: false);
    }

    private static EntryPreview TextPreview(ArchiveEntry entry, ArchiveHealth health, byte[] bytes, bool truncated)
    {
        int count = CompleteTextLength(bytes);
        if (count < 0) return Unavailable(entry, health, SignatureError(entry));
        truncated |= count != bytes.Length;
        if (count != bytes.Length) Array.Resize(ref bytes, count);
        var verification = SignatureVerifier.Verify(entry.DecodedPath, bytes);
        if (!verification.IsSuccess) return Unavailable(entry, health, verification.Error!);
        if (!SignatureVerifier.TryDecodeSaneText(bytes, out string decoded))
        {
            return Unavailable(entry, health, SignatureError(entry));
        }
        return new(PreviewKind.Text, decoded, null, Metadata(entry, health, verification.Value!.Signature), truncated, bytes.Length);
    }

    private async ValueTask<EntryPreview> PreviewImageAsync(IPayloadDecoder decoder, string archivePath, ArchiveEntry entry, ArchiveHealth health, CancellationToken token)
    {
        if (entry.DecodedSize > MaxImageBytes) return Unavailable(entry, health, new ToolDiagnostic(DiagnosticCode.DecodedSizeMismatch, "The declared image size exceeds the preview limit.", entry.DecodedPath));
        var output = new BoundedCaptureStream(MaxImageBytes);
        PayloadResult? result;
        try { result = await decoder.DecodeAsync(archivePath, entry, output, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Unavailable(entry, health, DecodeError(entry, ex)); }
        token.ThrowIfCancellationRequested();
        if (!ValidResult(result, entry, output)) return Unavailable(entry, health, SizeError(entry));
        byte[] bytes = output.ToArray();
        var verification = SignatureVerifier.Verify(entry.DecodedPath, bytes);
        if (!verification.IsSuccess || !HasImageStructure(verification.Value!.Signature, bytes)) return Unavailable(entry, health, verification.Error ?? SignatureError(entry));
        return new(PreviewKind.Image, null, bytes, Metadata(entry, health, verification.Value!.Signature), false, bytes.Length);
    }

    private async ValueTask<EntryPreview> PreviewMediaAsync(IPayloadDecoder decoder, string archivePath, ArchiveEntry entry, ArchiveHealth health, CancellationToken token)
    {
        var output = new BoundedCaptureStream(MaxMediaHeaderBytes);
        PayloadResult? result;
        try { result = await decoder.DecodeAsync(archivePath, entry, output, token).ConfigureAwait(false); }
        catch (PreviewLimitReachedException) when (entry.DecodedSize > output.Length && output.Length == MaxMediaHeaderBytes)
        {
            return MediaPreview(entry, health, output.ToArray(), truncated: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Unavailable(entry, health, DecodeError(entry, ex)); }
        token.ThrowIfCancellationRequested();
        if (!ValidResult(result, entry, output)) return Unavailable(entry, health, SizeError(entry));
        return MediaPreview(entry, health, output.ToArray(), truncated: false);
    }

    private static EntryPreview MediaPreview(ArchiveEntry entry, ArchiveHealth health, byte[] bytes, bool truncated)
    {
        var verification = SignatureVerifier.Verify(entry.DecodedPath, bytes);
        if (!verification.IsSuccess)
        {
            return Unavailable(entry, health, verification.Error!);
        }
        PayloadSignature signature = verification.Value!.Signature;
        if (signature == PayloadSignature.KnownZeroFilledOgg)
        {
            return Unavailable(
                entry,
                health,
                new ToolDiagnostic(
                    DiagnosticCode.SignatureMismatch,
                    "This OGG entry is a known empty placeholder and has no audio to play.",
                    entry.DecodedPath));
        }
        var metadata = new Dictionary<string, string>(Metadata(entry, health, signature), StringComparer.Ordinal)
        {
            ["previewState"] = truncated ? "partial-header" : "complete"
        };
        return new(PreviewKind.Media, null, null, metadata, truncated, bytes.Length);
    }

    private async ValueTask<EntryPreview> PreviewVceAsync(
        IPayloadDecoder decoder,
        string archivePath,
        ArchiveEntry entry,
        ArchiveHealth health,
        CancellationToken token)
    {
        if (entry.DecodedSize > MaxVceBytes)
        {
            return Unavailable(
                entry,
                health,
                new ToolDiagnostic(
                    DiagnosticCode.DecodedSizeMismatch,
                    "The declared VCE size exceeds the animation preview limit.",
                    entry.DecodedPath));
        }

        var output = new BoundedCaptureStream(MaxVceBytes);
        PayloadResult? result;
        try
        {
            result = await decoder
                .DecodeAsync(archivePath, entry, output, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Unavailable(entry, health, DecodeError(entry, exception));
        }
        token.ThrowIfCancellationRequested();
        if (!ValidResult(result, entry, output))
        {
            return Unavailable(entry, health, SizeError(entry));
        }

        try
        {
            VceAnimation animation = VceParser.Parse(output.ToArray());
            var metadata = new Dictionary<string, string>(
                Metadata(
                    entry,
                    health,
                    PayloadSignature.Unknown),
                StringComparer.Ordinal)
            {
                ["fps"] = animation.FramesPerSecond.ToString(),
                ["frames"] = animation.MaximumFrame.ToString(),
                ["duration"] = $"{animation.DurationSeconds:0.###} s",
                ["layers"] = animation.Layers.Count.ToString(),
                ["masked"] = animation.WasMasked.ToString()
            };
            return new EntryPreview(
                PreviewKind.Animation,
                null,
                null,
                metadata,
                false,
                checked((int)output.Length))
            {
                Animation = animation
            };
        }
        catch (InvalidDataException exception)
        {
            return Unavailable(
                entry,
                health,
                new ToolDiagnostic(
                    DiagnosticCode.SignatureMismatch,
                    $"The VCE animation could not be parsed: {exception.Message}",
                    entry.DecodedPath));
        }
    }

    private async ValueTask<EntryPreview> PreviewHexAsync(
        IPayloadDecoder decoder,
        string archivePath,
        ArchiveEntry entry,
        ArchiveHealth health,
        CancellationToken token)
    {
        var output = new BoundedCaptureStream(MaxHexPreviewBytes);
        try
        {
            PayloadResult result = await decoder
                .DecodeAsync(archivePath, entry, output, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!ValidResult(result, entry, output))
            {
                return Unavailable(entry, health, SizeError(entry));
            }
            return HexPreview(entry, health, output.ToArray(), truncated: false);
        }
        catch (PreviewLimitReachedException)
            when (entry.DecodedSize > output.Length
                  && output.Length == MaxHexPreviewBytes)
        {
            return HexPreview(entry, health, output.ToArray(), truncated: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Unavailable(entry, health, DecodeError(entry, exception));
        }
    }

    private static EntryPreview HexPreview(
        ArchiveEntry entry,
        ArchiveHealth health,
        byte[] bytes,
        bool truncated)
    {
        var text = new StringBuilder(bytes.Length * 4);
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            int count = Math.Min(16, bytes.Length - offset);
            text.Append(offset.ToString("X8"));
            text.Append("  ");
            for (int index = 0; index < 16; index++)
            {
                if (index < count)
                {
                    text.Append(bytes[offset + index].ToString("X2"));
                }
                else
                {
                    text.Append("  ");
                }
                text.Append(index == 7 ? "  " : " ");
            }
            text.Append(" |");
            for (int index = 0; index < count; index++)
            {
                byte value = bytes[offset + index];
                text.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
            }
            text.AppendLine("|");
        }

        return new EntryPreview(
            PreviewKind.Hex,
            text.ToString(),
            null,
            Metadata(entry, health, PayloadSignature.Unknown),
            truncated,
            bytes.Length);
    }

    private static bool ValidResult(PayloadResult? result, ArchiveEntry entry, BoundedCaptureStream output) =>
        result is not null && result.DecodedBytes >= 0 &&
        result.DecodedBytes == entry.DecodedSize &&
        result.DecodedBytes == output.Length &&
        (!string.IsNullOrWhiteSpace(result.Sha256)) &&
        string.Equals(result.Sha256, Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

    private static int CompleteTextLength(byte[] bytes)
    {
        for (int count = bytes.Length; count >= Math.Max(0, bytes.Length - 4); count--)
        {
            if (SignatureVerifier.TryDecodeSaneText(
                    bytes.AsSpan(0, count),
                    out _))
            {
                return count;
            }
        }
        return -1;
    }

    private static bool HasImageStructure(PayloadSignature signature, ReadOnlySpan<byte> bytes) => signature switch
    {
        PayloadSignature.Png => HasPngStructure(bytes),
        PayloadSignature.Jpeg => HasJpegStructure(bytes),
        PayloadSignature.Bmp => HasBmpStructure(bytes),
        _ => false
    };

    private static bool HasPngStructure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 + 12 + 13 + 12) return false;
        int offset = 8; bool ihdr = false;
        while (offset <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            long end = (long)offset + 12 + length;
            if (end > bytes.Length) return false;
            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            if (!ihdr)
            {
                if (length != 13 || !type.SequenceEqual("IHDR"u8)) return false;
                uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4));
                uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 12, 4));
                if (width == 0 || height == 0) return false;
                ihdr = true;
            }
            if (type.SequenceEqual("IEND"u8)) return ihdr && length == 0 && end == bytes.Length;
            offset = checked((int)end);
        }
        return false;
    }

    private static bool HasJpegStructure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        int offset = 2; bool sof = false;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 0xff) return false;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return false;
            byte marker = bytes[offset++];
            if (marker == 0xd9) return sof && offset == bytes.Length;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset > bytes.Length - 2) return false;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || length > bytes.Length - offset) return false;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) return false;
                ushort height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                ushort width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                if (height == 0 || width == 0 || bytes[offset + 7] == 0) return false;
                sof = true;
            }
            if (marker == 0xda) return sof && bytes.Length >= 2 && bytes[^2] == 0xff && bytes[^1] == 0xd9;
            offset += length;
        }
        return false;
    }

    private static bool HasBmpStructure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 54) return false;
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(2, 4));
        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(10, 4));
        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(14, 4));
        if (fileSize != bytes.Length || dibSize < 40 || dibSize > bytes.Length - 14 || dataOffset < 14 + dibSize || dataOffset >= bytes.Length) return false;
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(18, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(22, 4));
        return width != 0 && height != 0 && height != int.MinValue;
    }

    private static IReadOnlyDictionary<string, string> Metadata(ArchiveEntry e, ArchiveHealth health, PayloadSignature signature) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["path"] = e.DecodedPath,
        ["ordinal"] = e.Ordinal.ToString(),
        ["recordOffset"] = e.RecordOffset.ToString(),
        ["payloadOffset"] = e.PayloadOffset.ToString(),
        ["storedSize"] = e.StoredSize.ToString(),
        ["decodedSize"] = e.DecodedSize.ToString(),
        ["fileKey"] = e.FileKey.ToString("X8"),
        ["keyIndex"] = e.KeyIndex.ToString(),
        ["signature"] = signature.ToString(),
        ["archiveHealth"] = health.ToString()
    };
    private static EntryPreview Unavailable(ArchiveEntry e, ArchiveHealth h, ToolDiagnostic error) => new(PreviewKind.Unavailable, null, null, Metadata(e, h, PayloadSignature.Unknown), false, 0) { Diagnostic = error };
    private static ToolDiagnostic DecodeError(ArchiveEntry e, Exception ex) => new(DiagnosticCode.DecodedSizeMismatch, $"The payload could not be previewed: {ex.Message}", e.DecodedPath);
    private static ToolDiagnostic SizeError(ArchiveEntry e) => new(DiagnosticCode.DecodedSizeMismatch, "The decoder result does not match the bounded preview output.", e.DecodedPath);
    private static ToolDiagnostic SignatureError(ArchiveEntry e) => new(DiagnosticCode.SignatureMismatch, "The text preview is not valid UTF-8.", e.DecodedPath);
    private static bool IsText(string e) => e.Equals(".txt", StringComparison.OrdinalIgnoreCase) || e.Equals(".lua", StringComparison.OrdinalIgnoreCase) || e.Equals(".xml", StringComparison.OrdinalIgnoreCase) || e.Equals(".json", StringComparison.OrdinalIgnoreCase) || e.Equals(".ini", StringComparison.OrdinalIgnoreCase) || e.Equals(".cfg", StringComparison.OrdinalIgnoreCase) || e.Equals(".csv", StringComparison.OrdinalIgnoreCase) || e.Equals(".tsv", StringComparison.OrdinalIgnoreCase) || e.Equals(".log", StringComparison.OrdinalIgnoreCase) || e.Equals(".md", StringComparison.OrdinalIgnoreCase) || e.Equals(".yml", StringComparison.OrdinalIgnoreCase) || e.Equals(".yaml", StringComparison.OrdinalIgnoreCase);
    private static bool IsImage(string e) => e.Equals(".png", StringComparison.OrdinalIgnoreCase) || e.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || e.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    private static bool IsMedia(string e) =>
        e.Equals(".bik", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".wav", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".avi", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
    private static bool IsVce(string e) =>
        e.Equals(".vce", StringComparison.OrdinalIgnoreCase)
        || e.Equals(".vci", StringComparison.OrdinalIgnoreCase);
    private static void AddSaturating(ref long total, long value) { if (value <= 0) return; total = total > long.MaxValue - value ? long.MaxValue : total + value; }

    private static IReadOnlyList<ArchivePreviewNode> ToNodes(MutableNode node) => node.Children.Values.OrderBy(static n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static n => n.Name, StringComparer.Ordinal).Select(n => new ArchivePreviewNode(n.Name, ToNodes(n), n.Entry, n.Entry is not null && n.Children.Count > 0)).ToArray();
    private sealed class MutableNode(string name)
    {
        public string Name { get; } = name; public ArchiveEntry? Entry { get; set; }
        public Dictionary<string, MutableNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MutableNode GetOrAdd(string name) { if (!Children.TryGetValue(name, out var node)) Children.Add(name, node = new(name)); return node; }
    }
    private sealed class PreviewLimitReachedException : IOException;

    private sealed class BoundedCaptureStream(int capacity) : Stream
    {
        private readonly MemoryStream _buffer = new(capacity);
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => _buffer.Length; public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken c) => Task.CompletedTask; public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin w) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => Write(b.AsSpan(o, c)); public override void Write(ReadOnlySpan<byte> source) { int available = capacity - checked((int)_buffer.Length); if (source.Length <= available) { _buffer.Write(source); return; } if (available > 0) _buffer.Write(source[..available]); throw new PreviewLimitReachedException(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token = default) { token.ThrowIfCancellationRequested(); Write(source.Span); return ValueTask.CompletedTask; }
        public byte[] ToArray() => _buffer.ToArray(); protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
    }
}
