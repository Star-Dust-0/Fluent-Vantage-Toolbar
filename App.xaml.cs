using FluentLegionToolbar.Hardware;
using Microsoft.UI.Xaml;
namespace FluentLegionToolbar;
public partial class App : Application
{
    private MainWindow? window;
    private Mutex? instanceMutex;
    public App() {
        DebugSettings.XamlResourceReferenceFailed += (_,e) => Log(new Exception("Resource: "+e.Message));
        UnhandledException += (_,e) => { Log(new Exception("XAML event: "+e.Message)); Log(e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_,e) => Log(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        try { InitializeComponent(); } catch(Exception e) { Log(e); throw; }
    }
    static void Log(Exception e) { try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"launch-error.txt"),e.ToString()+"\n"); } catch { } }
    /// <summary>The elevated instance owns Local\FluentLegionToolbar.Instance; a normal process may be denied access to it, which also proves it exists.</summary>
    static bool WaitForElevatedInstance(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            try { if (Mutex.TryOpenExisting(@"Local\FluentLegionToolbar.Instance", out var existing)) { existing.Dispose(); return true; } }
            catch (UnauthorizedAccessException) { return true; }
            catch { }
            Thread.Sleep(100);
        }
        return false;
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var a = Environment.GetCommandLineArgs();
            if(a.Contains("--shutdown")){Startup.SignalExit();Environment.Exit(0);return;}
            if(a.Contains("--update-e2e")){SelfTest.RunUpdateE2E();return;}
            int gi = Array.IndexOf(a,"--tray-guid");
            if(gi>=0 && gi+1<a.Length && Guid.TryParse(a[gi+1],out var overrideGuid))TrayIcon.OverrideGuid = overrideGuid;
            bool capture = a.Contains("--capture"), background = a.Contains("--background");
            if (!capture)
            {
                // Not elevated and the scheduled task exists: let the task start the elevated instance and leave quietly.
                if (!Startup.IsAdmin() && !a.Contains("--no-elevate") && Startup.TryRunTask(background ? Startup.BackgroundTask : Startup.OpenTask))
                {
                    if (WaitForElevatedInstance(3000)) { Environment.Exit(0); return; }
                    // The task ran but no elevated instance showed up: keep running here (with the administrator notice) instead of vanishing.
                    FluentLegionToolbar.Hardware.DiagnosticLog.Write("Elevated instance did not start within 3 s; continuing without administrator rights.");
                }
                bool first;
                try { instanceMutex = new Mutex(true, @"Local\FluentLegionToolbar.Instance", out first); }
                catch (UnauthorizedAccessException) { first = false; }
                if (!first) { if (!background) Startup.SignalShow(); Environment.Exit(0); return; }
            }
            window = new MainWindow(background && !capture);
            if (!(background && !capture)) if (capture) window.Activate(); else window.OpenInitial();
        }
        catch(Exception e) { Log(e); throw; }
    }
}
