using Mister.Core.Formats;

namespace Mister.Core.Evidence;

public enum ClientEvidenceValidationStatus
{
    Unvalidated,
    StructurallyValidated
}

public sealed record ClientEvidence(
    GameFamily Family,
    string ClientPath,
    string ClientSha256,
    byte[] RecordXorTable,
    uint[]? T2SubtractTable,
    ulong SourceVirtualAddress,
    ClientEvidenceValidationStatus ValidationStatus =
        ClientEvidenceValidationStatus.Unvalidated);
