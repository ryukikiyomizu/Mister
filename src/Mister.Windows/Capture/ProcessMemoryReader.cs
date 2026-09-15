using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Mister.Windows.Capture;

public sealed class ProcessMemoryReader
{
    private const int MaxReadSize = 1024 * 1024;
    private readonly Func<IntPtr, ulong, byte[], int> read;

    public ProcessMemoryReader() : this(ReadNative) { }
    internal ProcessMemoryReader(Func<IntPtr, ulong, byte[], int> read) => this.read = read;

    public byte[] ReadExact(IntPtr process, ulong address, int size)
    {
        if (size is <= 0 or > MaxReadSize) throw new ArgumentOutOfRangeException(nameof(size));
        if (address > ulong.MaxValue - (ulong)size) throw new ArgumentOutOfRangeException(nameof(address));
        byte[] bytes = new byte[size];
        int actual = read(process, address, bytes);
        if (actual != size) throw new IOException($"ReadProcessMemory returned {actual} of {size} requested bytes.");
        return bytes;
    }

    public ulong GetImageBase(IntPtr process)
    {
        if (Environment.Is64BitProcess)
        {
            int wow64Status = NtQueryInformationProcess(process, 26, out IntPtr wow64Peb, (uint)IntPtr.Size, out _);
            if (wow64Status != 0) throw new IOException($"Unable to query the staged CLIENT architecture (NTSTATUS 0x{wow64Status:X8}).");
            if (wow64Peb != IntPtr.Zero)
            {
                byte[] raw = ReadExact(process, checked((ulong)wow64Peb.ToInt64() + 8), sizeof(uint));
                uint imageBase = BitConverter.ToUInt32(raw);
                if (imageBase == 0) throw new IOException("The 32-bit staged CLIENT reported an invalid image base.");
                return imageBase;
            }
        }
        int status = NtQueryInformationProcess(process, 0, out ProcessBasicInformation information, (uint)Marshal.SizeOf<ProcessBasicInformation>(), out _);
        if (status != 0) throw new IOException($"Unable to query the staged CLIENT image base (NTSTATUS 0x{status:X8}).");
        ulong peb = unchecked((ulong)information.PebBaseAddress.ToInt64());
        byte[] native = ReadExact(process, checked(peb + (Environment.Is64BitProcess ? 0x10UL : 8UL)), IntPtr.Size);
        ulong image = IntPtr.Size == 8 ? BitConverter.ToUInt64(native) : BitConverter.ToUInt32(native);
        if (image == 0) throw new IOException("The staged CLIENT reported an invalid image base.");
        return image;
    }

    private static int ReadNative(IntPtr process, ulong address, byte[] destination)
    {
        if (!ReadProcessMemory(process, new IntPtr(checked((long)address)), destination, (nuint)destination.Length, out nuint read))
            throw new IOException("ReadProcessMemory failed.", new Win32Exception(Marshal.GetLastWin32Error()));
        return checked((int)read);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out IntPtr information, uint length, out uint returnLength);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out ProcessBasicInformation information, uint length, out uint returnLength);
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }
}
