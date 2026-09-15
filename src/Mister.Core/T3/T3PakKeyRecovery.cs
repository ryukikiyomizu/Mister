using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Formats;

namespace Mister.Core.T3;

public sealed class T3PakKeyRecovery
{
    // A 4096:1 ceiling is deliberately generous for LZO while rejecting
    // candidate metadata that would imply an unbounded decompression result.
    private const int MaximumDecodedExpansionRatio = 4096;

    public async ValueTask<ToolResult<PakKeyCoverage>> RecoverFamilyAsync(
        IReadOnlyList<string> archivePaths,
        ClientEvidence client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archivePaths);
        ArgumentNullException.ThrowIfNull(client);

        if (client.Family != GameFamily.Technika3)
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                "T3 pakkey recovery requires Technika 3 client evidence.");
        }

        if (client.RecordXorTable.Length != T3Constants.RecordXorTableSize)
        {
            return Failure(
                DiagnosticCode.MissingClientTable,
                $"The T3 client record table must be exactly {T3Constants.RecordXorTableSize} bytes.");
        }

        T3PackFamily family;
        try
        {
            family = T3PackFamily.Create(archivePaths);
        }
        catch (ArgumentException exception)
        {
            return Failure(
                DiagnosticCode.ConflictingPakKey,
                exception.Message);
        }

        var consulted = new HashSet<int>();
        var candidates = new Dictionary<int, HashSet<byte>>();
        var evidence = new List<PakKeyEvidence>();

        try
        {
            foreach (string archivePath in family.ArchivePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string archiveHash = await FileHasher.Sha256Async(
                    archivePath,
                    cancellationToken);
                ToolResult<bool> archiveResult = await RecoverArchiveAsync(
                    archivePath,
                    archiveHash,
                    client.RecordXorTable,
                    consulted,
                    candidates,
                    evidence,
                    cancellationToken);
                if (!archiveResult.IsSuccess)
                {
                    return ToolResult<PakKeyCoverage>.Failure(
                        archiveResult.Error!);
                }
            }

            IReadOnlyDictionary<int, IReadOnlySet<byte>> candidateView =
                candidates.ToDictionary(
                    item => item.Key,
                    item => (IReadOnlySet<byte>)item.Value);
            return ToolResult<PakKeyCoverage>.Success(
                PakKeyCoverageBuilder.FromEvidence(
                    family.FamilyName,
                    consulted,
                    candidateView,
                    evidence));
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "T3 pakkey recovery was cancelled.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                DiagnosticCode.UnknownPakFormat,
                $"A selected T3 archive could not be read: {exception.Message}");
        }
    }

    private static async ValueTask<ToolResult<bool>> RecoverArchiveAsync(
        string archivePath,
        string archiveHash,
        ReadOnlyMemory<byte> clientTable,
        HashSet<int> consulted,
        Dictionary<int, HashSet<byte>> aggregateCandidates,
        List<PakKeyEvidence> evidence,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        long archiveSize = stream.Length;
        if (archiveSize < T3Constants.HeaderOffset + T3Constants.HeaderSize)
        {
            return ArchiveFailure(
                DiagnosticCode.InvalidHeader,
                "The selected file is too small to contain a T3 archive header.",
                archivePath);
        }

        byte[] rawHeader = new byte[T3Constants.HeaderSize];
        stream.Position = T3Constants.HeaderOffset;
        await stream.ReadExactlyAsync(rawHeader, cancellationToken);
        ToolResult<T3ArchiveHeader> headerResult =
            T3ArchiveHeaderCodec.Decode(rawHeader);
        if (!headerResult.IsSuccess)
        {
            return ToolResult<bool>.Failure(
                headerResult.Error! with { Subject = archivePath });
        }

        T3ArchiveHeader header = headerResult.Value!;
        if (header.FirstRecordOffset < T3Constants.HeaderOffset
                + T3Constants.HeaderSize
            || header.FirstRecordOffset > archiveSize
            || header.EntryCount > archiveSize / T3Constants.EntrySize)
        {
            return ArchiveFailure(
                DiagnosticCode.InvalidHeader,
                "The T3 archive header points outside the selected file.",
                archivePath);
        }

        long recordOffset = header.FirstRecordOffset;
        byte recordSeed = header.InitialSeed;
        byte[] rawRecord = new byte[T3Constants.EntrySize];
        byte[] trialKey = new byte[T3Constants.PakKeySize];
        for (int ordinal = 0; ordinal < header.EntryCount; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recordOffset < 0
                || recordOffset > archiveSize - T3Constants.EntrySize)
            {
                return ArchiveFailure(
                    DiagnosticCode.RecordChainOverflow,
                    $"T3 record {ordinal} extends beyond the selected archive.",
                    archivePath);
            }

            stream.Position = recordOffset;
            await stream.ReadExactlyAsync(rawRecord, cancellationToken);
            int keyIndex = EffectiveKeyIndex(rawRecord);
            consulted.Add(keyIndex);

            uint storedSize = ReadStoredSize(rawRecord);
            long nextRecordOffset;
            try
            {
                nextRecordOffset = checked(
                    recordOffset + T3Constants.EntrySize + storedSize);
            }
            catch (OverflowException)
            {
                return ArchiveFailure(
                    DiagnosticCode.RecordChainOverflow,
                    $"T3 record {ordinal} has an overflowing payload size.",
                    archivePath);
            }

            int remainingRecords = header.EntryCount - ordinal - 1;
            long remainingHeaderBytes =
                (long)remainingRecords * T3Constants.EntrySize;
            bool validBoundary = storedSize > 0
                && nextRecordOffset > recordOffset
                && nextRecordOffset <= archiveSize - remainingHeaderBytes
                && (remainingRecords != 0 || nextRecordOffset == archiveSize);
            if (!validBoundary)
            {
                return ArchiveFailure(
                    DiagnosticCode.RecordChainOverflow,
                    $"T3 record {ordinal} does not lead to a valid next-record boundary.",
                    archivePath);
            }

            var recordCandidates = new HashSet<byte>();
            for (int candidate = byte.MinValue;
                 candidate <= byte.MaxValue;
                 candidate++)
            {
                trialKey[keyIndex] = (byte)candidate;
                ToolResult<ArchiveEntry> entryResult = T3EntryCodec.Decode(
                    rawRecord,
                    recordSeed,
                    trialKey,
                    clientTable.Span,
                    ordinal,
                    recordOffset);
                if (entryResult.IsSuccess
                    && HasPlausibleDecodedSize(entryResult.Value!)
                    && entryResult.Value!.PayloadOffset
                        + entryResult.Value.StoredSize == nextRecordOffset)
                {
                    byte value = (byte)candidate;
                    recordCandidates.Add(value);
                    evidence.Add(new PakKeyEvidence(
                        archivePath,
                        archiveHash,
                        ordinal,
                        keyIndex,
                        value));
                }
            }

            if (recordCandidates.Count == 0)
            {
                return ArchiveFailure(
                    DiagnosticCode.MismatchedPakKey,
                    $"T3 record {ordinal} has no structurally valid pakkey candidate.",
                    archivePath);
            }

            if (!aggregateCandidates.TryGetValue(
                keyIndex,
                out HashSet<byte>? indexCandidates))
            {
                indexCandidates = [];
                aggregateCandidates.Add(keyIndex, indexCandidates);
            }

            indexCandidates.UnionWith(recordCandidates);
            recordOffset = nextRecordOffset;
            recordSeed = unchecked((byte)(recordSeed - 1));
        }

        return ToolResult<bool>.Success(true);
    }

    private static bool HasPlausibleDecodedSize(ArchiveEntry entry)
    {
        long maximumDecodedSize = Math.Min(
            int.MaxValue,
            checked(entry.StoredSize * MaximumDecodedExpansionRatio));
        return entry.DecodedSize <= maximumDecodedSize;
    }

    private static int EffectiveKeyIndex(ReadOnlySpan<byte> rawRecord)
    {
        uint lowIndex = BinaryPrimitivesEx.ReadUInt32(rawRecord, 4);
        uint highIndex = BinaryPrimitivesEx.ReadUInt32(rawRecord, 8);
        return checked((int)(
            ((ulong)highIndex << 32 | lowIndex)
            % T3Constants.EffectivePakKeySize));
    }

    private static uint ReadStoredSize(ReadOnlySpan<byte> rawRecord)
    {
        ReadOnlySpan<byte> tail = rawRecord.Slice(T3Constants.TailOffset, 8);
        return tail[3]
            | ((uint)tail[5] << 8)
            | ((uint)tail[1] << 16)
            | ((uint)tail[7] << 24);
    }

    private static ToolResult<PakKeyCoverage> Failure(
        DiagnosticCode code,
        string message) =>
        ToolResult<PakKeyCoverage>.Failure(new ToolDiagnostic(code, message));

    private static ToolResult<bool> ArchiveFailure(
        DiagnosticCode code,
        string message,
        string subject) =>
        ToolResult<bool>.Failure(new ToolDiagnostic(code, message, subject));
}
