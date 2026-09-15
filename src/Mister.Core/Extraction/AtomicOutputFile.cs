using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace Mister.Core.Extraction;

internal delegate OpenedFileProof AtomicOutputHandleVerifier(
    SafeFileHandle handle,
    string expectedPath);

public sealed class AtomicOutputFile : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly AtomicOutputHandleVerifier? _handleVerifier;
    private readonly OpenedFileProof? _openedPartProof;
    private readonly SafeFileHandle? _destinationDirectory;
    private readonly OpenedFileProof? _openedDirectoryProof;
    private readonly Action<SafeFileHandle>? _deleteOpenedPart;
    private Stream? _stream;
    private OutputState _state;
    private bool _cleanupAttempted;

    private AtomicOutputFile(
        string finalPath,
        string partPath,
        Stream stream,
        AtomicOutputHandleVerifier? handleVerifier = null,
        OpenedFileProof? openedPartProof = null,
        SafeFileHandle? destinationDirectory = null,
        OpenedFileProof? openedDirectoryProof = null,
        Action<SafeFileHandle>? deleteOpenedPart = null)
    {
        FinalPath = finalPath;
        PartPath = partPath;
        _stream = stream;
        _handleVerifier = handleVerifier;
        _openedPartProof = openedPartProof;
        _destinationDirectory = destinationDirectory;
        _openedDirectoryProof = openedDirectoryProof;
        _deleteOpenedPart = deleteOpenedPart;
    }

    private enum OutputState
    {
        Open,
        Publishing,
        Published,
        Failed,
        Disposed
    }

    public string FinalPath { get; }

    public string PartPath { get; }

    public Stream Stream
    {
        get
        {
            _operationGate.Wait();
            try
            {
                return _state == OutputState.Open && _stream is not null
                    ? _stream
                    : throw new ObjectDisposedException(
                        nameof(AtomicOutputFile));
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    public static ValueTask<AtomicOutputFile> CreateAsync(
        string finalPath,
        CancellationToken cancellationToken = default) =>
        CreateAsyncCore(
            finalPath,
            static partPath => new FileStream(
                partPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan),
            cancellationToken);

    internal static ValueTask<AtomicOutputFile> CreateAsync(
        string finalPath,
        Func<string, Stream> streamFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        return CreateAsyncCore(
            finalPath,
            streamFactory,
            cancellationToken);
    }

    internal static ValueTask<AtomicOutputFile> CreateVerifiedAsync(
        string finalPath,
        Action<string>? beforePartCreate,
        Action<string>? beforeRelativePartCreate,
        AtomicOutputHandleVerifier handleVerifier,
        CancellationToken cancellationToken = default,
        Action<SafeFileHandle>? deleteOpenedPart = null)
    {
        ArgumentNullException.ThrowIfNull(handleVerifier);
        return OperatingSystem.IsWindows()
            ? CreateVerifiedWindows(
                finalPath,
                beforePartCreate,
                beforeRelativePartCreate,
                handleVerifier,
                cancellationToken,
                deleteOpenedPart)
            : CreateAsyncCore(
                finalPath,
                static partPath => new FileStream(
                    partPath,
                    FileMode.CreateNew,
                FileAccess.ReadWrite,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous
                        | FileOptions.SequentialScan),
                cancellationToken,
                beforePartCreate,
                handleVerifier);
    }

    private static ValueTask<AtomicOutputFile> CreateAsyncCore(
        string finalPath,
        Func<string, Stream> streamFactory,
        CancellationToken cancellationToken,
        Action<string>? beforePartCreate = null,
        AtomicOutputHandleVerifier? handleVerifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        cancellationToken.ThrowIfCancellationRequested();

        string canonicalFinalPath = Path.GetFullPath(finalPath);
        string? directory = Path.GetDirectoryName(canonicalFinalPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException(
                "The output path must have a parent directory.",
                nameof(finalPath));
        }

        Directory.CreateDirectory(directory);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string partPath = Path.Combine(
                directory,
                $".{Path.GetFileName(canonicalFinalPath)}.{Guid.NewGuid():N}.part");
            Stream? stream = null;
            try
            {
                beforePartCreate?.Invoke(partPath);
                stream = streamFactory(partPath);
                OpenedFileProof? proof = VerifyOpenedPart(
                    stream,
                    partPath,
                    handleVerifier);
                return ValueTask.FromResult(
                    new AtomicOutputFile(
                        canonicalFinalPath,
                        partPath,
                        stream,
                        handleVerifier,
                        proof));
            }
            catch (IOException) when (stream is null && File.Exists(partPath))
            {
                stream?.Dispose();
            }
            catch
            {
                try
                {
                    stream?.Dispose();
                }
                finally
                {
                    TryDeletePath(partPath);
                }

                throw;
            }
        }

        throw new IOException(
            "A unique temporary output file could not be created.");
    }

    public async ValueTask PublishAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            switch (_state)
            {
                case OutputState.Published:
                    return;
                case OutputState.Disposed:
                    throw new ObjectDisposedException(
                        nameof(AtomicOutputFile));
                case OutputState.Failed:
                    throw new InvalidOperationException(
                        "The atomic output publication previously failed.");
                case OutputState.Publishing:
                    throw new InvalidOperationException(
                        "The atomic output is already publishing.");
            }

            _state = OutputState.Publishing;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifyStillBound();
                await _stream!.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (_destinationDirectory is not null)
                {
                    PublishWindowsHandle();
                    await CloseStreamAsync().ConfigureAwait(false);
                    CloseDirectory();
                    _state = OutputState.Published;
                    return;
                }

                await CloseStreamAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(FinalPath) || Directory.Exists(FinalPath))
                {
                    throw new IOException(
                        $"The output already exists: {FinalPath}");
                }

                File.Move(PartPath, FinalPath, overwrite: false);
                _state = OutputState.Published;
            }
            catch
            {
                _state = OutputState.Failed;
                await CloseAndDeletePartAsync(suppressCloseFailure: true)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        _operationGate.Wait();
        try
        {
            if (_state == OutputState.Disposed)
            {
                return;
            }

            bool mustDeletePart = _state != OutputState.Published;
            _state = OutputState.Disposed;
            if (mustDeletePart)
            {
                CloseAndDeletePart();
            }
            else
            {
                CloseStream();
                CloseDirectory();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            if (_state == OutputState.Disposed)
            {
                return;
            }

            bool mustDeletePart = _state != OutputState.Published;
            _state = OutputState.Disposed;
            if (mustDeletePart)
            {
                await CloseAndDeletePartAsync(suppressCloseFailure: false)
                    .ConfigureAwait(false);
            }
            else
            {
                await CloseStreamAsync().ConfigureAwait(false);
                CloseDirectory();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void CloseStream()
    {
        Stream? stream = _stream;
        try
        {
            stream?.Dispose();
        }
        finally
        {
            _stream = null;
        }
    }

    private async ValueTask CloseStreamAsync()
    {
        Stream? stream = _stream;
        try
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _stream = null;
        }
    }

    private void CloseAndDeletePart()
    {
        if (_cleanupAttempted)
        {
            CloseDirectory();
            return;
        }

        _cleanupAttempted = true;
        if (_destinationDirectory is null)
        {
            try
            {
                CloseStream();
            }
            finally
            {
                TryDeletePart();
            }

            return;
        }

        if (_stream is null)
        {
            CloseDirectory();
            return;
        }

        Exception? cleanupFailure = null;
        try
        {
            MarkPartForDeletion();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ObjectDisposedException)
        {
            cleanupFailure = exception;
        }

        Exception? closeFailure = null;
        try
        {
            CloseStream();
        }
        catch (Exception exception)
        {
            closeFailure = exception;
        }
        finally
        {
            CloseDirectory();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        if (closeFailure is not null)
        {
            ExceptionDispatchInfo.Capture(closeFailure).Throw();
        }
    }

    private async ValueTask CloseAndDeletePartAsync(
        bool suppressCloseFailure)
    {
        if (_cleanupAttempted)
        {
            CloseDirectory();
            return;
        }

        _cleanupAttempted = true;
        if (_destinationDirectory is null)
        {
            Exception? publicCloseFailure = null;
            try
            {
                await CloseStreamAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                publicCloseFailure = exception;
            }
            finally
            {
                TryDeletePart();
            }

            if (!suppressCloseFailure
                && publicCloseFailure is not null)
            {
                ExceptionDispatchInfo.Capture(
                    publicCloseFailure).Throw();
            }

            return;
        }

        if (_stream is null)
        {
            CloseDirectory();
            return;
        }

        Exception? cleanupFailure = null;
        try
        {
            MarkPartForDeletion();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ObjectDisposedException)
        {
            cleanupFailure = exception;
        }

        Exception? closeFailure = null;
        try
        {
            await CloseStreamAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            closeFailure = exception;
        }
        finally
        {
            CloseDirectory();
        }

        if (!suppressCloseFailure
            && cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        if (!suppressCloseFailure && closeFailure is not null)
        {
            ExceptionDispatchInfo.Capture(closeFailure).Throw();
        }
    }

    private void TryDeletePart()
    {
        TryDeletePath(PartPath);
    }

    private void MarkPartForDeletion()
    {
        if (_destinationDirectory is null
            || _stream is not FileStream file
            || !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                "Handle-verified atomic output cleanup is incomplete.");
        }

        (_deleteOpenedPart
            ?? OpenedFileHandle.DeleteWindowsHandleOnClose)(
                file.SafeFileHandle);
    }

    private static ValueTask<AtomicOutputFile> CreateVerifiedWindows(
        string finalPath,
        Action<string>? beforePartCreate,
        Action<string>? beforeRelativePartCreate,
        AtomicOutputHandleVerifier handleVerifier,
        CancellationToken cancellationToken,
        Action<SafeFileHandle>? deleteOpenedPart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        cancellationToken.ThrowIfCancellationRequested();
        string canonicalFinalPath = Path.GetFullPath(finalPath);
        string? directory = Path.GetDirectoryName(canonicalFinalPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException(
                "The output path must have a parent directory.",
                nameof(finalPath));
        }

        Directory.CreateDirectory(directory);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string partPath = Path.Combine(
                directory,
                $".{Path.GetFileName(canonicalFinalPath)}.{Guid.NewGuid():N}.part");
            SafeFileHandle? directoryHandle = null;
            FileStream? stream = null;
            try
            {
                beforePartCreate?.Invoke(partPath);
                directoryHandle =
                    OpenedFileHandle.OpenWindowsDirectory(directory);
                OpenedFileProof directoryProof =
                    handleVerifier(directoryHandle, directory);
                beforeRelativePartCreate?.Invoke(partPath);
                stream =
                    OpenedFileHandle.CreateWindowsOutputPart(
                        directoryHandle,
                        Path.GetFileName(partPath));
                OpenedFileProof partProof =
                    handleVerifier(stream.SafeFileHandle, partPath);
                return ValueTask.FromResult(
                    new AtomicOutputFile(
                        canonicalFinalPath,
                        partPath,
                        stream,
                        handleVerifier,
                        partProof,
                        directoryHandle,
                        directoryProof,
                        deleteOpenedPart));
            }
            catch (IOException exception) when (
                stream is null
                    && OpenedFileHandle.IsWindowsNameCollision(
                        exception))
            {
                directoryHandle?.Dispose();
            }
            catch (Exception operationFailure)
            {
                Exception? cleanupFailure = null;
                if (stream is not null)
                {
                    try
                    {
                        MarkOpenedPartForDeletion(
                            stream,
                            deleteOpenedPart);
                    }
                    catch (Exception exception) when (
                        exception is IOException
                            or UnauthorizedAccessException
                            or ObjectDisposedException)
                    {
                        cleanupFailure = exception;
                    }
                }

                try
                {
                    stream?.Dispose();
                }
                finally
                {
                    directoryHandle?.Dispose();
                }

                if (cleanupFailure is not null)
                {
                    throw new IOException(
                        $"Verified atomic output cleanup failed: {cleanupFailure.Message}",
                        new AggregateException(
                            operationFailure,
                            cleanupFailure));
                }

                throw;
            }
        }

        throw new IOException(
            "A unique temporary output file could not be created.");
    }

    private static OpenedFileProof? VerifyOpenedPart(
        Stream stream,
        string partPath,
        AtomicOutputHandleVerifier? handleVerifier)
    {
        if (handleVerifier is null)
        {
            return null;
        }

        if (stream is not FileStream file)
        {
            throw new InvalidOperationException(
                "Handle-verified atomic output requires a file stream.");
        }

        return handleVerifier(file.SafeFileHandle, partPath);
    }

    private static void MarkOpenedPartForDeletion(
        FileStream? stream,
        Action<SafeFileHandle>? deleteOpenedPart)
    {
        if (stream is null || !OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                "Handle-verified atomic output cleanup is incomplete.");
        }

        (deleteOpenedPart
            ?? OpenedFileHandle.DeleteWindowsHandleOnClose)(
                stream.SafeFileHandle);
    }

    private void VerifyStillBound()
    {
        if (_handleVerifier is null || _openedPartProof is null)
        {
            return;
        }

        if (_stream is not FileStream file)
        {
            throw new InvalidOperationException(
                "Handle-verified atomic output lost its file stream.");
        }

        OpenedFileProof currentPart =
            _handleVerifier(file.SafeFileHandle, PartPath);
        if (!OpenedFileHandle.RepresentsSameObject(
                _openedPartProof.Value,
                currentPart))
        {
            throw new IOException(
                "The atomic output part no longer identifies the opened file.");
        }

        if (_destinationDirectory is null
            || _openedDirectoryProof is null)
        {
            return;
        }

        OpenedFileProof currentDirectory =
            _handleVerifier(
                _destinationDirectory,
                Path.GetDirectoryName(FinalPath)!);
        if (!OpenedFileHandle.RepresentsSameObject(
                _openedDirectoryProof.Value,
                currentDirectory))
        {
            throw new IOException(
                "The atomic output directory no longer identifies the opened directory.");
        }
    }

    private void PublishWindowsHandle()
    {
        if (_stream is not FileStream file
            || _destinationDirectory is null
            || _handleVerifier is null
            || _openedPartProof is null)
        {
            throw new InvalidOperationException(
                "The Windows atomic output publication is incomplete.");
        }

        OpenedFileHandle.RenameWindowsHandleNoReplace(
            file.SafeFileHandle,
            _destinationDirectory,
            Path.GetFileName(FinalPath));
    }

    private void CloseDirectory()
    {
        _destinationDirectory?.Dispose();
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
