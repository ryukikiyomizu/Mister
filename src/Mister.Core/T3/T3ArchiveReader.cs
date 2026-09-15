using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Formats;

namespace Mister.Core.T3;

public sealed class T3ArchiveReader : IArchiveReader
{
    private readonly string _pakKeyRoot;
    private readonly bool _preferNamedKey;

    public T3ArchiveReader(
        ClientEvidence clientEvidence,
        string pakKeyRoot,
        bool preferNamedKey = false)
    {
        ArgumentNullException.ThrowIfNull(clientEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(pakKeyRoot);
        if (clientEvidence.Family != GameFamily.Technika3)
        {
            throw new ArgumentException(
                "T3 archive indexing requires Technika 3 client evidence.",
                nameof(clientEvidence));
        }

        if (clientEvidence.RecordXorTable.Length
            != T3Constants.RecordXorTableSize)
        {
            throw new ArgumentException(
                $"The T3 client record table must be exactly {T3Constants.RecordXorTableSize} bytes.",
                nameof(clientEvidence));
        }

        ClientEvidence = clientEvidence;
        _pakKeyRoot = pakKeyRoot;
        _preferNamedKey = preferNamedKey;
    }

    public ClientEvidence ClientEvidence { get; private set; }

    public GameFamily Family => GameFamily.Technika3;

    public async ValueTask<ToolResult<ArchiveIndex>> IndexAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        ToolResult<ArchiveIndex> result = await IndexWithNamedKeyAsync(
            archivePath,
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
        IndexByContentAsync(
            archivePath,
            depth,
            cancellationToken);

    private async ValueTask<ToolResult<ArchiveIndex>> IndexByContentAsync(
        string archivePath,
        DetectionDepth depth,
        CancellationToken cancellationToken)
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
            if (_preferNamedKey)
            {
                ToolResult<byte[]> namedKey = T3PakKeyResolver.Resolve(
                    Path.GetFileName(fullPath),
                    _pakKeyRoot);
                if (namedKey.IsSuccess)
                {
                    return await IndexWithKeyAsync(
                        fullPath,
                        namedKey.Value!,
                        depth,
                        cancellationToken);
                }

                if (namedKey.Error?.Code != DiagnosticCode.MissingPakKey)
                {
                    return ToolResult<ArchiveIndex>.Failure(
                        namedKey.Error!);
                }
            }

            return await IndexWithAnyAvailableKeyAsync(
                fullPath,
                initialError: null,
                depth,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "T3 archive indexing was cancelled.",
                fullPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                DiagnosticCode.UnknownPakFormat,
                $"The selected T3 archive could not be read: {exception.Message}",
                fullPath);
        }
    }

    private async ValueTask<ToolResult<ArchiveIndex>> IndexWithNamedKeyAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string fullPath = Path.GetFullPath(archivePath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolResult<byte[]> keyResult = T3PakKeyResolver.Resolve(
                Path.GetFileName(fullPath),
                _pakKeyRoot);
            if (!keyResult.IsSuccess)
            {
                return ToolResult<ArchiveIndex>.Failure(keyResult.Error!);
            }

            return await IndexWithKeyAsync(
                fullPath,
                keyResult.Value!,
                DetectionDepth.Full,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "T3 archive indexing was cancelled.",
                fullPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                DiagnosticCode.UnknownPakFormat,
                $"The selected T3 archive could not be read: {exception.Message}",
                fullPath);
        }
    }

    private async ValueTask<ToolResult<ArchiveIndex>>
        IndexWithAnyAvailableKeyAsync(
            string fullPath,
            ToolDiagnostic? initialError,
            DetectionDepth depth,
            CancellationToken cancellationToken)
    {
        ToolDiagnostic? lastError = initialError;
        var successful = new List<ArchiveIndex>(capacity: 2);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_pakKeyRoot))
        {
            return ToolResult<ArchiveIndex>.Failure(
                initialError
                ?? new ToolDiagnostic(
                    DiagnosticCode.MissingPakKey,
                    "No T3 pakkey evidence directory is available.",
                    _pakKeyRoot));
        }

        foreach (string keyPath in Directory.EnumerateFiles(_pakKeyRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] pakKey;
            try
            {
                pakKey = await File.ReadAllBytesAsync(
                    keyPath,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                lastError = new ToolDiagnostic(
                    DiagnosticCode.MissingPakKey,
                    $"A supplied T3 pakkey could not be read: {exception.Message}",
                    keyPath);
                continue;
            }

            if (pakKey.Length != T3Constants.PakKeySize)
            {
                lastError = new ToolDiagnostic(
                    DiagnosticCode.PartialPakKey,
                    $"The supplied T3 pakkey must be exactly {T3Constants.PakKeySize} bytes; received {pakKey.Length}.",
                    keyPath);
                continue;
            }

            if (!seenKeys.Add(Convert.ToHexString(pakKey)))
            {
                continue;
            }

            ToolResult<ArchiveIndex> candidate = await IndexWithKeyAsync(
                fullPath,
                pakKey,
                depth,
                cancellationToken);
            if (!candidate.IsSuccess)
            {
                lastError = candidate.Error;
                continue;
            }

            successful.Add(candidate.Value!);
            if (successful.Count > 1)
            {
                return Failure(
                    DiagnosticCode.ConflictingPakKey,
                    "More than one supplied T3 pakkey structurally validated the selected archive.",
                    fullPath);
            }
        }

        if (successful.Count == 1)
        {
            return ToolResult<ArchiveIndex>.Success(successful[0]);
        }

        return ToolResult<ArchiveIndex>.Failure(
            lastError
            ?? new ToolDiagnostic(
                DiagnosticCode.MissingPakKey,
                "No usable T3 pakkey evidence is available.",
                _pakKeyRoot));
    }

    private async ValueTask<ToolResult<ArchiveIndex>> IndexWithKeyAsync(
        string fullPath,
        byte[] pakKey,
        DetectionDepth depth,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            long archiveSize = stream.Length;
            if (archiveSize < T3Constants.HeaderOffset + T3Constants.HeaderSize)
            {
                return Failure(
                    DiagnosticCode.InvalidHeader,
                    "The selected file is too small to contain a T3 archive header.",
                    fullPath);
            }

            byte[] rawHeader = new byte[T3Constants.HeaderSize];
            stream.Position = T3Constants.HeaderOffset;
            await stream.ReadExactlyAsync(rawHeader, cancellationToken);
            ToolResult<T3ArchiveHeader> headerResult =
                T3ArchiveHeaderCodec.Decode(rawHeader);
            if (!headerResult.IsSuccess)
            {
                return ToolResult<ArchiveIndex>.Failure(
                    headerResult.Error! with { Subject = fullPath });
            }

            T3ArchiveHeader header = headerResult.Value!;
            if (header.FirstRecordOffset < T3Constants.HeaderOffset
                    + T3Constants.HeaderSize
                || header.FirstRecordOffset > archiveSize
                || header.EntryCount > archiveSize / T3Constants.EntrySize)
            {
                return Failure(
                    DiagnosticCode.InvalidHeader,
                    "The T3 archive header points outside the selected file.",
                    fullPath);
            }

            int entryLimit = depth == DetectionDepth.Full
                ? header.EntryCount
                : Math.Min(header.EntryCount, 1);
            var entries = new List<ArchiveEntry>(entryLimit);
            long recordOffset = header.FirstRecordOffset;
            byte recordSeed = header.InitialSeed;
            byte[] rawRecord = new byte[T3Constants.EntrySize];
            for (int ordinal = 0; ordinal < entryLimit; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (recordOffset < 0
                    || recordOffset > archiveSize - T3Constants.EntrySize)
                {
                    return Failure(
                        DiagnosticCode.RecordChainOverflow,
                        $"T3 record {ordinal} extends beyond the selected archive.",
                        fullPath);
                }

                stream.Position = recordOffset;
                await stream.ReadExactlyAsync(rawRecord, cancellationToken);
                ToolResult<ArchiveEntry> entryResult = T3EntryCodec.Decode(
                    rawRecord,
                    recordSeed,
                    pakKey,
                    ClientEvidence.RecordXorTable,
                    ordinal,
                    recordOffset);
                if (!entryResult.IsSuccess)
                {
                    return Failure(
                        DiagnosticCode.MismatchedPakKey,
                        $"The supplied pakkey and client table did not decode T3 record {ordinal}: {entryResult.Error!.Message}",
                        fullPath);
                }

                ArchiveEntry entry = entryResult.Value!;
                long nextRecordOffset;
                try
                {
                    nextRecordOffset = checked(entry.PayloadOffset + entry.StoredSize);
                }
                catch (OverflowException)
                {
                    return Failure(
                        DiagnosticCode.RecordChainOverflow,
                        $"T3 record {ordinal} has an overflowing payload size.",
                        fullPath);
                }

                if (nextRecordOffset <= recordOffset
                    || nextRecordOffset > archiveSize)
                {
                    return Failure(
                        DiagnosticCode.RecordChainOverflow,
                        $"T3 record {ordinal} payload extends beyond the selected archive.",
                        fullPath);
                }

                entries.Add(entry);
                recordOffset = nextRecordOffset;
                recordSeed = unchecked((byte)(recordSeed - 1));
            }

            if (depth == DetectionDepth.Full && recordOffset != archiveSize)
            {
                return Failure(
                    DiagnosticCode.TrailingData,
                    $"The decoded T3 record chain ends at {recordOffset}, not EOF {archiveSize}.",
                    fullPath);
            }

            return ToolResult<ArchiveIndex>.Success(
                new ArchiveIndex(
                    fullPath,
                    GameFamily.Technika3,
                    archiveSize,
                    entries,
                    recordOffset,
                    ArchiveHealth.Valid,
                    []));
        }
        catch (OperationCanceledException)
        {
            return Failure(
                DiagnosticCode.Cancelled,
                "T3 archive indexing was cancelled.",
                fullPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure(
                DiagnosticCode.UnknownPakFormat,
                $"The selected T3 archive could not be read: {exception.Message}",
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

    private static ToolResult<ArchiveIndex> Failure(
        DiagnosticCode code,
        string message,
        string subject) =>
        ToolResult<ArchiveIndex>.Failure(
            new ToolDiagnostic(code, message, subject));
}
