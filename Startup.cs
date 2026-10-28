using System.Diagnostics;
using System.Security.Principal;
namespace FluentLegionToolbar;

/// <summary>Elevated start without a UAC prompt: a scheduled task that runs with highest privileges (SkipUAC pattern).</summary>
static class Startup
{
    public const string OpenTask = "FluentLegionToolbar-Open";
    public const string BackgroundTask = "FluentLegionToolbar";
    public const string ShowEventName = @"Local\FluentLegionToolbar.Show";

    public static bool IsAdmin()
    {
        try { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    static int RunSchtasks(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(info);
            if (process == null) return -1;
            process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd();
            if (!process.WaitForExit(8000)) return -1;
            return process.ExitCode;
        }
        catch { return -1; }
    }

    /// <summary>Starts the elevated scheduled task when it exists. Returns false when it is not installed.</summary>
    public static bool TryRunTask(string name)
    {
        if (RunSchtasks($"/query /tn \"{name}\"") != 0) return false;
        return RunSchtasks($"/run /tn \"{name}\"") == 0;
    }

    public static void SignalShow()
    {
        try { using var handle = EventWaitHandle.OpenExisting(ShowEventName); handle.Set(); } catch { }
    }
}
