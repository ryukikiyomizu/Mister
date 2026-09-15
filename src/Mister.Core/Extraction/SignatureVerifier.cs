using System.Text;
using System.Xml;
using Mister.Core.Diagnostics;

namespace Mister.Core.Extraction;

public enum PayloadSignature
{
    Unknown,
    Png,
    Ogg,
    KnownZeroFilledOgg,
    Wav,
    Bmp,
    Jpeg,
    Bik,
    Xml,
    Text
}

public sealed record SignatureVerification(
    PayloadSignature Signature,
    string Extension);

public static class SignatureVerifier
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> TextExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt",
            ".lua",
            ".json",
            ".ini",
            ".cfg",
            ".csv",
            ".tsv",
            ".log"
        };

    public static ToolResult<SignatureVerification> Verify(
        string path,
        ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string extension = Path.GetExtension(path);
        PayloadSignature expected = ExpectedSignature(extension);
        if (expected == PayloadSignature.Unknown)
        {
            return Success(PayloadSignature.Unknown, extension);
        }

        bool valid;
        PayloadSignature actual = expected;
        switch (expected)
        {
            case PayloadSignature.Png:
                valid = payload.StartsWith(
                    new byte[]
                    {
                        0x89, 0x50, 0x4e, 0x47,
                        0x0d, 0x0a, 0x1a, 0x0a
                    });
                break;
            case PayloadSignature.Ogg:
                if (payload.Length > 0 && IsEntirelyZero(payload))
                {
                    valid = true;
                    actual = PayloadSignature.KnownZeroFilledOgg;
                }
                else
                {
                    valid = payload.StartsWith("OggS"u8);
                }

                break;
            case PayloadSignature.Wav:
                valid = payload.Length >= 12
                    && payload[..4].SequenceEqual("RIFF"u8)
                    && payload.Slice(8, 4).SequenceEqual("WAVE"u8);
                break;
            case PayloadSignature.Bmp:
                valid = payload.StartsWith("BM"u8);
                break;
            case PayloadSignature.Jpeg:
                valid = payload.Length >= 3
                    && payload[0] == 0xff
                    && payload[1] == 0xd8
                    && payload[2] == 0xff;
                break;
            case PayloadSignature.Bik:
                valid = payload.Length >= 4
                    && payload[0] == (byte)'B'
                    && payload[1] == (byte)'I'
                    && payload[2] == (byte)'K'
                    && payload[3] is >= (byte)'a' and <= (byte)'z';
                break;
            case PayloadSignature.Xml:
                valid = HasSaneXml(
                    payload,
                    IsLegacyNameListPath(path));
                break;
            case PayloadSignature.Text:
                valid = HasSaneText(payload);
                break;
            default:
                valid = false;
                break;
        }

        return valid
            ? Success(actual, extension)
            : ToolResult<SignatureVerification>.Failure(
                new ToolDiagnostic(
                    DiagnosticCode.SignatureMismatch,
                    $"The decoded payload does not match the expected {expected} signature.",
                    path));
    }

    public static async ValueTask<ToolResult<SignatureVerification>> VerifyAsync(
        string path,
        Stream payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        string extension = Path.GetExtension(path);
        PayloadSignature expected = ExpectedSignature(extension);
        if (expected == PayloadSignature.Unknown)
        {
            return Success(PayloadSignature.Unknown, extension);
        }

        if (expected == PayloadSignature.Xml)
        {
            return await VerifyXmlAsync(
                path,
                extension,
                payload,
                cancellationToken).ConfigureAwait(false);
        }

        if (expected == PayloadSignature.Text)
        {
            return await VerifyTextAsync(
                path,
                extension,
                payload,
                cancellationToken).ConfigureAwait(false);
        }

        int requiredPrefix = expected switch
        {
            PayloadSignature.Png => 8,
            PayloadSignature.Ogg => 4,
            PayloadSignature.Wav => 12,
            PayloadSignature.Bmp => 2,
            PayloadSignature.Jpeg => 3,
            PayloadSignature.Bik => 4,
            _ => 0
        };
        byte[] prefix = new byte[requiredPrefix];
        int prefixLength = await ReadAtMostAsync(
            payload,
            prefix,
            cancellationToken).ConfigureAwait(false);

        if (expected != PayloadSignature.Ogg)
        {
            return Verify(path, prefix.AsSpan(0, prefixLength));
        }

        bool allZero = prefixLength > 0
            && prefix.AsSpan(0, prefixLength).IndexOfAnyExcept((byte)0) < 0;
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await payload.ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0)
            {
                allZero = false;
            }
        }

        bool hasOgg = prefix.AsSpan(0, prefixLength).StartsWith("OggS"u8);
        if (hasOgg || allZero)
        {
            return Success(
                allZero
                    ? PayloadSignature.KnownZeroFilledOgg
                    : PayloadSignature.Ogg,
                extension);
        }

        return SignatureFailure(path, PayloadSignature.Ogg);
    }

    // Structured entries (config/script tables and XML) are small. A bounded
    // buffer lets stream extraction and in-memory preview use the same
    // UTF-8/CP949/UTF-16 and legacy-XML validation rules.
    private const int MaximumBufferedTextPayload = 256 * 1024 * 1024;

    private static async ValueTask<ToolResult<SignatureVerification>>
        VerifyTextAsync(
            string path,
            string extension,
            Stream payload,
            CancellationToken cancellationToken)
    {
        byte[]? buffered = await ReadStructuredPayloadAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        if (buffered is null)
        {
            return SignatureFailure(path, PayloadSignature.Text);
        }

        return HasSaneText(buffered)
            ? Success(PayloadSignature.Text, extension)
            : SignatureFailure(path, PayloadSignature.Text);
    }

    private static async ValueTask<ToolResult<SignatureVerification>>
        VerifyXmlAsync(
            string path,
            string extension,
            Stream payload,
            CancellationToken cancellationToken)
    {
        byte[]? buffered = await ReadStructuredPayloadAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        if (buffered is null)
        {
            return SignatureFailure(path, PayloadSignature.Xml);
        }

        return HasSaneXml(
            buffered,
            IsLegacyNameListPath(path))
            ? Success(PayloadSignature.Xml, extension)
            : SignatureFailure(path, PayloadSignature.Xml);
    }

    private static async ValueTask<byte[]?> ReadStructuredPayloadAsync(
        Stream payload,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = await payload.ReadAsync(
                chunk,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length > MaximumBufferedTextPayload - read)
            {
                return null;
            }

            await buffer.WriteAsync(
                chunk.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream payload,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await payload.ReadAsync(
                buffer[total..],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static ToolResult<SignatureVerification> SignatureFailure(
        string path,
        PayloadSignature expected) =>
        ToolResult<SignatureVerification>.Failure(
            new ToolDiagnostic(
                DiagnosticCode.SignatureMismatch,
                $"The decoded payload does not match the expected {expected} signature.",
                path));

    private static PayloadSignature ExpectedSignature(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".png" => PayloadSignature.Png,
            ".ogg" => PayloadSignature.Ogg,
            ".wav" => PayloadSignature.Wav,
            ".bmp" => PayloadSignature.Bmp,
            ".jpg" or ".jpeg" => PayloadSignature.Jpeg,
            ".bik" => PayloadSignature.Bik,
            ".xml" => PayloadSignature.Xml,
            _ when TextExtensions.Contains(extension) => PayloadSignature.Text,
            _ => PayloadSignature.Unknown
        };

    private static bool IsEntirelyZero(ReadOnlySpan<byte> payload)
    {
        foreach (byte value in payload)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSaneText(ReadOnlySpan<byte> payload) =>
        TryDecodeSaneText(payload, out _);

    internal static bool TryDecodeSaneText(
        ReadOnlySpan<byte> payload,
        out string decoded)
    {
        if (payload.Length >= 2
            && payload[0] == 0xFF
            && payload[1] == 0xFE)
        {
            return TryDecodeBomText(
                payload[2..],
                new UnicodeEncoding(
                    bigEndian: false,
                    byteOrderMark: true,
                    throwOnInvalidBytes: true),
                out decoded);
        }

        if (payload.Length >= 2
            && payload[0] == 0xFE
            && payload[1] == 0xFF)
        {
            return TryDecodeBomText(
                payload[2..],
                new UnicodeEncoding(
                    bigEndian: true,
                    byteOrderMark: true,
                    throwOnInvalidBytes: true),
                out decoded);
        }

        if (payload.Length >= 3
            && payload[0] == 0xEF
            && payload[1] == 0xBB
            && payload[2] == 0xBF)
        {
            payload = payload[3..];
        }

        // Legacy resource text (config/script tables) is frequently hand
        // edited line-by-line over the game's lifetime, so a single file can
        // legitimately mix plain ASCII/UTF-8 lines with CP949 Korean lines.
        // Requiring the *whole* buffer to decode under one encoding rejects
        // authentic content, so each line is validated independently. '\n'
        // (0x0A) never appears as a continuation byte in UTF-8 or in
        // Windows-949 lead/trail byte ranges, so splitting on it never cuts
        // a multi-byte character in half.
        var builder = new StringBuilder(payload.Length);
        int start = 0;
        for (int index = 0; index <= payload.Length; index++)
        {
            if (index != payload.Length && payload[index] != (byte)'\n')
            {
                continue;
            }

            if (!TryDecodeSaneTextLine(
                    payload[start..index],
                    out string line))
            {
                decoded = string.Empty;
                return false;
            }

            builder.Append(line);
            if (index != payload.Length)
            {
                builder.Append('\n');
            }

            start = index + 1;
        }

        decoded = builder.ToString();
        return true;
    }

    private static bool TryDecodeBomText(
        ReadOnlySpan<byte> payload,
        Encoding encoding,
        out string decoded)
    {
        try
        {
            decoded = encoding.GetString(payload);
            return HasSaneCharacters(decoded);
        }
        catch (DecoderFallbackException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    private static bool TryDecodeSaneTextLine(
        ReadOnlySpan<byte> line,
        out string decoded)
    {
        if (line.IsEmpty)
        {
            decoded = string.Empty;
            return true;
        }

        try
        {
            decoded = StrictUtf8.GetString(line);
            return HasSaneCharacters(decoded);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                decoded = StrictCp949().GetString(line);
                return HasSaneCharacters(decoded);
            }
            catch (DecoderFallbackException)
            {
                decoded = string.Empty;
                return false;
            }
        }
    }

    private static bool HasSaneCharacters(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (character is not ('\t' or '\r' or '\n')
                && char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static Encoding StrictCp949()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            949,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private static bool HasSaneXml(
        ReadOnlySpan<byte> payload,
        bool allowLegacyButtonName)
    {
        if (!TryDecodeSaneText(payload, out string decoded))
        {
            return false;
        }

        if (decoded.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || decoded.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var text = new StringReader(decoded);
            using XmlReader reader = XmlReader.Create(
                text,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    CloseInput = true
                });
            while (reader.Read())
            {
            }

            return true;
        }
        catch (XmlException)
        {
            // Shipping Technika resources include a legacy XML file with a
            // literal '<' as the complete value of a quoted button-name
            // attribute (the element is FourthLine in NameList.xml).
            // Normalize only that exact name-attribute anomaly for
            // validation, then require the entire document to parse normally.
            return allowLegacyButtonName
                && TryNormalizeLegacyButtonName(decoded, out string normalized)
                && IsWellFormedXml(normalized);
        }
    }

    private static bool IsLegacyNameListPath(string path) =>
        path.Replace('\\', '/').Equals(
            "resource/Script/NameEntry/NameList.xml",
            StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeLegacyButtonName(
        string text,
        out string normalized)
    {
        var builder = new StringBuilder(text.Length);
        bool insideTag = false;
        bool fourthLineElement = false;
        char quote = '\0';
        int valueStart = -1;
        bool nameAttribute = false;
        int replacements = 0;

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (!insideTag)
            {
                insideTag = character == '<';
                fourthLineElement = insideTag
                    && IsFourthLineStartTag(text, index);
                builder.Append(character);
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                    valueStart = -1;
                    nameAttribute = false;
                    builder.Append(character);
                    continue;
                }

                if (character == '<'
                    && fourthLineElement
                    && nameAttribute
                    && index == valueStart
                    && index + 1 < text.Length
                    && text[index + 1] == quote)
                {
                    builder.Append("&lt;");
                    replacements++;
                    continue;
                }

                builder.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                valueStart = index + 1;
                nameAttribute = IsNameAttributeBeforeQuote(text, index);
            }
            else if (character == '>')
            {
                insideTag = false;
                fourthLineElement = false;
            }

            builder.Append(character);
        }

        normalized = builder.ToString();
        return replacements > 0;
    }

    private static bool IsFourthLineStartTag(
        string text,
        int tagStart)
    {
        int index = tagStart + 1;
        if (index >= text.Length
            || text[index] is '/' or '!' or '?')
        {
            return false;
        }

        int start = index;
        while (index < text.Length
            && (char.IsLetterOrDigit(text[index])
                || text[index] is '_' or '-' or '.' or ':'))
        {
            index++;
        }

        return text.AsSpan(start, index - start).Equals(
            "FourthLine",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNameAttributeBeforeQuote(
        string text,
        int quoteIndex)
    {
        int index = quoteIndex - 1;
        while (index >= 0 && char.IsWhiteSpace(text[index]))
        {
            index--;
        }

        if (index < 0 || text[index] != '=')
        {
            return false;
        }

        index--;
        while (index >= 0 && char.IsWhiteSpace(text[index]))
        {
            index--;
        }

        int end = index + 1;
        while (index >= 0
            && (char.IsLetterOrDigit(text[index])
                || text[index] is '_' or '-' or '.' or ':'))
        {
            index--;
        }

        ReadOnlySpan<char> attributeName = text.AsSpan(index + 1, end - index - 1);
        return attributeName.Equals(
            "name",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWellFormedXml(string text)
    {
        try
        {
            using var input = new StringReader(text);
            using XmlReader reader = XmlReader.Create(
                input,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    CloseInput = true
                });
            while (reader.Read())
            {
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static ToolResult<SignatureVerification> Success(
        PayloadSignature signature,
        string extension) =>
        ToolResult<SignatureVerification>.Success(
            new SignatureVerification(signature, extension));
}
