using System.Reflection.PortableExecutable;
using Mister.Core.Diagnostics;

namespace Mister.Core.Evidence;

public static class PeImageReader
{
    public static ToolResult<byte[]> ReadAtVirtualAddress(
        Stream stream,
        ulong virtualAddress,
        int byteCount)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);

        try
        {
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            PEHeaders headers = reader.PEHeaders;
            PEHeader? peHeader = headers.PEHeader;

            if (peHeader is null)
            {
                return Failure(
                    DiagnosticCode.UnsupportedClient,
                    "The selected client is not a PE image.");
            }

            if (headers.CoffHeader.Machine != Machine.I386 ||
                peHeader.Magic != PEMagic.PE32)
            {
                return Failure(
                    DiagnosticCode.ClientArchitectureMismatch,
                    "The selected client is not a 32-bit x86 PE image.");
            }

            ulong imageBase = peHeader.ImageBase;
            if (virtualAddress < imageBase ||
                virtualAddress - imageBase > uint.MaxValue)
            {
                return MissingTable(virtualAddress, byteCount);
            }

            ulong rva = virtualAddress - imageBase;
            foreach (SectionHeader section in headers.SectionHeaders)
            {
                ulong sectionRva = unchecked((uint)section.VirtualAddress);
                ulong rawSize = unchecked((uint)section.SizeOfRawData);
                if (rva < sectionRva)
                {
                    continue;
                }

                ulong sectionOffset = rva - sectionRva;
                if (sectionOffset > rawSize ||
                    (ulong)byteCount > rawSize - sectionOffset)
                {
                    continue;
                }

                ulong fileOffset =
                    unchecked((uint)section.PointerToRawData) + sectionOffset;
                if (fileOffset > long.MaxValue ||
                    fileOffset + (ulong)byteCount > (ulong)stream.Length)
                {
                    return MissingTable(virtualAddress, byteCount);
                }

                stream.Position = (long)fileOffset;
                byte[] bytes = new byte[byteCount];
                stream.ReadExactly(bytes);
                return ToolResult<byte[]>.Success(bytes);
            }

            return MissingTable(virtualAddress, byteCount);
        }
        catch (BadImageFormatException)
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                "The selected client is not a valid PE image.");
        }
        catch (IOException exception)
        {
            return Failure(
                DiagnosticCode.UnsupportedClient,
                $"The selected client could not be read: {exception.Message}");
        }
    }

    private static ToolResult<byte[]> MissingTable(ulong virtualAddress, int byteCount) =>
        Failure(
            DiagnosticCode.MissingClientTable,
            $"The PE image does not contain {byteCount} bytes at virtual address 0x{virtualAddress:X}.");

    private static ToolResult<byte[]> Failure(DiagnosticCode code, string message) =>
        ToolResult<byte[]>.Failure(new ToolDiagnostic(code, message));
}
