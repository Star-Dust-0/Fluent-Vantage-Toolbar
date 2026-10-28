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
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var a = Environment.GetCommandLineArgs();
            if(a.Contains("--shutdown")){Startup.SignalExit();Environment.Exit(0);return;}
            if(a.Contains("--update-e2e")){SelfTest.RunUpdateE2E();return;}
            int gi = Array.IndexOf(a,"--tray-guid");
            if(gi>=0 && gi+1<a.Length && Guid.TryParse(a[gi+1],out var overrideGuid))TrayIcon.OverrideGuid = overrideGuid;
            if(a.Contains("--tray-capture")) { MainWindow.TrayDiag = a[Array.IndexOf(a,"--capture")+1]+".log"; File.WriteAllText(MainWindow.TrayDiag, "launched\n"); }
            bool capture = a.Contains("--capture"), background = a.Contains("--background");
            if (!capture)
            {
                // Not elevated and the scheduled task exists: let the task start the elevated instance and leave quietly.
                if (!Startup.IsAdmin() && !a.Contains("--no-elevate") && Startup.TryRunTask(background ? Startup.BackgroundTask : Startup.OpenTask)) { Environment.Exit(0); return; }
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
