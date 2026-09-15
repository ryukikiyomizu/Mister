using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using Mister.Core.Diagnostics;

namespace Mister.Windows.Sandbox;

[SupportedOSPlatform("windows")]
internal static class SandboxSessionProtocol
{
    internal const int SessionKeyBytes = 32;
    internal const int SessionIdBytes = 16;
    private static readonly byte[] Domain =
        "Mister.WindowsSandboxCapture.v1"u8.ToArray();

    public static SandboxWorker.SandboxCaptureStatus CreateStatus(
        string sessionId,
        ReadOnlySpan<byte> sessionKey,
        bool isSuccess,
        string? bundleFile,
        string? bundleSha256,
        DiagnosticCode code,
        string message)
    {
        ValidateSessionId(sessionId);
        ValidateKey(sessionKey);
        ArgumentNullException.ThrowIfNull(message);
        var unsigned = new SandboxWorker.SandboxCaptureStatus(
            sessionId,
            isSuccess,
            bundleFile,
            bundleSha256,
            code,
            message,
            string.Empty);
        byte[] authenticator = ComputeAuthenticator(
            unsigned,
            sessionKey);
        try
        {
            return unsigned with
            {
                Authenticator = Convert.ToBase64String(authenticator)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authenticator);
        }
    }

    public static bool VerifyStatus(
        SandboxWorker.SandboxCaptureStatus? status,
        string expectedSessionId,
        ReadOnlySpan<byte> sessionKey)
    {
        if (status is null
            || !IsValidSessionId(expectedSessionId)
            || sessionKey.Length != SessionKeyBytes
            || status.Message is null
            || status.Message.Length > SandboxWorker.MaxStatusBytes
            || !FixedTimeSessionEquals(
                status.SessionId,
                expectedSessionId)
            || !TryDecodeAuthenticator(
                status.Authenticator,
                out byte[]? supplied))
        {
            return false;
        }

        byte[] expected = ComputeAuthenticator(
            status with { Authenticator = string.Empty },
            sessionKey);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                supplied,
                expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(supplied);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public static string Sha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();

    public static bool FixedTimeSha256Equals(
        string? left,
        string? right)
    {
        if (!TryDecodeSha256(left, out byte[]? leftBytes)
            || !TryDecodeSha256(right, out byte[]? rightBytes))
        {
            return false;
        }
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                leftBytes,
                rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    public static bool FixedTimeSessionIdEquals(
        string? left,
        string right) =>
        IsValidSessionId(right)
        && FixedTimeSessionEquals(left, right);

    private static byte[] ComputeAuthenticator(
        SandboxWorker.SandboxCaptureStatus status,
        ReadOnlySpan<byte> sessionKey)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(
            payload,
            new UTF8Encoding(false, true),
            leaveOpen: true))
        {
            writer.Write(Domain.Length);
            writer.Write(Domain);
            WriteString(writer, status.SessionId);
            writer.Write(status.IsSuccess);
            WriteString(writer, status.BundleFile);
            WriteString(writer, status.BundleSha256);
            writer.Write((int)status.Code);
            WriteString(writer, status.Message);
        }
        return HMACSHA256.HashData(
            sessionKey,
            payload.GetBuffer().AsSpan(
                0,
                checked((int)payload.Length)));
    }

    private static void WriteString(
        BinaryWriter writer,
        string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        writer.Write(encoded.Length);
        writer.Write(encoded);
    }

    private static bool FixedTimeSessionEquals(
        string? left,
        string right)
    {
        if (!TryDecodeSession(left, out byte[]? leftBytes)
            || !TryDecodeSession(right, out byte[]? rightBytes))
        {
            return false;
        }
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                leftBytes,
                rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool TryDecodeAuthenticator(
        string? value,
        out byte[] bytes)
    {
        bytes = [];
        if (value is null)
        {
            return false;
        }
        try
        {
            bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32)
            {
                return true;
            }
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecodeSession(
        string? value,
        out byte[] bytes)
    {
        bytes = [];
        if (!IsValidSessionId(value))
        {
            return false;
        }
        try
        {
            bytes = Convert.FromHexString(value!);
            return bytes.Length == SessionIdBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecodeSha256(
        string? value,
        out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != 64)
        {
            return false;
        }
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException(
                "A sandbox session ID must be 16 hexadecimal bytes.",
                nameof(sessionId));
        }
    }

    private static bool IsValidSessionId(string? value) =>
        value is not null
        && value.Length == SessionIdBytes * 2
        && value.All(static character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static void ValidateKey(ReadOnlySpan<byte> sessionKey)
    {
        if (sessionKey.Length != SessionKeyBytes)
        {
            throw new ArgumentException(
                "A sandbox session key must contain 32 bytes.",
                nameof(sessionKey));
        }
    }
}
