using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Mister.Windows.AppContainer;

[SupportedOSPlatform("windows")]
public sealed class AppContainerProfile : IDisposable
{
    private bool disposed;
    private AppContainerProfile(string name, IntPtr sid) { Name = name; Sid = sid; }
    public string Name { get; }
    internal IntPtr Sid { get; private set; }
    public SecurityIdentifier SecurityIdentifier => new(Sid);

    public static AppContainerProfile CreateUnique() => Create($"Mister-{Guid.NewGuid():N}");
    public static AppContainerProfile Create(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        int hresult = NativeMethods.CreateAppContainerProfile(name, name, "Technika Tools disposable no-network capture profile", IntPtr.Zero, 0, out IntPtr sid);
        ThrowIfFailed(hresult, "Unable to create zero-capability AppContainer profile.");
        return new AppContainerProfile(name, sid);
    }

    public void GrantReadExecute(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var directoryInfo = new DirectoryInfo(directory);
        var security = directoryInfo.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(SecurityIdentifier, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directoryInfo.SetAccessControl(security);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Sid != IntPtr.Zero) { NativeMethods.FreeSid(Sid); Sid = IntPtr.Zero; }
        DeleteNamed(Name);
    }

    internal static void DeleteNamed(string name)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int result = NativeMethods.DeleteAppContainerProfile(name);
            if (result >= 0) return;
            Thread.Sleep(50 * (attempt + 1));
        }
        throw Failure("Unable to delete disposable AppContainer profile.", NativeMethods.DeleteAppContainerProfile(name));
    }

    internal static void ThrowIfFailed(int hresult, string message)
    {
        if (hresult < 0) throw Failure(message, hresult);
    }

    private static ChildNetworkDenialFailure Failure(string message, int hresult) => new(message, hresult, Marshal.GetExceptionForHR(hresult));
}

public sealed class ChildNetworkDenialFailure : Exception
{
    public ChildNetworkDenialFailure(string message, Exception? innerException = null) : base(message, innerException) { }
    internal ChildNetworkDenialFailure(string message, int hresult, Exception? innerException) : base(message, innerException) => HResult = hresult;
}
