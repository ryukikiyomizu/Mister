namespace Mister.Core.Diagnostics;

public enum DiagnosticCode
{
    UnsupportedClient,
    ClientArchitectureMismatch,
    CaptureTimeout,
    ChildNetworkDenialFailure,
    AppContainerNetworkIsolationUnavailable,
    MissingClientTable,
    MissingPakKey,
    PartialPakKey,
    ConflictingPakKey,
    MismatchedPakKey,
    UnknownPakFormat,
    InvalidHeader,
    InvalidHeaderChecksum,
    InvalidCompressionFlag,
    InvalidCp949Path,
    InvalidPathFiller,
    UnsafeOutputPath,
    RecordChainOverflow,
    TrailingData,
    UnsupportedT2Compression,
    LzoCorruption,
    DecodedSizeMismatch,
    SignatureMismatch,
    PreviewFailure,
    OutputCollision,
    OutputPermissionFailure,
    Cancelled
}

public sealed record ToolDiagnostic(
    DiagnosticCode Code,
    string Message,
    string? Subject = null);

public sealed record ToolResult<T>(T? Value, ToolDiagnostic? Error)
{
    public bool IsSuccess => Error is null;

    public static ToolResult<T> Success(T value) => new(value, null);

    public static ToolResult<T> Failure(ToolDiagnostic error) => new(default, error);
}
