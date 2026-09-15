namespace Mister.Core.Evidence;

public sealed record T2CaptureBundle(
    int FormatVersion,
    string ClientSha256,
    uint[] SubtractTable,
    byte[] RecordXorTable,
    DateTimeOffset CapturedAtUtc,
    string ValidationArchiveSha256,
    string IntegritySha256);
