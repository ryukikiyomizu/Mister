using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Mister.Windows.AppContainer;

[SupportedOSPlatform("windows")]
internal interface IKillOnCloseJob : IDisposable
{
    void Assign(IntPtr process);
    void Terminate();
}

internal interface IKillOnCloseJobFactory
{
    IKillOnCloseJob Create();
}

internal sealed class KillOnCloseJobFactory : IKillOnCloseJobFactory
{
    public static KillOnCloseJobFactory Instance { get; } = new();
    public IKillOnCloseJob Create() => new KillOnCloseJob();
}

public sealed class KillOnCloseJob : IKillOnCloseJob
{
    private IntPtr handle;
    public KillOnCloseJob()
    {
        handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new NativeMethods.JobObjectExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose } };
        if (!NativeMethods.SetInformationJobObject(handle, NativeMethods.JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>())) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public void Assign(IntPtr process)
    {
        if (!NativeMethods.AssignProcessToJobObject(handle, process)) throw new ChildNetworkDenialFailure("Unable to assign suspended child to kill-on-close job.", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    public void Terminate()
    {
        if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(KillOnCloseJob));
        if (!NativeMethods.TerminateJobObject(handle, 1))
            throw new ChildNetworkDenialFailure(
                "Unable to terminate the AppContainer capture job.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }
    public void Dispose() { IntPtr old = Interlocked.Exchange(ref handle, IntPtr.Zero); if (old != IntPtr.Zero) NativeMethods.CloseHandle(old); }
}
