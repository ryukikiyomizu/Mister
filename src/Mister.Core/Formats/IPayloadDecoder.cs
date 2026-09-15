namespace Mister.Core.Formats;

public sealed record PayloadResult(
    long DecodedBytes,
    string Sha256,
    bool IsAllZero);

public interface IPayloadDecoder
{
    ValueTask<PayloadResult> DecodeAsync(
        string archivePath,
        ArchiveEntry entry,
        Stream destination,
        CancellationToken cancellationToken);
}
