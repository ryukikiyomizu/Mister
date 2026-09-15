using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Formats;

namespace Mister.Core.T2;

public sealed class T2ArchiveReader : IArchiveReader
{
    private static readonly HashSet<string> ReservedWindowsDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "CONIN$",
            "CONOUT$"
        };

    public T2ArchiveReader(ClientEvidence clientEvidence)
    {
        ArgumentNullException.ThrowIfNull(clientEvidence);
        if (clientEvidence.Family != GameFamily.Technika2)
        {
            throw new ArgumentException(
                "T2 archive indexing requires Technika 2 client evidence.",
                nameof(clientEvidence));
        }

        if (clientEvidence.T2SubtractTable is null
            || clientEvidence.T2SubtractTable.Length
                != T2Constants.SubtractTableSize)
        {
            throw new ArgumentException(
                $"The T2 subtraction table must contain exactly {T2Constants.SubtractTableSize} dwords.",
                nameof(clientEvidence));
        }

        if (clientEvidence.RecordXorTable.Length
            != T2Constants.RecordXorTableSize)
        {
            throw new ArgumentException(
                $"The T2 client record table must be exactly {T2Constants.RecordXorTableSize} bytes.",
                nameof(clientEvidence));
        }

        ClientEvidence = clientEvidence with
        {
            T2SubtractTable = clientEvidence.T2SubtractTable.ToArray(),
            RecordXorTable = clientEvidence.RecordXorTable.ToArray()
        };
    }

    public ClientEvidence ClientEvidence { get; private set; }

    public GameFamily Family => GameFamily.Technika2;

    public async ValueTask<ToolResult<ArchiveIndex>> IndexAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        ToolResult<ArchiveIndex> result = await IndexCoreAsync(
            archivePath,
            DetectionDepth.Full,
            cancellationToken);
        if (result.IsSuccess)
        {
            CommitStructuralValidation();
        }

        return result;
    }

    public ValueTask<ToolResult<ArchiveIndex>> IndexAsync(
        string archivePath,
        DetectionDepth depth,
        CancellationToken cancellationToken) =>
        IndexCoreAsync(archivePath, depth, cancellationToken);

    public async ValueTask<ToolResult<ArchiveIndex>> IndexAsync(
        FileStream archiveStream,
        string archivePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ToolResult<ArchiveIndex> result = await IndexCoreAsync(
            archivePath, DetectionDepth.Full, cancellationToken, archiveStream);
        if (result.IsSuccess) CommitStructuralValidation();
        return result;
    }

    private async ValueTask<ToolResult<ArchiveIndex>> IndexCoreAsync(
        string archivePath,
        DetectionDepth depth,
        CancellationToken cancellationToken,
        FileStream? retainedStream = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!Enum.IsDefined(depth))
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        string fullPath = Path.GetFullPath(archivePath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream = retainedStream ?? new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            await using FileStream? ownedStream = retainedStream is null ? stream : null;
            long archiveSize = stream.Length;
            if (archiveSize < T2Constants.HeaderOffset + T2Constants.HeaderSize)
            {
                return Failure(
                    DiagnosticCode.InvalidHeader,
                    "The selected file is too small to contain a T2 archive header.",
                    fullPath);
            }

            byte[] rawHeader = new byte[T2Constants.HeaderSize];
            stream.Position = T2Constants.HeaderOffset;
            await stream.ReadExactlyAsync(rawHeader, cancellationToken);
            ToolResult<T2ArchiveHeader> headerResult = DecodeHeader(
                rawHeader,
                ClientEvidence.T2SubtractTable!);
            if (!headerResult.IsSuccess)
            {
                return ToolResult<ArchiveIndex>.Failure(
                    headerResult.Error! with { Subject = fullPath });
            }

            T2ArchiveHeader header = headerResult.Value!;
            if (header.FirstRecordOffset
                    < T2Constants.HeaderOffset + T2Constants.HeaderSize
                || header.FirstRecordOffset > archiveSize
                || header.EntryCount > archiveSize / T2Constants.RecordSize)
            {
                return Failure(
                    DiagnosticCode.InvalidHeader,
                    "The T2 archive header points outside the selected file.",
                    fullPath);
            }

            if (depth == DetectionDepth.HeaderAndFirstRecord
                && header.EntryCount == 0)
            {
                return Failure(
                    DiagnosticCode.InvalidHeader,
                    "T2 quick detection requires a first archive record to validate.",
                    fullPath);
            }

            int entryLimit = depth == DetectionDepth.Full
                ? header.EntryCount
                : Math.Min(header.EntryCount, 1);
            var entries = new List<ArchiveEntry>(entryLimit);
            var diagnostics = new List<ToolDiagnostic>();
            long recordOffset = header.FirstRecordOffset;
            byte recordSeed = header.InitialSeed;
            byte[] rawRecord = new byte[T2Constants.RecordSize];
            for (int ordinal = 0; ordinal < entryLimit; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (recordOffset < 0
                    || recordOffset > archiveSize - T2Constants.RecordSize)
                {
                    return Failure(
                        DiagnosticCode.RecordChainOverflow,
                        $"T2 record {ordinal} extends beyond the selected archive.",
                        fullPath);
                }

                stream.Position = recordOffset;
                await stream.ReadExactlyAsync(rawRecord, cancellationToken);
                ToolResult<ArchiveEntry> entryResult = DecodeEntry(
                    rawRecord,
                    recordSeed,
                    ClientEvidence.T2SubtractTable!,
                    ClientEvidence.RecordXorTable,
                    ordinal,
                    recordOffset);
                if (!entryResult.IsSuccess)
                {
                    return ToolResult<ArchiveIndex>.Failure(
                        entryResult.Error! with { Subject = fullPath });
                }

                ArchiveEntry entry = entryResult.Value!;
                long nextRecordOffset;
                try
                {
                    nextRecordOffset = checked(
                        entry.PayloadOffset + entry.StoredSize);
                }
                catch (OverflowException)
                {
                    return Failure(
                        DiagnosticCode.RecordChainOverflow,
                        $"T2 record {ordinal} has an overflowing payload size.",
                        fullPath);
                }

                if (nextRecordOffset < entry.PayloadOffset
                    || nextRecordOffset > archiveSize)
                {
                    return Failure(
                        DiagnosticCode.RecordChainOverflow,
                        $"T2 record {ordinal} payload extends beyond the selected archive.",
                        fullPath);
                }

                if (entry.StoredSize != entry.DecodedSize)
                {
                    diagnostics.Add(new ToolDiagnostic(
                        DiagnosticCode.UnsupportedT2Compression,
                        $"T2 entry {ordinal} ({entry.DecodedPath}) declares {entry.StoredSize} stored bytes and {entry.DecodedSize} decoded bytes; indexing can continue but payload decoding is unsupported.",
                        entry.DecodedPath));
                }

                entries.Add(entry);
                recordOffset = nextRecordOffset;
                recordSeed = unchecked((byte)(recordSeed - 1));
            }

            if (depth == DetectionDepth.Full && recordOffset != archiveSize)
            {
                return Failure(
                    DiagnosticCode.TrailingData,
                    $"The decoded T2 record chain ends at {recordOffset}, not EOF {archiveSize}.",
                    fullPath);
            }

            return ToolResult<ArchiveIndex>.Success(
                new ArchiveIndex(
                    fullPath,
                    GameFamily.Technika2,
                    archiveSize,
                    entries,
                    recordOffset,
                    diagnostics.Count == 0
                        ? ArchiveHealth.Valid
                        : ArchiveHealth.Warning,
                    diagnostics));
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "T2 archive indexing was cancelled.",
                fullPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                DiagnosticCode.UnknownPakFormat,
                $"The selected T2 archive could not be read: {exception.Message}",
                fullPath);
        }
    }

    internal void CommitStructuralValidation()
    {
        ClientEvidence = ClientEvidence with
        {
            ValidationStatus =
                ClientEvidenceValidationStatus.StructurallyValidated
        };
    }

    private static ToolResult<T2ArchiveHeader> DecodeHeader(
        ReadOnlySpan<byte> rawHeader,
        ReadOnlySpan<uint> subtractTable)
    {
        if (rawHeader.Length != T2Constants.HeaderSize)
        {
            return HeaderFailure(
                DiagnosticCode.InvalidHeader,
                $"The T2 archive header must be exactly {T2Constants.HeaderSize} bytes.");
        }

        Span<byte> decoded = stackalloc byte[T2Constants.HeaderSize];
        rawHeader.CopyTo(decoded);
        for (int index = 0; index < 3; index++)
        {
            int offset = index * sizeof(uint);
            uint word = BinaryPrimitivesEx.ReadUInt32(decoded, offset);
            BinaryPrimitivesEx.WriteUInt32(
                decoded,
                offset,
                unchecked(word - subtractTable[T2Constants.HeaderSize + index]));
        }

        uint firstRecordOffset =
            BinaryPrimitivesEx.ReadUInt32(decoded, 0)
            ^ T2Constants.FirstRecordMask;
        BinaryPrimitivesEx.WriteUInt32(decoded, 0, firstRecordOffset);
        uint second =
            BinaryPrimitivesEx.ReadUInt32(decoded, sizeof(uint))
            ^ firstRecordOffset;
        BinaryPrimitivesEx.WriteUInt32(decoded, sizeof(uint), second);

        if (decoded[4] != 1)
        {
            return HeaderFailure(
                DiagnosticCode.InvalidHeader,
                $"Unsupported T2 archive version {decoded[4]}.");
        }

        uint entryCount = BinaryPrimitivesEx.ReadUInt32(decoded, 6);
        if (entryCount > int.MaxValue)
        {
            return HeaderFailure(
                DiagnosticCode.InvalidHeader,
                "The T2 archive header contains too many entries.");
        }

        byte flag = decoded[11];
        byte expectedChecksum = unchecked((byte)~(
            (decoded[6] ^ (flag == 0 ? 0 : 1))
            * decoded[0]
            + decoded[10]));
        if (decoded[12] != expectedChecksum)
        {
            return HeaderFailure(
                DiagnosticCode.InvalidHeaderChecksum,
                "The T2 archive header checksum is invalid.");
        }

        return ToolResult<T2ArchiveHeader>.Success(
            new T2ArchiveHeader(
                firstRecordOffset,
                checked((int)entryCount),
                decoded[10]));
    }

    private static ToolResult<ArchiveEntry> DecodeEntry(
        ReadOnlySpan<byte> rawRecord,
        byte recordSeed,
        ReadOnlySpan<uint> subtractTable,
        ReadOnlySpan<byte> recordXorTable,
        int ordinal,
        long recordOffset)
    {
        byte[] candidate = new byte[T2Constants.RecordSize];
        byte[]? decodedRecord = null;
        string? decodedPath = null;
        int matchingKey = -1;
        ToolDiagnostic? pathDiagnostic = null;
        string? unsafePath = null;
        for (int key = byte.MinValue; key <= byte.MaxValue; key++)
        {
            rawRecord.CopyTo(candidate);
            DecodeRecord(
                candidate,
                checked((byte)key),
                recordSeed,
                subtractTable,
                recordXorTable);
            if (!HasSupportedArchivePrefix(candidate))
            {
                continue;
            }

            ToolResult<string> pathResult = Cp949PathDecoder.Decode(
                candidate.AsSpan(
                    T2Constants.PathOffset,
                    T2Constants.PathSize),
                requireCcFiller: false,
                allowPatternRoot: true);
            if (!pathResult.IsSuccess)
            {
                pathDiagnostic ??= pathResult.Error;
                continue;
            }

            if (!IsWindowsSafeArchivePath(pathResult.Value!))
            {
                unsafePath ??= pathResult.Value;
                continue;
            }

            if (decodedRecord is not null)
            {
                return EntryFailure(
                    DiagnosticCode.MissingClientTable,
                    $"T2 record {ordinal} did not produce a unique record key.");
            }

            matchingKey = key;
            decodedRecord = candidate.ToArray();
            decodedPath = pathResult.Value;
        }

        if (decodedRecord is null)
        {
            if (unsafePath is not null)
            {
                return EntryFailure(
                    DiagnosticCode.UnsafeOutputPath,
                    $"T2 record {ordinal} contains an unsafe resource path.");
            }

            if (pathDiagnostic is not null)
            {
                return ToolResult<ArchiveEntry>.Failure(pathDiagnostic);
            }

            return EntryFailure(
                DiagnosticCode.MissingClientTable,
                $"T2 record {ordinal} did not produce a unique record key.");
        }

        uint decodedSize = ReadPermutedUInt32(
            decodedRecord,
            138,
            140,
            136,
            142);
        uint storedSize = ReadPermutedUInt32(
            decodedRecord,
            143,
            137,
            141,
            139);
        long payloadOffset = checked(recordOffset + T2Constants.RecordSize);
        return ToolResult<ArchiveEntry>.Success(
            new ArchiveEntry(
                ordinal,
                decodedPath!,
                recordOffset,
                payloadOffset,
                storedSize,
                decodedSize,
                checked((uint)matchingKey),
                recordSeed));
    }

    private static void DecodeRecord(
        Span<byte> record,
        byte key,
        byte recordSeed,
        ReadOnlySpan<uint> subtractTable,
        ReadOnlySpan<byte> recordXorTable)
    {
        for (int offset = 8;
            offset < T2Constants.RecordSize;
            offset += sizeof(uint))
        {
            record[offset] ^= key;
        }

        for (int offset = 0;
            offset < T2Constants.RecordSize;
            offset += sizeof(uint))
        {
            record[offset] ^=
                recordXorTable[(recordSeed + offset) & byte.MaxValue];
        }

        for (int index = 0; index < 32; index++)
        {
            int offset = T2Constants.PathOffset + index * sizeof(uint);
            uint word = BinaryPrimitivesEx.ReadUInt32(record, offset);
            BinaryPrimitivesEx.WriteUInt32(
                record,
                offset,
                unchecked(word - subtractTable[0x80 + index]));
        }
    }

    private static bool HasSupportedArchivePrefix(ReadOnlySpan<byte> record)
    {
        ReadOnlySpan<byte> path = record[T2Constants.PathOffset..];
        return HasAsciiPrefix(path, "resource\\"u8)
            || HasAsciiPrefix(path, "pattern\\"u8);
    }

    private static bool HasAsciiPrefix(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length)
        {
            return false;
        }

        for (int index = 0; index < prefix.Length; index++)
        {
            byte character = value[index];
            if (character is >= (byte)'A' and <= (byte)'Z')
            {
                character = (byte)(character + ('a' - 'A'));
            }

            if (character != prefix[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWindowsSafeArchivePath(string path)
    {
        if (path.StartsWith('\\')
            || path.StartsWith('/')
            || Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string[] components =
            path.Split(['\\', '/'], StringSplitOptions.None);
        foreach (string component in components)
        {
            if (component.Length == 0
                || component is "." or ".."
                || component.EndsWith(' ')
                || component.EndsWith('.')
                || component.Any(static character =>
                    char.IsControl(character)
                    || character is '<' or '>' or ':' or '"'
                        or '|' or '?' or '*'))
            {
                return false;
            }

            int extensionSeparator = component.IndexOf('.');
            string deviceName = extensionSeparator < 0
                ? component
                : component[..extensionSeparator];
            deviceName = deviceName.TrimEnd(' ', '.');
            if (ReservedWindowsDeviceNames.Contains(deviceName)
                || IsSuperscriptWindowsDeviceName(deviceName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSuperscriptWindowsDeviceName(string component)
    {
        if (component.Length != 4
            || component[3] is not ('¹' or '²' or '³'))
        {
            return false;
        }

        return component.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || component.StartsWith("LPT", StringComparison.OrdinalIgnoreCase);
    }

    private static uint ReadPermutedUInt32(
        ReadOnlySpan<byte> record,
        int first,
        int second,
        int third,
        int fourth) =>
        ((uint)record[first] << 24)
        | ((uint)record[second] << 16)
        | ((uint)record[third] << 8)
        | record[fourth];

    private static ToolResult<T2ArchiveHeader> HeaderFailure(
        DiagnosticCode code,
        string message) =>
        ToolResult<T2ArchiveHeader>.Failure(
            new ToolDiagnostic(code, message));

    private static ToolResult<ArchiveEntry> EntryFailure(
        DiagnosticCode code,
        string message) =>
        ToolResult<ArchiveEntry>.Failure(
            new ToolDiagnostic(code, message));

    private static ToolResult<ArchiveIndex> Failure(
        DiagnosticCode code,
        string message,
        string subject) =>
        ToolResult<ArchiveIndex>.Failure(
            new ToolDiagnostic(code, message, subject));

    private sealed record T2ArchiveHeader(
        long FirstRecordOffset,
        int EntryCount,
        byte InitialSeed);
}
