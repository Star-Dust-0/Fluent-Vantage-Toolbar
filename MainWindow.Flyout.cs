using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FluentLegionToolbar.Hardware;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
namespace FluentLegionToolbar;

public sealed partial class MainWindow
{

    // Event-handler entry points: an exception here would reach the XAML UnhandledException handler and end the process, so it is logged instead.
    async void HideFlyout()
    {
        try { await HideFlyoutAsync(); } catch (Exception ex) { DiagnosticLog.Write("HideFlyout failed: " + ex); }
    }
    async Task HideFlyoutAsync()
    {
        if(!flyoutVisible && !showingFlyout)return;
        int generation=++animationGeneration;lastHidden=DateTime.UtcNow;
        showingFlyout=false; // any show still in flight is now superseded and exits early without clearing the flag
        if(flyoutVisible)await AnimateFlyout(false);
        if(generation!=animationGeneration)return;
        flyoutVisible=false;SetCloaked(true);
        flyoutMotion?.Stop();Root.Opacity=1;Root.RenderTransform=new TranslateTransform();
    }
    Task AnimateFlyout(bool show)
    {
        flyoutMotion?.Stop();
        if(!MotionEnabled){Root.Opacity=1;Root.RenderTransform=new TranslateTransform();return Task.CompletedTask;}
        var transform=new TranslateTransform();Root.RenderTransform=transform;
        var motion=new Microsoft.UI.Xaml.Media.Animation.Storyboard();flyoutMotion=motion;
        double time=show?220:170;
        var easing=new Microsoft.UI.Xaml.Media.Animation.CubicEase{EasingMode=show?Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut:Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn};
        var slide=new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation{From=show?42:0,To=show?0:42,Duration=new Duration(TimeSpan.FromMilliseconds(time)),EasingFunction=easing,EnableDependentAnimation=true};
        var fade=new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation{From=show?0:1,To=show?1:0,Duration=new Duration(TimeSpan.FromMilliseconds(time)),EasingFunction=easing};
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide,transform);Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide,"Y");
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade,Root);Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade,"Opacity");motion.Children.Add(slide);motion.Children.Add(fade);
        var done=new TaskCompletionSource<bool>();motion.Completed+=(_,_)=>done.TrySetResult(true);motion.Begin();
        return done.Task;
    }

    async Task PresentPreparedAsync()
    {
        // Cloaking keeps DWM composition alive, unlike SW_HIDE. Wait for the XAML
        // render and composition commit before exposing the already-painted surface.
        Root.UpdateLayout();await NextFrameAsync();
        await Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(Root).Compositor.RequestCommitAsync();
        DwmFlush();
    }
    async void ShowFlyout(string? target, bool toggle)
    {
        if (toggle && (flyoutVisible || showingFlyout || (DateTime.UtcNow-lastHidden).TotalMilliseconds<350)) { HideFlyout();return; }
        int generation=++animationGeneration;showingFlyout=true;
        try {
            SetCloaked(true);flyoutVisible=false;
            view=target??"main";Render();if(preview)await geometryReady;else await Task.WhenAny(geometryReady,Task.Delay(2500));
            var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest).WorkArea;
            int x=area.X+area.Width-AppWindow.Size.Width/2-12,y=area.Y+area.Height+4;
            if(tray.TryGetAnchor(out int anchorX,out int anchorY)){x=anchorX;y=anchorY;area=DisplayArea.GetFromPoint(new PointInt32(x,y),DisplayAreaFallback.Nearest).WorkArea;}
            int left=Math.Clamp(x-AppWindow.Size.Width/2,area.X,Math.Max(area.X,area.X+area.Width-AppWindow.Size.Width));
            int top=Math.Clamp(y-AppWindow.Size.Height-16,area.Y,Math.Max(area.Y,area.Y+area.Height-AppWindow.Size.Height-12));
            AppWindow.Move(new PointInt32(left,top));HideFromTaskbar();
            if(!AppWindow.IsVisible)AppWindow.Show(false);
            flyoutMotion?.Stop();Root.Opacity=MotionEnabled?0:1;Root.RenderTransform=new TranslateTransform{Y=MotionEnabled?42:0};
            await PresentPreparedAsync();
            if(generation!=animationGeneration)return;
            SetCloaked(false);flyoutVisible=true;Activate();SetForegroundWindow(hwnd);
            await AnimateFlyout(true);
            PositionTitleBar();_ = Refresh();
            if(preview && args.Contains("--interactive")) {
                if(SystemBackdrop is not MicaBackdrop || Root.Background is not SolidColorBrush bg || bg.Color.A!=0)throw new InvalidOperationException("Mica obscured");
                if(AppWindow.Position.Y+AppWindow.Size.Height>area.Y+area.Height-12)throw new InvalidOperationException("Taskbar gap lost");
            }
        } catch(Exception ex) when(!preview) {
            DiagnosticLog.Write("ShowFlyout failed: "+ex);
            try{ if(!flyoutVisible){SetCloaked(false);Activate();SetForegroundWindow(hwnd);flyoutVisible=true;} }catch{}
        } finally { if(generation==animationGeneration)showingFlyout=false; } // a superseded show leaves the flag to whoever superseded it
    }

    void ListenForShowRequests()
    {
        try
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Startup.ShowEventName);
            var queue = DispatcherQueue.GetForCurrentThread();
            var thread = new Thread(() => { while (true) { signal.WaitOne(); queue.TryEnqueue(() => ShowFlyout(null, false)); } }) { IsBackground = true };
            thread.Start();
        }
        catch { }
    }

    (int, string)[] TrayItems() => [(1, L.T("menu.open")), (2, L.T("menu.settings")), (5, UpdateLabel), (3, L.T("menu.about")), (4, L.Ar ? "إغلاق التطبيق" : "Close app")];

    void ExitApp(){++animationGeneration;exiting=true;dashboard=null;contextMenu?.Close();contextMenu=null;Close();}
    void ListenForExitRequests()
    {
        try {var signal=new EventWaitHandle(false,EventResetMode.AutoReset,Startup.ExitEventName);var queue=DispatcherQueue.GetForCurrentThread();new Thread(()=>{while(true){signal.WaitOne();queue.TryEnqueue(ExitApp);}}){IsBackground=true}.Start();}catch{}
    }
    bool OpenTrayContext(int x,int y){contextMenu?.Close();contextMenu=new TrayMenuWindow(settings.Theme,TrayItems(),OnTrayMenu,x,y);contextMenu.Closed+=(_,_)=>contextMenu=null;contextMenu.Activate();return true;}
    void OnTrayMenu(int id){if(id==5){ShowFlyout("about",false);_ = CheckForUpdatesAsync();return;}if(id==4){ExitApp();return;}ShowFlyout(id switch { 2 => "settings", 3 => "about", _ => "main" }, false);}

    public void OpenInitial() => ShowFlyout(null, false);

    /// <summary>Windows taskbar theme ("Windows mode"), independent of the app's own theme setting. The white glyph vanishes on a light taskbar.</summary>
    internal static bool TaskbarIsLight()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1; }
        catch { return false; }
    }
    internal static string TrayIconPath() => Path.Combine(AppContext.BaseDirectory, TaskbarIsLight() ? "tray-dark.ico" : "tray-white.ico");}
