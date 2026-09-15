using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mister.Core.Diagnostics;
using Mister.Core.Formats;

namespace Mister.Core.Evidence;

public static class T2CaptureBundleSerializer
{
    private const int CurrentFormatVersion = 1;
    private const int TableLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static T2CaptureBundle Seal(T2CaptureBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return bundle with
        {
            IntegritySha256 = CalculateIntegrity(bundle)
        };
    }

    public static string Serialize(T2CaptureBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    public static ToolResult<T2CaptureBundle> Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            T2CaptureBundle? bundle =
                JsonSerializer.Deserialize<T2CaptureBundle>(json, JsonOptions);
            if (bundle is null)
            {
                return InvalidBundle(
                    "The T2 capture bundle did not contain an object.");
            }

            ToolDiagnostic? error = Validate(bundle);
            return error is null
                ? ToolResult<T2CaptureBundle>.Success(bundle)
                : ToolResult<T2CaptureBundle>.Failure(error);
        }
        catch (JsonException exception)
        {
            return InvalidBundle(
                $"The T2 capture bundle is not valid JSON: {exception.Message}");
        }
    }

    public static ToolResult<ClientEvidence> ToEvidence(
        T2CaptureBundle bundle,
        string currentClientSha256)
        => ToEvidenceCore(
            bundle,
            currentClientSha256,
            expectedValidationArchiveSha256: null);

    public static ToolResult<ClientEvidence> ToEvidence(
        T2CaptureBundle bundle,
        string currentClientSha256,
        string currentValidationArchiveSha256)
        => ToEvidenceCore(
            bundle,
            currentClientSha256,
            currentValidationArchiveSha256);

    private static ToolResult<ClientEvidence> ToEvidenceCore(
        T2CaptureBundle bundle,
        string currentClientSha256,
        string? expectedValidationArchiveSha256)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentClientSha256);

        ToolDiagnostic? validationError = Validate(bundle);
        if (validationError is not null)
        {
            return ToolResult<ClientEvidence>.Failure(validationError);
        }

        if (!IsSha256(currentClientSha256)
            || !FixedTimeHashEquals(
                bundle.ClientSha256,
                currentClientSha256))
        {
            return Failure<ClientEvidence>(
                "The T2 capture bundle was produced by a different CLIENT.EXE.");
        }

        if (expectedValidationArchiveSha256 is not null
            && (!IsSha256(expectedValidationArchiveSha256)
                || !FixedTimeHashEquals(
                    bundle.ValidationArchiveSha256,
                    expectedValidationArchiveSha256)))
        {
            return Failure<ClientEvidence>(
                "The T2 capture bundle was produced for a different validation PAK.");
        }

        return ToolResult<ClientEvidence>.Success(
            new ClientEvidence(
                GameFamily.Technika2,
                "T2 capture bundle",
                bundle.ClientSha256.ToLowerInvariant(),
                bundle.RecordXorTable.ToArray(),
                bundle.SubtractTable.ToArray(),
                SourceVirtualAddress: 0,
                ClientEvidenceValidationStatus.Unvalidated));
    }

    private static ToolDiagnostic? Validate(T2CaptureBundle bundle)
    {
        if (bundle.FormatVersion != CurrentFormatVersion)
        {
            return Diagnostic(
                $"Unsupported T2 capture-bundle format version {bundle.FormatVersion}.");
        }

        if (!IsSha256(bundle.ClientSha256)
            || !IsSha256(bundle.ValidationArchiveSha256)
            || !IsSha256(bundle.IntegritySha256))
        {
            return Diagnostic(
                "The T2 capture bundle contains an invalid SHA-256 value.");
        }

        if (bundle.SubtractTable is null
            || bundle.SubtractTable.Length != TableLength
            || bundle.RecordXorTable is null
            || bundle.RecordXorTable.Length != TableLength)
        {
            return Diagnostic(
                "The T2 capture bundle must contain one 256-dword subtraction table and one 256-byte record XOR table.");
        }

        if (bundle.CapturedAtUtc.Offset != TimeSpan.Zero)
        {
            return Diagnostic(
                "The T2 capture timestamp must be expressed in UTC.");
        }

        string expectedIntegrity = CalculateIntegrity(bundle);
        if (!FixedTimeHashEquals(expectedIntegrity, bundle.IntegritySha256))
        {
            return Diagnostic(
                "The T2 capture-bundle integrity check failed.");
        }

        return null;
    }

    private static string CalculateIntegrity(T2CaptureBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle.SubtractTable);
        ArgumentNullException.ThrowIfNull(bundle.RecordXorTable);
        using var content = new MemoryStream();
        using (var writer = new BinaryWriter(
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true))
        {
            writer.Write("Mister.T2CaptureBundle");
            writer.Write(bundle.FormatVersion);
            writer.Write(bundle.ClientSha256.ToLowerInvariant());
            writer.Write(bundle.SubtractTable.Length);
            foreach (uint value in bundle.SubtractTable)
            {
                writer.Write(value);
            }

            writer.Write(bundle.RecordXorTable.Length);
            writer.Write(bundle.RecordXorTable);
            writer.Write(bundle.CapturedAtUtc.UtcTicks);
            writer.Write(bundle.ValidationArchiveSha256.ToLowerInvariant());
        }

        return Convert.ToHexString(SHA256.HashData(content.ToArray()))
            .ToLowerInvariant();
    }

    private static bool FixedTimeHashEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right))
        {
            return false;
        }

        byte[] leftBytes = Convert.FromHexString(left);
        byte[] rightBytes = Convert.FromHexString(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsSha256(string? value) =>
        value is not null
        && value.Length == 64
        && value.All(static character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static ToolDiagnostic Diagnostic(string message) =>
        new(DiagnosticCode.UnsupportedClient, message);

    private static ToolResult<T2CaptureBundle> InvalidBundle(string message) =>
        Failure<T2CaptureBundle>(message);

    private static ToolResult<T> Failure<T>(string message) =>
        ToolResult<T>.Failure(Diagnostic(message));
}
