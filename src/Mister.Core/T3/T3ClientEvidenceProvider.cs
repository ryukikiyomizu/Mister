using Mister.Core.Diagnostics;
using Mister.Core.Evidence;
using Mister.Core.Formats;

namespace Mister.Core.T3;

public sealed class T3ClientEvidenceProvider
{
    private const ulong RecordTableVirtualAddress = 0x11AAB68;
    private const int RecordTableLength = 256;

    public async ValueTask<ToolResult<ClientEvidence>> LoadAsync(
        string clientPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                clientPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            ToolResult<byte[]> tableResult = PeImageReader.ReadAtVirtualAddress(
                stream,
                RecordTableVirtualAddress,
                RecordTableLength);
            if (!tableResult.IsSuccess)
            {
                return ToolResult<ClientEvidence>.Failure(
                    tableResult.Error! with { Subject = clientPath });
            }

            string hash = await FileHasher.Sha256Async(clientPath, cancellationToken);
            return ToolResult<ClientEvidence>.Success(
                new ClientEvidence(
                    GameFamily.Technika3,
                    Path.GetFullPath(clientPath),
                    hash,
                    tableResult.Value!,
                    T2SubtractTable: null,
                    RecordTableVirtualAddress,
                    ClientEvidenceValidationStatus.Unvalidated));
        }
        catch (OperationCanceledException)
        {
            return ToolResult<ClientEvidence>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.Cancelled,
                    "Client evidence loading was cancelled.",
                    clientPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return ToolResult<ClientEvidence>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.UnsupportedClient,
                    $"The selected client could not be read: {exception.Message}",
                    clientPath));
        }
    }
}
