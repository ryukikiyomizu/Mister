using System.Text;
using Mister.Core.Diagnostics;

namespace Mister.Core.Formats;

public static class Cp949PathDecoder
{
    public static ToolResult<string> Decode(
        ReadOnlySpan<byte> field,
        bool requireCcFiller,
        bool allowPatternRoot = false)
    {
        int terminator = field.IndexOf((byte)0);
        if (terminator < 0)
        {
            return InvalidPath("The path field has no NUL terminator.");
        }

        ReadOnlySpan<byte> filler = field[(terminator + 1)..];
        if (requireCcFiller && filler.IndexOfAnyExcept((byte)0xCC) >= 0)
        {
            return ToolResult<string>.Failure(new ToolDiagnostic(
                DiagnosticCode.InvalidPathFiller,
                "Bytes after the path terminator must all be 0xCC."));
        }

        string decodedPath;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding cp949 = Encoding.GetEncoding(
                949,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            decodedPath = cp949.GetString(field[..terminator]);
        }
        catch (DecoderFallbackException)
        {
            return InvalidPath("The path field is not valid CP949.");
        }

        if (!IsAllowedPath(decodedPath, allowPatternRoot))
        {
            return InvalidPath(
                allowPatternRoot
                    ? "The decoded path must start with the resource or pattern component."
                    : "The decoded path must start with the resource component.");
        }

        return ToolResult<string>.Success(decodedPath);
    }

    private static bool IsAllowedPath(string path, bool allowPatternRoot)
    {
        string[] components = path.Split(['\\', '/'], StringSplitOptions.None);
        return components.Length > 1
            && (string.Equals(
                    components[0],
                    "resource",
                    StringComparison.OrdinalIgnoreCase)
                || allowPatternRoot
                && string.Equals(
                    components[0],
                    "pattern",
                    StringComparison.OrdinalIgnoreCase))
            && components.All(static component => component.Length > 0);
    }

    private static ToolResult<string> InvalidPath(string message) =>
        ToolResult<string>.Failure(new ToolDiagnostic(DiagnosticCode.InvalidCp949Path, message));
}
