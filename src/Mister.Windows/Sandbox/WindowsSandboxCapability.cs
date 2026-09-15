using System.Runtime.Versioning;
using System.Diagnostics;

namespace Mister.Windows.Sandbox;

[SupportedOSPlatform("windows")]
public static class WindowsSandboxCapability
{
    private static readonly Lazy<bool> CliAvailable =
        new(ProbeCli);
    public static string ExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32",
        "WindowsSandbox.exe");

    public static string CliExecutablePath => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "WindowsApps",
        "wsb.exe");

    public static bool IsAvailable =>
        OperatingSystem.IsWindows()
        && File.Exists(ExecutablePath)
        && File.Exists(CliExecutablePath)
        && CliAvailable.Value;

    private static bool ProbeCli()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = CliExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--version");
            using Process? process = Process.Start(start);
            if (process is null || !process.WaitForExit(5000))
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
                return false;
            }
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            return process.ExitCode == 0
                && output.Length + error.Length <= 4096;
        }
        catch
        {
            return false;
        }
    }
}
