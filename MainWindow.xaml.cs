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

public sealed partial class MainWindow : Window
{
    sealed record TileDef(string Id, string LabelKey, string Glyph);
    static readonly TileDef[] Tiles =
    [
        new("thermal", "tile.thermal", "\uE713"), new("mic", "tile.mic", "\uE720"), new("conserve", "tile.conserve", "\uEA93"),
        new("rapid", "tile.rapid", "\uE945"), new("touchpad", "tile.touchpad", "\uEFA5"),
        new("fn", "tile.fn", ""), new("refresh", "tile.refresh", "\uE7F4"), new("usb", "tile.usb", "\uE88E"),
    ];

    IEnumerable<TileDef> OrderedTiles() => settings.TileOrder.Distinct().Select(id=>Tiles.FirstOrDefault(t=>t.Id==id)).Where(t=>t!=null).Cast<TileDef>().Concat(Tiles.Where(t=>!settings.TileOrder.Contains(t.Id)));

    readonly LegionDevice device = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly TrayIcon tray;
    readonly AppSettings settings;
    readonly string[] args = Environment.GetCommandLineArgs();
    readonly bool preview;
    readonly IntPtr hwnd;
    Grid canvas = null!;
    readonly Grid titleBar = new() { MinHeight = 36 };
    readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Dictionary<string, ToggleButton> tileButtons = new();
    DeviceState? current;
    DeviceDashboardWindow? dashboard;
    TrayMenuWindow? contextMenu;
    BatteryDetails? batteryDetails;
    WarrantyResult? warranty;
    string? warrantyError;
    bool warrantyLoading;
    bool busy, dialogOpen, exiting, checkingUpdates;
    readonly List<Button> updateButtons = new();
    readonly List<Button> installButtons = new();
    readonly List<TextBlock> updateResults = new();
    readonly List<HyperlinkButton> updateLinks = new();
    string manualUpdateState = "idle";
    string? manualUpdateUrl, manualUpdateVersion;
    UpdateAsset? manualUpdateAsset;
    UpdateToast? updateToast;
    readonly CancellationTokenSource updateLifetime=new();
    bool flyoutVisible, showingFlyout, reorderMode, reordering;
    StackPanel? reorderRows;
    int reorderPresses,reorderMoves,reorderReleases;
    string reorderTrace="";
    int animationGeneration;
    Microsoft.UI.Xaml.Media.Animation.Storyboard? flyoutMotion;
    string view = "main";
    DateTime lastHidden = DateTime.MinValue;
    TextBlock? percentText, chargeText, statusText;
    FontIcon? plugIcon;
    Border? batteryFill; Grid? batteryFillHost;
    Windows.UI.Color batteryColor = Windows.UI.Color.FromArgb(255, 156, 218, 155);

    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd,ref NativePoint point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref uint value,int size);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out uint value,int size);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr insertAfter,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd,IntPtr rect,IntPtr region,uint flags);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);


    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr hwnd,IntPtr region);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);

    public MainWindow(bool startHidden)
    {
        InitializeComponent();
        preview = args.Contains("--capture");
        if (preview) Hardware.DeviceInfo.Override = "Legion 5 15ITH6H";
        settings = preview ? new AppSettings { Language = args.Contains("--arabic") ? "ar" : "en", Theme = args.Contains("--dark") ? "dark" : "light" } : AppSettings.Load();
        L.Set(settings.Language);
        Title = "Fluent Vantage Toolbar";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetCloaked(true); // Compose while invisible; SW_SHOW exposes an unpainted XAML surface.
        uint disableTransitions=1;DwmSetWindowAttribute(hwnd,3,ref disableTransitions,4);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); } catch { }
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true; }
        // Presenter flag setters may restore WS_BORDER after SetBorderAndTitleBar.
        var windowStyle=GetWindowLongPtr(hwnd,-16).ToInt64() & ~0x00C40000L;
        SetWindowLongPtr(hwnd,-16,new IntPtr(windowStyle));
        SetWindowPos(hwnd,IntPtr.Zero,0,0,0,0,0x0037);
        uint noBorder=0xFFFFFFFE;DwmSetWindowAttribute(hwnd,34,ref noBorder,4);
        HideFromTaskbar();
        double scale = GetDpiForWindow(hwnd) / 96d;
        AppWindow.ResizeClient(new SizeInt32((int)(520 * scale), (int)(520 * scale)));

        Root.Padding = new Thickness(24, 15, 24, 24);
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        canvas = new Grid { Height = 492 };
        canvas.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        canvas.RowDefinitions.Add(new RowDefinition());
        canvas.Children.Add(titleBar); Grid.SetRow(body, 1); canvas.Children.Add(body);
        Root.RowDefinitions.Clear(); Root.Padding = new Thickness(24, 15, 24, 24);
        canvas.HorizontalAlignment = HorizontalAlignment.Stretch; canvas.VerticalAlignment = VerticalAlignment.Top; Root.Children.Add(canvas);
        // Flyout uses a compact custom caption row; the gear and X share one baseline.
        if (preview)
        {

            AppWindow.ResizeClient(new SizeInt32((int)(520 * scale), (int)(520 * scale)));
            Root.Background = new SolidColorBrush(Colors.Transparent);
        }
        // Paint the entire borderless client, including pixels allocated by resizing.
        Root.Background=new SolidColorBrush(Colors.Transparent);
        ApplyTheme();

        tray = new TrayIcon(hwnd, TrayIconPath(), TrayItems(), () => ShowFlyout(null, true), OnTrayMenu);
        tray.IsDark=()=>Dark;
        tray.OnSystemThemeChanged=()=>tray.SetIcon(TrayIconPath());
        tray.NativeTip = true; tray.SetTip("Fluent Vantage Toolbar");
        AppWindow.Closing += (_, e) => { if (!exiting && !preview) { e.Cancel = true; HideFlyout(); } };
        Closed += (_, _) => { updateLifetime.Cancel();try{updateToast?.Close();}catch{}contextMenu?.Close();timer.Stop(); tray.Dispose(); };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && !preview && !dialogOpen && !pageNavigating && !showingFlyout && !reordering) HideFlyout(); };
        Root.ActualThemeChanged += (_, _) => { if (!preview) Render(); };
        timer.Tick += async (_, _) => { if (flyoutVisible && view == "main") await Refresh(); };
        Root.SizeChanged += (_, _) => DiagnosticLog.Write($"Layout dpi={GetDpiForWindow(hwnd)} outer={AppWindow.Size.Width}x{AppWindow.Size.Height} client={AppWindow.ClientSize.Width}x{AppWindow.ClientSize.Height} root={Root.ActualWidth}x{Root.ActualHeight} canvas={canvas.ActualWidth}x{canvas.ActualHeight}");
        Root.Loaded += async (_, _) => { if (preview) await Capture(); };

        Render();
        if (!preview)
        {
            timer.Start();
            _ = AutomaticUpdatesAsync(updateLifetime.Token);
            _ = Refresh();
            ListenForShowRequests();
            ListenForExitRequests();
            AppWindow.Show(false);
        }
    }

    // ---- window behaviour ----
    void HideFromTaskbar()
    {
        try
        {
            AppWindow.IsShownInSwitchers = false;
            long style = GetWindowLongPtr(hwnd, -20).ToInt64();
            style = (style | 0x80L) & ~0x40000L; // WS_EX_TOOLWINDOW on, WS_EX_APPWINDOW off
            SetWindowLongPtr(hwnd, -20, new IntPtr(style));
        }
        catch { }
    }

    void SetCloaked(bool cloak)
    {
        uint value=cloak?1u:0u;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd,13,ref value,4));
        if(DwmGetWindowAttribute(hwnd,14,out uint actual,4)>=0 && ((actual&1)!=0)!=cloak)
            throw new InvalidOperationException("DWM cloak state differs from requested state");
    }
    async void HideFlyout()
    {
        if(!flyoutVisible && !showingFlyout)return;
        int generation=++animationGeneration;lastHidden=DateTime.UtcNow;
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
    async Task VerifyPointerReorderAsync(string dir)
    {
        reorderMode=true;await NavigateAsync("settings");Render();await geometryReady;
        if(body.Content is ScrollViewer scroll && reorderRows is {} rows) {
            Activate();SetForegroundWindow(hwnd);Root.UpdateLayout();await Task.Delay(150);
            double offset=rows.TransformToVisual((UIElement)scroll.Content).TransformPoint(new Windows.Foundation.Point()).Y;
            scroll.ChangeView(null,Math.Max(0,offset-80),null,true);await Task.Delay(200);
            for(int pass=0;pass<2;pass++) {
                var from=(FrameworkElement)rows.Children[pass==0?0:2];var to=(FrameworkElement)rows.Children[pass==0?2:0];
                string id=(string)from.Tag;
                var h=(FrameworkElement)((Grid)from).Children[0];
                var start=h.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(h.ActualWidth/2,h.ActualHeight/2));
                var end=to.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(h.ActualWidth/2,to.ActualHeight/2+(pass==0?12:-12)));
                if(!GetWindowRect(hwnd,out var rect))throw new InvalidOperationException("Drag bounds unavailable");
                double scale=GetDpiForWindow(hwnd)/96d;
                var origin=new NativePoint();ClientToScreen(hwnd,ref origin);rect.Left=origin.X;rect.Top=origin.Y;
                if(Root.FlowDirection==FlowDirection.RightToLeft){start.X=Root.ActualWidth-start.X;end.X=Root.ActualWidth-end.X;}
                SetCursorPos(rect.Left+(int)(start.X*scale),rect.Top+(int)(start.Y*scale));mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(120);
                for(int step=1;step<=12;step++) {mouse_event(0x8001,(uint)((rect.Left+(start.X+(end.X-start.X)*step/12)*scale)*65535/(GetSystemMetrics(0)-1)),(uint)((rect.Top+(start.Y+(end.Y-start.Y)*step/12)*scale)*65535/(GetSystemMetrics(1)-1)),0,UIntPtr.Zero);await Task.Delay(25);}
                mouse_event(4,0,0,0,UIntPtr.Zero);await Task.Delay(180);
                await SaveImage(Path.Combine(dir,$"reorder-attempt-{pass}.png"));
                File.AppendAllText(Path.Combine(dir,"reorder-input.txt"),$"pass={pass}; start={start}; end={end}; rect={rect.Left},{rect.Top}; pressed={reorderPresses}; moved={reorderMoves}; released={reorderReleases}; order={string.Join(",",settings.TileOrder)}; trace={reorderTrace}
");
                if(settings.TileOrder.Count!=Tiles.Length || settings.TileOrder[pass==0?2:0]!=id){Close();throw new InvalidOperationException("Actual pointer reorder did not move expected row");}
                await SaveImage(Path.Combine(dir,$"reorder-pointer-{pass}.png"));
            }
            string saved=System.Text.Json.JsonSerializer.Serialize(settings);var loaded=System.Text.Json.JsonSerializer.Deserialize<AppSettings>(saved)!;
            if(!loaded.TileOrder.SequenceEqual(settings.TileOrder))throw new InvalidOperationException("Order serialization failed");
            File.WriteAllText(Path.Combine(dir,"reorder-pointer.txt"),$"PASS actual mouse press/move/release twice; administrator={Startup.IsAdmin()}; order={string.Join(",",settings.TileOrder)}; serialization roundtrip passed\n");
        } else throw new InvalidOperationException("Reorder rows missing");
        reorderMode=false;await NavigateAsync("main");
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
            flyoutMotion?.Stop();canvas.Opacity=MotionEnabled?0:1;canvas.RenderTransform=new TranslateTransform{Y=MotionEnabled?42:0};
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
        } finally {showingFlyout=false;}
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

    void PositionTitleBar()
    {
        titleBar.Margin = new Thickness(0); return;
#pragma warning disable CS0162
        try
        {
            double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96d);
            double right = AppWindow.TitleBar.RightInset / scale, left = AppWindow.TitleBar.LeftInset / scale;
            // Keep the title row clear of the native close button; Root already adds 24 of padding.
            titleBar.Margin = L.Ar ? new Thickness(Math.Max(0, left - 24), 0, 0, 0) : new Thickness(0, 0, Math.Max(0, right - 24), 0);
        }
        catch { }
    }

#pragma warning restore CS0162
    void ApplyTheme()
    {
        ApplyAccent();
        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        Root.FlowDirection = L.Ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    static Windows.UI.Color? ParseAccent(string? hex)
    {
        if(hex!=null && hex.Length==7 && hex[0]=='#') {
            var digits=hex.Substring(1,hex.Length-1);
            if(uint.TryParse(digits,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint rgb))return Windows.UI.Color.FromArgb(255,(byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb);
        }
        return null;
    }
    void ApplyAccent()
    {
        var resources=Application.Current.Resources;
        string[] names={"SystemAccentColor","SystemAccentColorLight1","SystemAccentColorLight2","SystemAccentColorLight3","SystemAccentColorDark1","SystemAccentColorDark2","SystemAccentColorDark3"};
        var color=ParseAccent(settings.AccentColor);
        foreach(string name in names){if(color.HasValue)resources[name]=color.Value;else resources.Remove(name);}
        string[] brushes={"SystemControlHighlightAccentBrush","AccentFillColorDefaultBrush","AccentFillColorSecondaryBrush","AccentFillColorTertiaryBrush","HyperlinkForeground","ToggleSwitchFillOn"};
        foreach(string name in brushes){if(color.HasValue)resources[name]=new SolidColorBrush(color.Value);else resources.Remove(name);}
    }
    UIElement AccentPicker()
    {
        var stack=new StackPanel { Spacing=10 };
        stack.Children.Add(Text(L.Ar ? "لون التطبيق" : "Accent color",14,true));
        var swatches=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8 };
        foreach(string hex in new[]{"#0078D4","#00A6A6","#9CDA9B","#744DA9","#D83B01","#C239B3","#107C10"}) {
            var button=new Button { Width=40,Height=40,Padding=new Thickness(3),Background=new SolidColorBrush(ParseAccent(hex)!.Value),
                BorderBrush=Primary,BorderThickness=new Thickness(settings.AccentColor==hex?3:0),Content=settings.AccentColor==hex ? "✓" : "",Foreground=new SolidColorBrush(Colors.White) };
            ToolTipService.SetToolTip(button,hex);
            button.Click+=(_,_)=>{settings.AccentColor=hex;settings.Save();dashboard=null;Render();};swatches.Children.Add(button);
        }
        stack.Children.Add(swatches);
        var actions=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8 };
        var custom=new Button { Content=L.Ar ? "لون مخصص" : "Custom color" };
        custom.Click+=async(_,_)=>{
            var picker=new ColorPicker { IsAlphaEnabled=false,IsColorSpectrumVisible=true,IsColorSliderVisible=true,IsHexInputVisible=true,
                Color=ParseAccent(settings.AccentColor) ?? new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent),MaxWidth=380 };
            dialogOpen=true;
            try {
                var dialog=new ContentDialog { XamlRoot=Root.XamlRoot,Title=L.Ar ? "اختر لون التطبيق" : "Choose accent color",Content=picker,
                    PrimaryButtonText=L.Ar ? "تطبيق" : "Apply",CloseButtonText=L.Ar ? "إلغاء" : "Cancel",DefaultButton=ContentDialogButton.Primary };
                if(await dialog.ShowAsync()==ContentDialogResult.Primary){settings.AccentColor=$"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";settings.Save();dashboard=null;Render();}
            } finally {dialogOpen=false;}
        };
        var reset=new Button { Content=L.Ar ? "استعادة الافتراضي" : "Restore default" };
        reset.Click+=(_,_)=>{settings.AccentColor=AppSettings.DefaultAccentColor;settings.Save();dashboard=null;Render();};
        actions.Children.Add(custom);actions.Children.Add(reset);stack.Children.Add(actions);
        stack.Children.Add(Text(settings.AccentColor ?? AppSettings.DefaultAccentColor,12,false,SecondaryText));
        return Card(stack,new Thickness(14));
    }

    // ---- device ----
    async Task Refresh()
    {
        if (busy || preview) return;
        busy = true;
        try { current = await device.ReadAsync(); Apply(); }
        catch (Exception e) { if (statusText != null) statusText.Text = e.Message; }
        finally { busy = false; }
    }

    async Task Run(Func<Task> action)
    {
        if (busy || preview || current == null) return;
        busy = true;
        foreach (var button in tileButtons.Values) button.IsEnabled = false;
        try { await action(); current = await device.ReadAsync(); Apply(); }
        catch (Exception e) { DiagnosticLog.Write("Control failed: " + e); current = await device.ReadAsync(); Apply(); dialogOpen = true; try { await new ContentDialog { XamlRoot = Root.XamlRoot, Title = L.T("notconfirmed"), Content = (L.Ar ? "لم يتأكد تغيير الإعداد. أعد المحاولة، وإذا استمرت المشكلة افتح سجل التشخيص من الإعدادات وأرسله للدعم." : "The setting change could not be confirmed. Try again. If it continues, open the diagnostic log in Settings and send it to support."), CloseButtonText = "OK" }.ShowAsync(); } finally { dialogOpen = false; } }
        finally { busy = false; }
    }

    static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    async Task ConfirmTouchpadAsync(bool lockNow)
    {
        if (lockNow)
        {
            dialogOpen = true;
            try
            {
                var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L.T("dlg.touchpad.title"), Content = L.T("dlg.touchpad.body"), PrimaryButtonText = L.T("dlg.lock"), CloseButtonText = L.T("dlg.cancel"), DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) { await Refresh(); return; }
            }
            finally { dialogOpen = false; }
        }
        await Run(() => device.SetTouchpadAsync(lockNow));
    }

    async void OnTile(string id)
    {
        DiagnosticLog.Write($"Tile clicked: {id}, busy={busy}, stateLoaded={current != null}");
        if (busy || current == null) { Apply(); return; }
        var c = current;
        switch (id)
        {
            case "thermal": await Run(() => device.CycleThermalAsync()); break;
            case "fn": await Run(() => device.SetFnAsync(!(c?.FnLocked ?? false))); break;
            case "usb": await Run(() => device.SetUsbAsync(c?.UsbMode != 2)); break;
            case "refresh":
                var rates = c?.AvailableHz?.OrderBy(x => x).ToArray();
                if (rates is { Length: >= 2 }) await Run(() => device.SetRefreshAsync(c?.RefreshHz == rates[^1] ? rates[0] : rates[^1]));
                break;
            case "mic": await Run(() => device.SetMicrophoneAsync(!(c?.Muted ?? false))); break;
            case "conserve": await Run(() => device.SetModeAsync(c?.Mode == ChargeMode.Conservation ? ChargeMode.Normal : ChargeMode.Conservation)); break;
            case "rapid": await Run(() => device.SetModeAsync(c?.Mode == ChargeMode.Rapid ? ChargeMode.Normal : ChargeMode.Rapid)); break;
            case "touchpad": await ConfirmTouchpadAsync(c?.TouchpadLocked == false); break;
        }
    }

    // ---- colours ----
    bool Dark => Root.ActualTheme == ElementTheme.Dark;
    static SolidColorBrush Rgb(byte r, byte g, byte b, byte a = 255) => new(Windows.UI.Color.FromArgb(a, r, g, b));
    Brush SecondaryText => Dark ? Rgb(255, 255, 255, 0xC5) : Rgb(0, 0, 0, 0x9E);
    Brush CardBackground => Dark ? Rgb(255, 255, 255, 0x0D) : Rgb(255, 255, 255, 0xB3);
    Brush CardStroke => Dark ? Rgb(255, 255, 255, 0x29) : Rgb(0, 0, 0, 0x0F);
    Brush Primary => Dark ? Rgb(255, 255, 255) : Rgb(0, 0, 0, 0xE4);
    Windows.UI.Color BatteryColorFor(int? percent) => percent switch
    {
        null => Windows.UI.Color.FromArgb(255, 156, 218, 155),
        <= 5 => Windows.UI.Color.FromArgb(255, 255, 153, 164),   // Windows 11 critical
        <= 20 => Windows.UI.Color.FromArgb(255, 252, 225, 0),    // Windows 11 caution
        _ => Windows.UI.Color.FromArgb(255, 156, 218, 155), // battery keeps its original color, independent of the UI accent
    };

    // ---- view building ----
    static FontIcon Glyph(string glyph, double size) => new() { Glyph = glyph, FontSize = size, FontFamily = new FontFamily("Segoe Fluent Icons") };

    TextBlock Text(string text, double size = 14, bool bold = false, Brush? brush = null) =>
        new() { Text = text, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = brush ?? Primary, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    Border Card(UIElement child, Thickness padding) =>
        new() { Child = child, Padding = padding, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = CardStroke, Background = CardBackground };

    Button IconButton(string glyph, string tip, Action click)
    {
        var icon = Glyph(glyph, 18); icon.MirroredWhenRightToLeft = glyph == "\uE72B";
        var button = new Button { Content = icon, Width = 36, Height = 36, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    bool pageNavigating;
    bool MotionEnabled => preview ? args.Contains("--interactive") : new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    async void Navigate(string target) => await NavigateAsync(target);
    async Task NavigateAsync(string target)
    {
        if(pageNavigating || target==view)return;
        pageNavigating=true;
        try {
            await geometryReady;view=target;Render();await geometryReady;
            if(MotionEnabled && flyoutVisible) {
                var slide=new TranslateTransform{Y=24};
                body.RenderTransform=slide;
                var motion=new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var done=new TaskCompletionSource<bool>();
                motion.Completed+=(_,_)=>done.TrySetResult(true);
                void Add(DependencyObject element,string property,double from,double to) {
                    var a=new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation{From=from,To=to,Duration=new Duration(TimeSpan.FromMilliseconds(200)),EnableDependentAnimation=true,EasingFunction=new Microsoft.UI.Xaml.Media.Animation.CubicEase{EasingMode=Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut}};
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a,element);Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a,property);motion.Children.Add(a);
                }
                Add(slide,"Y",24,0);
                Add(body,"Opacity",0,1);
                motion.Begin();
                await done.Task;
                motion.Stop();
                body.Opacity=1;
                body.RenderTransform=new TranslateTransform();
            }
            if(target=="battery" && !preview)_ = LoadBatteryAsync();
            if(target=="warranty" && !preview)_ = LoadWarrantyAsync(false);
        } finally {pageNavigating=false;}
    }

    void Render()
    {
        canvas.Margin=new Thickness(0);
        L.Set(settings.Language);
        ApplyTheme();
        restoreAfterGeometry=false;
        if (tray != null) tray.SetItems(TrayItems());
        titleBar.Children.Clear(); titleBar.ColumnDefinitions.Clear();
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string titleText = view switch { "settings" => L.T("settings.title"), "about" => L.T("about.title"), "battery" => L.T("bat.title"), "warranty" => L.T("war.title"), "device" => L.Ar ? "عن جهازك" : "About your device", _ => L.T("title") };
        if (view != "main")
        {
            var back = IconButton("\uE72B", L.T("back"), () => Navigate("main"));
            titleBar.Children.Add(back);
        }
        var title = Text(titleText, 20, true);
        title.Margin = new Thickness(view == "main" ? 0 : 4, 0, 0, 0);
        Grid.SetColumn(title, 1); titleBar.Children.Add(title);
        if (view == "main")
        {
            var gear = IconButton("\uE713", L.T("settings.gear"), () => Navigate("settings"));
            Grid.SetColumn(gear, 2); titleBar.Children.Add(gear);
        }
        var close = IconButton("\uE8BB", L.Ar ? "إغلاق النافذة" : "Close window", HideFlyout);
        Grid.SetColumn(close, 3); titleBar.Children.Add(close);
        PositionTitleBar();
        tileButtons.Clear();updateButtons.Clear();updateResults.Clear();updateLinks.Clear();
        body.Content = view switch { "settings" => BuildSettings(), "about" => BuildAbout(), "battery" => BuildBattery(), "warranty" => BuildWarranty(), "device" => dashboard ??= new DeviceDashboardWindow(settings.Theme,current,preview), _ => BuildMain() };
        if (view == "main") Apply();
        ResizeForContent();
    }

    bool restoreAfterGeometry;
    Task geometryReady=Task.CompletedTask;
    readonly SemaphoreSlim geometryLock=new(1,1);
    int geometryGeneration;
    int nativeResizeCount;
    double frameWidthPixels=double.NaN,frameHeightPixels=double.NaN;

    void ResizeForContent() => geometryReady=FitGeometryAsync(++geometryGeneration);

    static Task NextFrameAsync()
    {
        var done=new TaskCompletionSource<bool>();
        EventHandler<object>? handler=null;
        handler=(_,_)=>{CompositionTarget.Rendering-=handler;done.TrySetResult(true);};
        CompositionTarget.Rendering+=handler;
        return WaitFrameAsync(done.Task,()=>CompositionTarget.Rendering-=handler);
    }
    static async Task WaitFrameAsync(Task frame,Action cleanup)
    {
        // Hidden windows can stop compositor ticks. Do not leave geometry blocked.
        await Task.WhenAny(frame,Task.Delay(34));cleanup();
    }

    async Task FitGeometryAsync(int generation)
    {
        await geometryLock.WaitAsync();
        try {
            if(generation!=geometryGeneration)return;
            // Finish templates and arrange natural content before touching the HWND.
            await NextFrameAsync();
            if(generation!=geometryGeneration)return;
            Root.UpdateLayout();
            double scale=Math.Max(1,GetDpiForWindow(hwnd)/96d);
            {
                // Window chrome is outer size minus client size. Never derive it from Root.ActualWidth:
                // at logon the cloaked window may not be laid out yet (ActualWidth 0), which made the "frame"
                // equal to the whole window and doubled the size to full screen.
                var outer=AppWindow.Size;var client=AppWindow.ClientSize;
                double fw=outer.Width-client.Width,fh=outer.Height-client.Height;
                double limit=48*scale;
                frameWidthPixels=(fw>=0&&fw<=limit)?fw:0;
                frameHeightPixels=(fh>=0&&fh<=limit)?fh:0;
            }
            canvas.Height=double.NaN;
            canvas.RowDefinitions[1].Height=view=="main"?GridLength.Auto:new GridLength(1,GridUnitType.Star);
            canvas.Measure(new Windows.Foundation.Size(472,double.PositiveInfinity));
            double naturalHeight=view=="main"?canvas.DesiredSize.Height:520+Math.Max(36,titleBar.DesiredSize.Height);
            canvas.Arrange(new Windows.Foundation.Rect(24,15,472,naturalHeight));
            double wanted=naturalHeight+Root.Padding.Top+Root.Padding.Bottom;
            if(view=="main" && body.Content is StackPanel main) {
                var last=(FrameworkElement)main.Children.Last();
                double ink=await PaintedHeightAsync(last);
                wanted=last.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0,ink)).Y-canvas.Margin.Top+24;
            }
            if(generation!=geometryGeneration)return;
            // At logon the cloaked window may not be laid out yet, so the painted-ink measure can come back near zero.
            // Never let that produce a tiny (invisible) window: fall back to the measured natural height, with a floor.
            if(!preview) {
                double natural=naturalHeight+Root.Padding.Top+Root.Padding.Bottom;
                if(double.IsNaN(wanted)||double.IsInfinity(wanted)||wanted<natural*0.6)wanted=natural;
                wanted=Math.Max(wanted,200);
            }
            var work=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest).WorkArea;
            wanted=Math.Min(wanted,work.Height/scale-16);
            var desired=new SizeInt32((int)Math.Round(520*scale+frameWidthPixels),(int)Math.Round(wanted*scale+frameHeightPixels));
            desired=new SizeInt32(Math.Min(desired.Width,(int)Math.Round(520*scale+48*scale)),Math.Min(desired.Height,work.Height));
            {
                if(AppWindow.Size.Width!=desired.Width || AppWindow.Size.Height!=desired.Height) {
                    AppWindow.Resize(desired);nativeResizeCount++;
                }
                Root.UpdateLayout();await NextFrameAsync();Root.UpdateLayout();await NextFrameAsync();
                if(!preview && tray!=null && tray.TryGetAnchor(out int x,out int y)) {
                    var area=DisplayArea.GetFromPoint(new PointInt32(x,y),DisplayAreaFallback.Nearest).WorkArea;
                    int left=Math.Clamp(x-AppWindow.Size.Width/2,area.X,Math.Max(area.X,area.X+area.Width-AppWindow.Size.Width));
                    int top=Math.Clamp(y-AppWindow.Size.Height-16,area.Y,Math.Max(area.Y,area.Y+area.Height-AppWindow.Size.Height-12));
                    if(AppWindow.Position.X!=left || AppWindow.Position.Y!=top)AppWindow.Move(new PointInt32(left,top));
                }
            }
            if(preview && args.Contains("--interactive")) {
                if(!GetWindowRect(hwnd,out var actual) || actual.Right-actual.Left!=desired.Width || actual.Bottom-actual.Top!=desired.Height)throw new InvalidOperationException("Native bounds differ from settled content size");
                var region=CreateRectRgn(0,0,0,0);int kind=GetWindowRgn(hwnd,region);DeleteObject(region);
                if(kind!=0 || canvas.Margin.Top!=0)throw new InvalidOperationException("Retained region or canvas offset");
                var dir=Path.GetDirectoryName(args[Array.IndexOf(args,"--capture")+1])!;
                File.AppendAllText(Path.Combine(dir,"navigation-native-bounds.csv"),$"{view},{actual.Left},{actual.Top},{actual.Right},{actual.Bottom},region={kind},offset={canvas.Margin.Top},dpi={GetDpiForWindow(hwnd)}\n");
            }
        } finally {geometryLock.Release();}
    }

    async Task<double> PaintedHeightAsync(FrameworkElement element)
    {
        var bitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer=await bitmap.GetPixelsAsync();
        using var reader=Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        var pixels=new byte[buffer.Length];reader.ReadBytes(pixels);
        for(int y=bitmap.PixelHeight-1;y>=0;y--)for(int x=0;x<bitmap.PixelWidth;x++)
            if(pixels[(y*bitmap.PixelWidth+x)*4+3]>16)
                return (y+1)*element.ActualHeight/bitmap.PixelHeight;
        return element.ActualHeight;
    }

    UIElement HeaderRow(string header, string? linkText, Action? click)
    {
        var grid = new Grid();
        grid.Children.Add(Text(header, 14, true));
        if (linkText != null && click != null)
        {
            var link = new HyperlinkButton { Content = linkText, HorizontalAlignment = HorizontalAlignment.Right };
            link.Click += (_, _) => click();
            grid.Children.Add(link);
        }
        return grid;
    }

    UIElement BuildMain()
    {
        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(HeaderRow(L.T("battery.header"), settings.ShowBatteryDetails ? L.T("battery.link") : null, () => Navigate("battery")));

        // Battery graphic: always laid out left to right so the fill grows from the left in both languages.
        var graphic = new Grid { Height = 100, Margin = new Thickness(12, 0, 12, 0), FlowDirection = FlowDirection.LeftToRight };
        graphic.ColumnDefinitions.Add(new ColumnDefinition());
        graphic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        var outline = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), BorderBrush = SecondaryText, Padding = new Thickness(5) };
        var inner = new Grid();
        batteryFill = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        inner.Children.Add(batteryFill);
        plugIcon = Glyph("\uE945", 28);
        percentText = Text("--%", 44, true);
        percentText.TextWrapping = TextWrapping.NoWrap;
        var centre = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        centre.Children.Add(plugIcon); centre.Children.Add(percentText);
        inner.Children.Add(centre);
        inner.SizeChanged += (_, _) => UpdateBatteryFill();
        outline.Child = inner;
        graphic.Children.Add(outline);
        var cap = new Border { Background = SecondaryText, Height = 30, CornerRadius = new CornerRadius(0, 3, 3, 0) };
        Grid.SetColumn(cap, 1); graphic.Children.Add(cap);
        batteryFillHost = inner;
        stack.Children.Add(graphic);
        chargeText = Text("", 12, false, SecondaryText);
        stack.Children.Add(chargeText);

        stack.Children.Add(HeaderRow(L.T("quick.header"), L.Ar ? "عن جهازك" : "About your device", OpenDashboard));
        var visible = OrderedTiles().Where(t => settings.IsTileVisible(t.Id)).ToList();
        var rows = new StackPanel { Spacing = 14 };
        for (int i = 0; i < visible.Count; i += 5)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var tile in visible.Skip(i).Take(5)) row.Children.Add(MakeTile(tile));
            rows.Children.Add(row);
        }
        if (visible.Count > 0) stack.Children.Add(Card(rows, new Thickness(8, 16, 8, 16)));

        statusText = Text("", 12, false, SecondaryText);
        statusText.MaxHeight = 60;
        // Availability details belong in Settings, not the main flyout.
        if (settings.ShowWarranty)
        {
            var warrantyLink = new HyperlinkButton { Content = L.T("warranty.link"), HorizontalAlignment = HorizontalAlignment.Right, Padding=new Thickness(0), MinHeight=0, VerticalAlignment=VerticalAlignment.Top };
            warrantyLink.Click += (_, _) => Navigate("warranty");
            stack.Children.Add(warrantyLink);
        }
        return stack;
    }

    FrameworkElement MakeTile(TileDef tile)
    {
        var button = new ToggleButton { Width = 56, Height = 56, CornerRadius = new CornerRadius(28), HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = false, Padding = new Thickness(0) };
        var accent = ParseAccent(settings.AccentColor) ?? Windows.UI.Color.FromArgb(255,156,218,155);
        // Preserve the existing default state's tint steps, translated to the current accent.
        Windows.UI.Color StateTint(int r,int g,int b) => Windows.UI.Color.FromArgb(255,
            (byte)Math.Clamp(accent.R+r,0,255),(byte)Math.Clamp(accent.G+g,0,255),(byte)Math.Clamp(accent.B+b,0,255));
        button.Resources["ToggleButtonBackgroundChecked"] = new SolidColorBrush(accent);
        button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = new SolidColorBrush(StateTint(-16,-13,-16));
        button.Resources["ToggleButtonBackgroundCheckedPressed"] = new SolidColorBrush(StateTint(-33,-28,-32));
        button.Resources["ToggleButtonForegroundChecked"] = Rgb(20, 55, 27);
        button.Resources["ToggleButtonForegroundCheckedPointerOver"] = Rgb(20, 55, 27);
        button.Resources["ToggleButtonForegroundCheckedPressed"] = Rgb(20, 55, 27);
        if (tile.Id == "fn") button.Content = MakeFnIcon();
        else if(tile.Id=="touchpad")button.Content=TouchpadIcon(false,Primary);
        else button.Content = Glyph(tile.Glyph, 24);
        button.Click += (_, _) => OnTile(tile.Id);
        tileButtons[tile.Id] = button;
        var label = Text(tile.Id=="thermal" ? ThermalLabel(current?.Thermal) : L.T(tile.LabelKey), 10.5);
        label.TextAlignment = TextAlignment.Center; label.HorizontalAlignment = HorizontalAlignment.Center; label.MaxLines = 2;
        var panel = new StackPanel { Width = 86, Spacing = 8 };
        panel.Children.Add(button); panel.Children.Add(label);
        return panel;
    }

    UIElement MicOffIcon(Brush ink)
    {
        var icon=(Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='24' Height='24' Stretch='Uniform' Data='M3.28034 2.21968C2.98745 1.92678 2.51257 1.92677 2.21968 2.21966C1.92678 2.51255 1.92677 2.98743 2.21966 3.28032L8 9.06078V12C8 14.2091 9.79086 16 12 16C12.8335 16 13.6074 15.7451 14.2481 15.309L15.394 16.4549C14.5176 17.1112 13.4292 17.5 12.25 17.5H11.75L11.5336 17.4956C8.73445 17.3821 6.5 15.077 6.5 12.25V11.75L6.49315 11.6482C6.44349 11.2822 6.1297 11 5.75 11C5.33579 11 5 11.3358 5 11.75V12.25L5.00406 12.4863C5.12283 15.938 7.83323 18.7316 11.25 18.9818L11.25 21.25L11.2568 21.3518C11.3065 21.7178 11.6203 22 12 22C12.4142 22 12.75 21.6642 12.75 21.25L12.751 18.9817C14.15 18.8791 15.4305 18.35 16.4631 17.5241L20.7194 21.7805C21.0123 22.0734 21.4872 22.0734 21.7801 21.7805C22.073 21.4876 22.073 21.0127 21.7801 20.7198L3.28034 2.21968ZM13.1562 14.2171C12.8105 14.3978 12.4172 14.5 12 14.5C10.6193 14.5 9.5 13.3807 9.5 12V10.5608L13.1562 14.2171ZM14.5 6V11.3182L15.9301 12.7483C15.976 12.5059 16 12.2558 16 12V6C16 3.79086 14.2091 2 12 2C10.1521 2 8.59692 3.25302 8.13768 4.95575L9.5 6.3181V6C9.5 4.61929 10.6193 3.5 12 3.5C13.3807 3.5 14.5 4.61929 14.5 6ZM17.1962 14.0144L18.3421 15.1604C18.7638 14.2791 19 13.2921 19 12.25V11.75L18.9932 11.6482C18.9435 11.2822 18.6297 11 18.25 11C17.8358 11 17.5 11.3358 17.5 11.75V12.25L17.4956 12.4664C17.4737 13.0075 17.3698 13.5276 17.1962 14.0144Z' />");
        icon.Fill=ink;return icon;
    }
    UIElement TouchpadIcon(bool off,Brush ink)
    {
        var grid=new Grid { Width=26,Height=26,FlowDirection=FlowDirection.LeftToRight };
        var icon=Glyph("\uEFA5",24);icon.Foreground=ink;grid.Children.Add(icon);
        if(off)grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1=3,Y1=3,X2=23,Y2=23,Stroke=ink,StrokeThickness=2,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round });
        return grid;
    }

    string ThermalLabel(ThermalMode? mode) => mode switch { ThermalMode.Quiet=>L.Ar ? "هادئ" : "Quiet",ThermalMode.Balanced=>L.Ar ? "متوازن" : "Balanced",ThermalMode.Performance=>L.Ar ? "أداء" : "Performance",_=>L.Ar ? "غير متاح" : "Unavailable" };
    UIElement ThermalIcon(ThermalMode? mode)
    {
        Brush ink=mode switch { ThermalMode.Quiet=>Rgb(53,123,242),ThermalMode.Performance=>Rgb(212,51,51),_=>Primary };
        // Vector mask of the owner-supplied square-box mode icon references.
        string data=mode switch {
            ThermalMode.Quiet=>"M43,10 H59 V11 H43 Z M39,11 H63 V12 H39 Z M36,12 H66 V13 H36 Z M34,13 H46 V14 H34 Z M56,13 H68 V14 H56 Z M32,14 H41 V15 H32 Z M61,14 H70 V15 H61 Z M30,15 H39 V16 H30 Z M64,15 H72 V16 H64 Z M28,16 H40 V17 H28 Z M67,16 H74 V17 H67 Z M27,17 H33 V18 H27 Z M35,17 H42 V18 H35 Z M69,17 H75 V18 H69 Z M25,18 H31 V19 H25 Z M37,18 H43 V19 H37 Z M71,18 H77 V19 H71 Z M24,19 H30 V20 H24 Z M39,19 H43 V20 H39 Z M72,19 H78 V20 H72 Z M23,20 H28 V21 H23 Z M40,20 H44 V21 H40 Z M74,20 H79 V21 H74 Z M22,21 H27 V22 H22 Z M41,21 H44 V22 H41 Z M75,21 H80 V22 H75 Z M21,22 H26 V23 H21 Z M41,22 H45 V23 H41 Z M76,22 H81 V23 H76 Z M20,23 H25 V24 H20 Z M41,23 H45 V24 H41 Z M77,23 H82 V24 H77 Z M19,24 H24 V25 H19 Z M42,24 H45 V25 H42 Z M78,24 H83 V25 H78 Z M18,25 H23 V26 H18 Z M42,25 H45 V26 H42 Z M79,25 H84 V26 H79 Z M17,26 H22 V27 H17 Z M42,26 H45 V27 H42 Z M80,26 H85 V27 H80 Z M17,27 H21 V28 H17 Z M42,27 H45 V28 H42 Z M81,27 H85 V28 H81 Z M16,28 H20 V29 H16 Z M42,28 H46 V29 H42 Z M82,28 H86 V29 H82 Z M15,29 H19 V30 H15 Z M42,29 H46 V30 H42 Z M83,29 H87 V30 H83 Z M15,30 H19 V31 H15 Z M42,30 H46 V31 H42 Z M83,30 H87 V31 H83 Z M14,31 H18 V32 H14 Z M42,31 H46 V32 H42 Z M84,31 H88 V32 H84 Z M14,32 H17 V33 H14 Z M42,32 H46 V33 H42 Z M85,32 H88 V33 H85 Z M13,33 H17 V34 H13 Z M43,33 H46 V34 H43 Z M85,33 H89 V34 H85 Z M13,34 H16 V35 H13 Z M43,34 H46 V35 H43 Z M86,34 H89 V35 H86 Z M12,35 H16 V36 H12 Z M43,35 H46 V36 H43 Z M86,35 H90 V36 H86 Z M12,36 H15 V37 H12 Z M43,36 H47 V37 H43 Z M87,36 H90 V37 H87 Z M11,37 H15 V38 H11 Z M43,37 H47 V38 H43 Z M87,37 H91 V38 H87 Z M11,38 H15 V39 H11 Z M44,38 H48 V39 H44 Z M87,38 H91 V39 H87 Z M11,39 H14 V40 H11 Z M44,39 H48 V40 H44 Z M88,39 H91 V40 H88 Z M10,40 H14 V41 H10 Z M45,40 H49 V41 H45 Z M88,40 H92 V41 H88 Z M10,41 H14 V42 H10 Z M45,41 H50 V42 H45 Z M88,41 H92 V42 H88 Z M10,42 H13 V43 H10 Z M46,42 H50 V43 H46 Z M89,42 H92 V43 H89 Z M10,43 H13 V44 H10 Z M47,43 H51 V44 H47 Z M89,43 H92 V44 H89 Z M9,44 H13 V45 H9 Z M48,44 H52 V45 H48 Z M89,44 H93 V45 H89 Z M9,45 H13 V46 H9 Z M49,45 H53 V46 H49 Z M89,45 H93 V46 H89 Z M9,46 H13 V47 H9 Z M50,46 H54 V47 H50 Z M89,46 H93 V47 H89 Z M9,47 H12 V48 H9 Z M51,47 H54 V48 H51 Z M59,47 H64 V48 H59 Z M90,47 H93 V48 H90 Z M9,48 H12 V49 H9 Z M51,48 H55 V49 H51 Z M59,48 H64 V49 H59 Z M90,48 H93 V49 H90 Z M9,49 H12 V50 H9 Z M49,49 H54 V50 H49 Z M58,49 H65 V50 H58 Z M90,49 H93 V50 H90 Z M9,50 H12 V51 H9 Z M47,50 H54 V51 H47 Z M58,50 H65 V51 H58 Z M90,50 H93 V51 H90 Z M9,51 H12 V52 H9 Z M46,51 H53 V52 H46 Z M58,51 H65 V52 H58 Z M90,51 H93 V52 H90 Z M9,52 H12 V53 H9 Z M46,52 H50 V53 H46 Z M58,52 H65 V53 H58 Z M90,52 H93 V53 H90 Z M9,53 H12 V54 H9 Z M45,53 H49 V54 H45 Z M58,53 H65 V54 H58 Z M90,53 H93 V54 H90 Z M9,54 H12 V55 H9 Z M45,54 H48 V55 H45 Z M57,54 H64 V55 H57 Z M90,54 H93 V55 H90 Z M9,55 H12 V56 H9 Z M45,55 H49 V56 H45 Z M57,55 H64 V56 H57 Z M90,55 H93 V56 H90 Z M9,56 H12 V57 H9 Z M45,56 H49 V57 H45 Z M57,56 H64 V57 H57 Z M90,56 H93 V57 H90 Z M9,57 H13 V58 H9 Z M46,57 H50 V58 H46 Z M57,57 H64 V58 H57 Z M89,57 H93 V58 H89 Z M9,58 H13 V59 H9 Z M46,58 H51 V59 H46 Z M57,58 H64 V59 H57 Z M89,58 H93 V59 H89 Z M9,59 H13 V60 H9 Z M46,59 H50 V60 H46 Z M57,59 H64 V60 H57 Z M89,59 H93 V60 H89 Z M10,60 H13 V61 H10 Z M46,60 H49 V61 H46 Z M56,60 H64 V61 H56 Z M89,60 H92 V61 H89 Z M10,61 H13 V62 H10 Z M46,61 H49 V62 H46 Z M56,61 H63 V62 H56 Z M89,61 H92 V62 H89 Z M10,62 H14 V63 H10 Z M46,62 H50 V63 H46 Z M56,62 H63 V63 H56 Z M88,62 H92 V63 H88 Z M10,63 H14 V64 H10 Z M45,63 H50 V64 H45 Z M56,63 H63 V64 H56 Z M88,63 H92 V64 H88 Z M11,64 H14 V65 H11 Z M44,64 H50 V65 H44 Z M56,64 H63 V65 H56 Z M88,64 H91 V65 H88 Z M11,65 H15 V66 H11 Z M43,65 H48 V66 H43 Z M56,65 H63 V66 H56 Z M87,65 H91 V66 H87 Z M11,66 H15 V67 H11 Z M43,66 H47 V67 H43 Z M55,66 H63 V67 H55 Z M87,66 H91 V67 H87 Z M12,67 H15 V68 H12 Z M43,67 H46 V68 H43 Z M55,67 H63 V68 H55 Z M87,67 H90 V68 H87 Z M12,68 H16 V69 H12 Z M42,68 H46 V69 H42 Z M55,68 H63 V69 H55 Z M86,68 H90 V69 H86 Z M13,69 H16 V70 H13 Z M42,69 H46 V70 H42 Z M55,69 H62 V70 H55 Z M86,69 H89 V70 H86 Z M13,70 H17 V71 H13 Z M42,70 H46 V71 H42 Z M55,70 H62 V71 H55 Z M64,70 H68 V71 H64 Z M85,70 H89 V71 H85 Z M14,71 H17 V72 H14 Z M42,71 H46 V72 H42 Z M55,71 H62 V72 H55 Z M64,71 H69 V72 H64 Z M85,71 H88 V72 H85 Z M14,72 H18 V73 H14 Z M34,72 H46 V73 H34 Z M54,72 H62 V73 H54 Z M63,72 H69 V73 H63 Z M84,72 H88 V73 H84 Z M15,73 H19 V74 H15 Z M32,73 H45 V74 H32 Z M54,73 H69 V74 H54 Z M83,73 H87 V74 H83 Z M15,74 H19 V75 H15 Z M30,74 H45 V75 H30 Z M54,74 H69 V75 H54 Z M83,74 H87 V75 H83 Z M16,75 H20 V76 H16 Z M29,75 H36 V76 H29 Z M39,75 H43 V76 H39 Z M54,75 H69 V76 H54 Z M82,75 H86 V76 H82 Z M17,76 H21 V77 H17 Z M28,76 H33 V77 H28 Z M54,76 H69 V77 H54 Z M81,76 H85 V77 H81 Z M17,77 H22 V78 H17 Z M27,77 H32 V78 H27 Z M54,77 H69 V78 H54 Z M80,77 H85 V78 H80 Z M18,78 H23 V79 H18 Z M27,78 H31 V79 H27 Z M53,78 H69 V79 H53 Z M79,78 H84 V79 H79 Z M19,79 H24 V80 H19 Z M26,79 H30 V80 H26 Z M53,79 H70 V80 H53 Z M78,79 H83 V80 H78 Z M20,80 H25 V81 H20 Z M26,80 H30 V81 H26 Z M53,80 H72 V81 H53 Z M77,80 H82 V81 H77 Z M21,81 H29 V82 H21 Z M53,81 H73 V82 H53 Z M76,81 H81 V82 H76 Z M22,82 H29 V83 H22 Z M53,82 H80 V83 H53 Z M23,83 H29 V84 H23 Z M53,83 H79 V84 H53 Z M24,84 H30 V85 H24 Z M52,84 H78 V85 H52 Z M25,85 H31 V86 H25 Z M52,85 H77 V86 H52 Z M27,86 H33 V87 H27 Z M52,86 H75 V87 H52 Z M28,87 H35 V88 H28 Z M52,87 H74 V88 H52 Z M30,88 H38 V89 H30 Z M52,88 H72 V89 H52 Z M32,89 H41 V90 H32 Z M52,89 H70 V90 H52 Z M34,90 H46 V91 H34 Z M51,90 H68 V91 H51 Z M36,91 H66 V92 H36 Z M39,92 H63 V93 H39 Z M43,93 H58 V94 H43 Z",
            ThermalMode.Balanced=>"M40,7 H54 V8 H40 Z M35,8 H59 V9 H35 Z M32,9 H62 V10 H32 Z M30,10 H42 V11 H30 Z M52,10 H64 V11 H52 Z M28,11 H37 V12 H28 Z M57,11 H66 V12 H57 Z M26,12 H34 V13 H26 Z M60,12 H68 V13 H60 Z M24,13 H31 V14 H24 Z M63,13 H70 V14 H63 Z M23,14 H29 V15 H23 Z M65,14 H71 V15 H65 Z M21,15 H27 V16 H21 Z M67,15 H73 V16 H67 Z M20,16 H26 V17 H20 Z M43,16 H52 V17 H43 Z M68,16 H74 V17 H68 Z M19,17 H24 V18 H19 Z M39,17 H57 V18 H39 Z M70,17 H75 V18 H70 Z M18,18 H23 V19 H18 Z M36,18 H59 V19 H36 Z M71,18 H76 V19 H71 Z M17,19 H22 V20 H17 Z M34,19 H61 V20 H34 Z M72,19 H77 V20 H72 Z M16,20 H21 V21 H16 Z M32,20 H41 V21 H32 Z M55,20 H63 V21 H55 Z M73,20 H78 V21 H73 Z M15,21 H20 V22 H15 Z M30,21 H37 V22 H30 Z M58,21 H65 V22 H58 Z M74,21 H79 V22 H74 Z M14,22 H19 V23 H14 Z M29,22 H35 V23 H29 Z M60,22 H66 V23 H60 Z M75,22 H80 V23 H75 Z M13,23 H18 V24 H13 Z M28,23 H34 V24 H28 Z M62,23 H67 V24 H62 Z M76,23 H81 V24 H76 Z M13,24 H17 V25 H13 Z M27,24 H32 V25 H27 Z M63,24 H69 V25 H63 Z M77,24 H81 V25 H77 Z M12,25 H16 V26 H12 Z M25,25 H31 V26 H25 Z M64,25 H70 V26 H64 Z M78,25 H82 V26 H78 Z M11,26 H15 V27 H11 Z M25,26 H30 V27 H25 Z M66,26 H71 V27 H66 Z M79,26 H83 V27 H79 Z M11,27 H15 V28 H11 Z M24,27 H28 V28 H24 Z M67,27 H71 V28 H67 Z M79,27 H83 V28 H79 Z M10,28 H14 V29 H10 Z M23,28 H27 V29 H23 Z M68,28 H72 V29 H68 Z M80,28 H84 V29 H80 Z M10,29 H13 V30 H10 Z M22,29 H27 V30 H22 Z M69,29 H73 V30 H69 Z M81,29 H84 V30 H81 Z M9,30 H13 V31 H9 Z M22,30 H26 V31 H22 Z M69,30 H74 V31 H69 Z M81,30 H85 V31 H81 Z M9,31 H12 V32 H9 Z M21,31 H25 V32 H21 Z M70,31 H74 V32 H70 Z M82,31 H85 V32 H82 Z M8,32 H12 V33 H8 Z M20,32 H24 V33 H20 Z M71,32 H75 V33 H71 Z M82,32 H86 V33 H82 Z M8,33 H11 V34 H8 Z M20,33 H24 V34 H20 Z M71,33 H75 V34 H71 Z M83,33 H86 V34 H83 Z M7,34 H11 V35 H7 Z M19,34 H23 V35 H19 Z M72,34 H76 V35 H72 Z M83,34 H87 V35 H83 Z M7,35 H11 V36 H7 Z M19,35 H23 V36 H19 Z M73,35 H76 V36 H73 Z M83,35 H87 V36 H83 Z M7,36 H10 V37 H7 Z M19,36 H22 V37 H19 Z M73,36 H77 V37 H73 Z M84,36 H87 V37 H84 Z M6,37 H10 V38 H6 Z M18,37 H22 V38 H18 Z M73,37 H77 V38 H73 Z M84,37 H88 V38 H84 Z M6,38 H10 V39 H6 Z M18,38 H21 V39 H18 Z M74,38 H77 V39 H74 Z M84,38 H88 V39 H84 Z M6,39 H9 V40 H6 Z M18,39 H21 V40 H18 Z M74,39 H78 V40 H74 Z M85,39 H88 V40 H85 Z M6,40 H9 V41 H6 Z M17,40 H21 V41 H17 Z M74,40 H78 V41 H74 Z M85,40 H88 V41 H85 Z M5,41 H9 V42 H5 Z M17,41 H21 V42 H17 Z M75,41 H78 V42 H75 Z M85,41 H89 V42 H85 Z M5,42 H9 V43 H5 Z M17,42 H20 V43 H17 Z M75,42 H78 V43 H75 Z M85,42 H89 V43 H85 Z M5,43 H9 V44 H5 Z M17,43 H20 V44 H17 Z M45,43 H49 V44 H45 Z M75,43 H78 V44 H75 Z M85,43 H89 V44 H85 Z M5,44 H8 V45 H5 Z M17,44 H20 V45 H17 Z M43,44 H50 V45 H43 Z M75,44 H78 V45 H75 Z M86,44 H89 V45 H86 Z M5,45 H8 V46 H5 Z M17,45 H20 V46 H17 Z M43,45 H46 V46 H43 Z M48,45 H51 V46 H48 Z M75,45 H79 V46 H75 Z M86,45 H89 V46 H86 Z M5,46 H8 V47 H5 Z M17,46 H20 V47 H17 Z M42,46 H45 V47 H42 Z M50,46 H52 V47 H50 Z M75,46 H79 V47 H75 Z M86,46 H89 V47 H86 Z M5,47 H8 V48 H5 Z M17,47 H20 V48 H17 Z M41,47 H44 V48 H41 Z M50,47 H53 V48 H50 Z M75,47 H79 V48 H75 Z M86,47 H89 V48 H86 Z M5,48 H8 V49 H5 Z M17,48 H20 V49 H17 Z M41,48 H43 V49 H41 Z M51,48 H53 V49 H51 Z M75,48 H79 V49 H75 Z M86,48 H89 V49 H86 Z M5,49 H8 V50 H5 Z M17,49 H20 V50 H17 Z M41,49 H43 V50 H41 Z M50,49 H53 V50 H50 Z M75,49 H78 V50 H75 Z M86,49 H89 V50 H86 Z M5,50 H8 V51 H5 Z M17,50 H20 V51 H17 Z M41,50 H44 V51 H41 Z M50,50 H53 V51 H50 Z M75,50 H78 V51 H75 Z M86,50 H89 V51 H86 Z M5,51 H8 V52 H5 Z M17,51 H20 V52 H17 Z M42,51 H45 V52 H42 Z M49,51 H52 V52 H49 Z M75,51 H78 V52 H75 Z M86,51 H89 V52 H86 Z M5,52 H8 V53 H5 Z M17,52 H20 V53 H17 Z M42,52 H46 V53 H42 Z M48,52 H52 V53 H48 Z M75,52 H78 V53 H75 Z M86,52 H89 V53 H86 Z M5,53 H8 V54 H5 Z M17,53 H21 V54 H17 Z M43,53 H50 V54 H43 Z M75,53 H78 V54 H75 Z M86,53 H89 V54 H86 Z M5,54 H9 V55 H5 Z M17,54 H21 V55 H17 Z M45,54 H49 V55 H45 Z M74,54 H78 V55 H74 Z M85,54 H89 V55 H85 Z M5,55 H9 V56 H5 Z M18,55 H21 V56 H18 Z M74,55 H78 V56 H74 Z M85,55 H89 V56 H85 Z M6,56 H9 V57 H6 Z M18,56 H21 V57 H18 Z M75,56 H77 V57 H75 Z M85,56 H88 V57 H85 Z M6,57 H9 V58 H6 Z M85,57 H88 V58 H85 Z M6,58 H9 V59 H6 Z M85,58 H88 V59 H85 Z M6,59 H10 V60 H6 Z M84,59 H88 V60 H84 Z M6,60 H10 V61 H6 Z M30,60 H36 V61 H30 Z M38,60 H42 V61 H38 Z M43,60 H47 V61 H43 Z M48,60 H56 V61 H48 Z M59,60 H64 V61 H59 Z M84,60 H88 V61 H84 Z M7,61 H10 V62 H7 Z M30,61 H36 V62 H30 Z M38,61 H42 V62 H38 Z M43,61 H47 V62 H43 Z M48,61 H56 V62 H48 Z M58,61 H65 V62 H58 Z M84,61 H87 V62 H84 Z M7,62 H11 V63 H7 Z M30,62 H36 V63 H30 Z M38,62 H42 V63 H38 Z M43,62 H47 V63 H43 Z M48,62 H56 V63 H48 Z M57,62 H61 V63 H57 Z M62,62 H65 V63 H62 Z M83,62 H87 V63 H83 Z M7,63 H11 V64 H7 Z M30,63 H36 V64 H30 Z M38,63 H42 V64 H38 Z M43,63 H47 V64 H43 Z M50,63 H54 V64 H50 Z M57,63 H61 V64 H57 Z M62,63 H66 V64 H62 Z M83,63 H87 V64 H83 Z M8,64 H11 V65 H8 Z M30,64 H36 V65 H30 Z M38,64 H42 V65 H38 Z M43,64 H47 V65 H43 Z M50,64 H54 V65 H50 Z M57,64 H61 V65 H57 Z M62,64 H66 V65 H62 Z M83,64 H86 V65 H83 Z M8,65 H12 V66 H8 Z M30,65 H36 V66 H30 Z M38,65 H42 V66 H38 Z M43,65 H47 V66 H43 Z M50,65 H54 V66 H50 Z M57,65 H61 V66 H57 Z M62,65 H66 V66 H62 Z M82,65 H86 V66 H82 Z M9,66 H12 V67 H9 Z M29,66 H37 V67 H29 Z M38,66 H42 V67 H38 Z M43,66 H47 V67 H43 Z M50,66 H54 V67 H50 Z M57,66 H61 V67 H57 Z M62,66 H66 V67 H62 Z M82,66 H85 V67 H82 Z M9,67 H13 V68 H9 Z M29,67 H32 V68 H29 Z M34,67 H37 V68 H34 Z M38,67 H42 V68 H38 Z M43,67 H47 V68 H43 Z M50,67 H54 V68 H50 Z M57,67 H61 V68 H57 Z M62,67 H66 V68 H62 Z M81,67 H85 V68 H81 Z M10,68 H13 V69 H10 Z M29,68 H32 V69 H29 Z M34,68 H37 V69 H34 Z M38,68 H42 V69 H38 Z M43,68 H47 V69 H43 Z M50,68 H54 V69 H50 Z M57,68 H61 V69 H57 Z M62,68 H66 V69 H62 Z M81,68 H84 V69 H81 Z M10,69 H14 V70 H10 Z M29,69 H37 V70 H29 Z M38,69 H42 V70 H38 Z M43,69 H47 V70 H43 Z M50,69 H54 V70 H50 Z M57,69 H61 V70 H57 Z M62,69 H66 V70 H62 Z M80,69 H84 V70 H80 Z M11,70 H15 V71 H11 Z M29,70 H37 V71 H29 Z M38,70 H42 V71 H38 Z M43,70 H47 V71 H43 Z M50,70 H54 V71 H50 Z M57,70 H61 V71 H57 Z M62,70 H66 V71 H62 Z M79,70 H83 V71 H79 Z M11,71 H15 V72 H11 Z M29,71 H37 V72 H29 Z M38,71 H42 V72 H38 Z M43,71 H47 V72 H43 Z M50,71 H54 V72 H50 Z M57,71 H61 V72 H57 Z M62,71 H66 V72 H62 Z M79,71 H83 V72 H79 Z M12,72 H16 V73 H12 Z M29,72 H32 V73 H29 Z M34,72 H38 V73 H34 Z M39,72 H47 V73 H39 Z M50,72 H54 V73 H50 Z M57,72 H65 V73 H57 Z M78,72 H82 V73 H78 Z M13,73 H17 V74 H13 Z M28,73 H32 V74 H28 Z M34,73 H38 V74 H34 Z M39,73 H46 V74 H39 Z M50,73 H54 V74 H50 Z M58,73 H65 V74 H58 Z M77,73 H81 V74 H77 Z M13,74 H18 V75 H13 Z M28,74 H32 V75 H28 Z M34,74 H38 V75 H34 Z M40,74 H45 V75 H40 Z M50,74 H54 V75 H50 Z M59,74 H64 V75 H59 Z M76,74 H81 V75 H76 Z M14,75 H19 V76 H14 Z M75,75 H80 V76 H75 Z M15,76 H20 V77 H15 Z M74,76 H79 V77 H74 Z M16,77 H21 V78 H16 Z M73,77 H78 V78 H73 Z M17,78 H22 V79 H17 Z M72,78 H77 V79 H72 Z M18,79 H23 V80 H18 Z M71,79 H76 V80 H71 Z M19,80 H24 V81 H19 Z M70,80 H75 V81 H70 Z M20,81 H26 V82 H20 Z M68,81 H74 V82 H68 Z M21,82 H27 V83 H21 Z M67,82 H73 V83 H67 Z M23,83 H29 V84 H23 Z M65,83 H71 V84 H65 Z M24,84 H31 V85 H24 Z M63,84 H70 V85 H63 Z M26,85 H34 V86 H26 Z M60,85 H68 V86 H60 Z M28,86 H37 V87 H28 Z M57,86 H66 V87 H57 Z M30,87 H42 V88 H30 Z M52,87 H64 V88 H52 Z M32,88 H62 V89 H32 Z M35,89 H59 V90 H35 Z M40,90 H55 V91 H40 Z",
            ThermalMode.Performance=>"M38,4 H54 V5 H38 Z M34,5 H58 V6 H34 Z M31,6 H61 V7 H31 Z M29,7 H41 V8 H29 Z M51,7 H63 V8 H51 Z M27,8 H36 V9 H27 Z M56,8 H65 V9 H56 Z M25,9 H33 V10 H25 Z M59,9 H67 V10 H59 Z M23,10 H30 V11 H23 Z M62,10 H69 V11 H62 Z M22,11 H28 V12 H22 Z M64,11 H70 V12 H64 Z M20,12 H26 V13 H20 Z M66,12 H72 V13 H66 Z M19,13 H25 V14 H19 Z M42,13 H50 V14 H42 Z M67,13 H73 V14 H67 Z M18,14 H23 V15 H18 Z M37,14 H55 V15 H37 Z M69,14 H74 V15 H69 Z M17,15 H22 V16 H17 Z M34,15 H58 V16 H34 Z M70,15 H75 V16 H70 Z M16,16 H21 V17 H16 Z M32,16 H60 V17 H32 Z M71,16 H76 V17 H71 Z M15,17 H20 V18 H15 Z M30,17 H62 V18 H30 Z M72,17 H77 V18 H72 Z M14,18 H19 V19 H14 Z M28,18 H35 V19 H28 Z M48,18 H64 V19 H48 Z M73,18 H78 V19 H73 Z M13,19 H18 V20 H13 Z M27,19 H31 V20 H27 Z M51,19 H65 V20 H51 Z M74,19 H79 V20 H74 Z M12,20 H17 V21 H12 Z M26,20 H29 V21 H26 Z M53,20 H67 V21 H53 Z M75,20 H80 V21 H75 Z M12,21 H16 V22 H12 Z M24,21 H27 V22 H24 Z M55,21 H68 V22 H55 Z M76,21 H80 V22 H76 Z M11,22 H15 V23 H11 Z M23,22 H26 V23 H23 Z M57,22 H69 V23 H57 Z M77,22 H81 V23 H77 Z M10,23 H14 V24 H10 Z M22,23 H24 V24 H22 Z M58,23 H70 V24 H58 Z M78,23 H82 V24 H78 Z M10,24 H14 V25 H10 Z M21,24 H23 V25 H21 Z M59,24 H71 V25 H59 Z M78,24 H82 V25 H78 Z M9,25 H13 V26 H9 Z M21,25 H22 V26 H21 Z M61,25 H72 V26 H61 Z M79,25 H83 V26 H79 Z M9,26 H12 V27 H9 Z M20,26 H21 V27 H20 Z M62,26 H72 V27 H62 Z M80,26 H83 V27 H80 Z M8,27 H12 V28 H8 Z M19,27 H20 V28 H19 Z M63,27 H73 V28 H63 Z M80,27 H84 V28 H80 Z M8,28 H11 V29 H8 Z M18,28 H19 V29 H18 Z M63,28 H74 V29 H63 Z M81,28 H84 V29 H81 Z M7,29 H11 V30 H7 Z M64,29 H74 V30 H64 Z M81,29 H85 V30 H81 Z M7,30 H10 V31 H7 Z M17,30 H18 V31 H17 Z M65,30 H75 V31 H65 Z M82,30 H85 V31 H82 Z M6,31 H10 V32 H6 Z M65,31 H75 V32 H65 Z M82,31 H86 V32 H82 Z M6,32 H10 V33 H6 Z M16,32 H17 V33 H16 Z M66,32 H76 V33 H66 Z M82,32 H86 V33 H82 Z M6,33 H9 V34 H6 Z M67,33 H76 V34 H67 Z M83,33 H86 V34 H83 Z M5,34 H9 V35 H5 Z M67,34 H77 V35 H67 Z M83,34 H87 V35 H83 Z M5,35 H9 V36 H5 Z M68,35 H77 V36 H68 Z M83,35 H87 V36 H83 Z M5,36 H8 V37 H5 Z M68,36 H77 V37 H68 Z M84,36 H87 V37 H84 Z M5,37 H8 V38 H5 Z M68,37 H78 V38 H68 Z M84,37 H87 V38 H84 Z M4,38 H8 V39 H4 Z M69,38 H78 V39 H69 Z M84,38 H88 V39 H84 Z M4,39 H8 V40 H4 Z M41,39 H49 V40 H41 Z M69,39 H78 V40 H69 Z M84,39 H88 V40 H84 Z M4,40 H8 V41 H4 Z M40,40 H50 V41 H40 Z M69,40 H78 V41 H69 Z M84,40 H88 V41 H84 Z M4,41 H7 V42 H4 Z M39,41 H42 V42 H39 Z M48,41 H51 V42 H48 Z M69,41 H78 V42 H69 Z M85,41 H88 V42 H85 Z M4,42 H7 V43 H4 Z M38,42 H41 V43 H38 Z M49,42 H52 V43 H49 Z M69,42 H79 V43 H69 Z M85,42 H88 V43 H85 Z M4,43 H7 V44 H4 Z M37,43 H40 V44 H37 Z M50,43 H52 V44 H50 Z M69,43 H79 V44 H69 Z M85,43 H88 V44 H85 Z M4,44 H7 V45 H4 Z M37,44 H39 V45 H37 Z M50,44 H53 V45 H50 Z M70,44 H79 V45 H70 Z M85,44 H88 V45 H85 Z M4,45 H7 V46 H4 Z M37,45 H39 V46 H37 Z M51,45 H53 V46 H51 Z M70,45 H79 V46 H70 Z M85,45 H88 V46 H85 Z M4,46 H7 V47 H4 Z M37,46 H39 V47 H37 Z M51,46 H53 V47 H51 Z M70,46 H79 V47 H70 Z M85,46 H88 V47 H85 Z M4,47 H7 V48 H4 Z M37,47 H39 V48 H37 Z M51,47 H53 V48 H51 Z M70,47 H79 V48 H70 Z M85,47 H88 V48 H85 Z M4,48 H7 V49 H4 Z M37,48 H39 V49 H37 Z M51,48 H53 V49 H51 Z M69,48 H79 V49 H69 Z M85,48 H88 V49 H85 Z M4,49 H7 V50 H4 Z M37,49 H40 V50 H37 Z M50,49 H53 V50 H50 Z M69,49 H79 V50 H69 Z M85,49 H88 V50 H85 Z M4,50 H7 V51 H4 Z M38,50 H41 V51 H38 Z M49,50 H55 V51 H49 Z M69,50 H78 V51 H69 Z M85,50 H88 V51 H85 Z M4,51 H8 V52 H4 Z M38,51 H42 V52 H38 Z M48,51 H56 V52 H48 Z M69,51 H78 V52 H69 Z M84,51 H88 V52 H84 Z M4,52 H8 V53 H4 Z M39,52 H44 V53 H39 Z M46,52 H51 V53 H46 Z M52,52 H58 V53 H52 Z M69,52 H78 V53 H69 Z M84,52 H88 V53 H84 Z M4,53 H8 V54 H4 Z M40,53 H49 V54 H40 Z M54,53 H59 V54 H54 Z M69,53 H78 V54 H69 Z M84,53 H87 V54 H84 Z M5,54 H8 V55 H5 Z M43,54 H47 V55 H43 Z M56,54 H61 V55 H56 Z M68,54 H78 V55 H68 Z M84,54 H87 V55 H84 Z M5,55 H8 V56 H5 Z M58,55 H62 V56 H58 Z M68,55 H77 V56 H68 Z M84,55 H87 V56 H84 Z M5,56 H9 V57 H5 Z M60,56 H64 V57 H60 Z M68,56 H77 V57 H68 Z M83,56 H87 V57 H83 Z M5,57 H9 V58 H5 Z M62,57 H65 V58 H62 Z M70,57 H77 V58 H70 Z M83,57 H87 V58 H83 Z M6,58 H9 V59 H6 Z M64,58 H67 V59 H64 Z M72,58 H76 V59 H72 Z M83,58 H86 V59 H83 Z M6,59 H10 V60 H6 Z M66,59 H68 V60 H66 Z M74,59 H76 V60 H74 Z M82,59 H86 V60 H82 Z M6,60 H10 V61 H6 Z M68,60 H69 V61 H68 Z M82,60 H86 V61 H82 Z M7,61 H10 V62 H7 Z M70,61 H71 V62 H70 Z M82,61 H85 V62 H82 Z M7,62 H11 V63 H7 Z M81,62 H85 V63 H81 Z M8,63 H11 V64 H8 Z M81,63 H84 V64 H81 Z M8,64 H12 V65 H8 Z M80,64 H84 V65 H80 Z M9,65 H12 V66 H9 Z M80,65 H83 V66 H80 Z M9,66 H13 V67 H9 Z M79,66 H83 V67 H79 Z M10,67 H14 V68 H10 Z M78,67 H82 V68 H78 Z M10,68 H14 V69 H10 Z M78,68 H82 V69 H78 Z M11,69 H15 V70 H11 Z M77,69 H81 V70 H77 Z M12,70 H16 V71 H12 Z M76,70 H80 V71 H76 Z M12,71 H17 V72 H12 Z M75,71 H80 V72 H75 Z M13,72 H18 V73 H13 Z M74,72 H79 V73 H74 Z M14,73 H19 V74 H14 Z M73,73 H78 V74 H73 Z M15,74 H20 V75 H15 Z M72,74 H77 V75 H72 Z M16,75 H21 V76 H16 Z M71,75 H76 V76 H71 Z M17,76 H22 V77 H17 Z M70,76 H75 V77 H70 Z M18,77 H23 V78 H18 Z M69,77 H74 V78 H69 Z M19,78 H25 V79 H19 Z M67,78 H73 V79 H67 Z M20,79 H26 V80 H20 Z M66,79 H72 V80 H66 Z M22,80 H28 V81 H22 Z M64,80 H70 V81 H64 Z M23,81 H30 V82 H23 Z M62,81 H69 V82 H62 Z M25,82 H33 V83 H25 Z M59,82 H67 V83 H59 Z M27,83 H36 V84 H27 Z M56,83 H65 V84 H56 Z M29,84 H41 V85 H29 Z M51,84 H63 V85 H51 Z M31,85 H61 V86 H31 Z M34,86 H58 V87 H34 Z M38,87 H54 V88 H38 Z",
            _=>"M0,0 L24,24 M24,0 L0,24"
        };
        var icon=(Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='28' Height='28' Stretch='Uniform' Data='"+data+"' />");
        icon.Fill=ink;return icon;
    }

    Microsoft.UI.Xaml.Shapes.Path? fnShackle;
    UIElement MakeFnIcon()
    {
        var grid = new Grid { Width = 34, Height = 36, IsHitTestVisible = false, FlowDirection = FlowDirection.LeftToRight };
        fnShackle = new Microsoft.UI.Xaml.Shapes.Path { StrokeThickness = 2, Stroke = Primary, Data = LockShackle(false) };
        grid.Children.Add(fnShackle);
        grid.Children.Add(new Border { Width = 25, Height = 21, BorderThickness = new Thickness(2), BorderBrush = Primary, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom });
        grid.Children.Add(new TextBlock { Text = "Fn", FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Primary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.NoWrap });
        return grid;
    }
    static PathGeometry LockShackle(bool locked)
    {
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(9, 16), IsClosed = false };
        figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(9, 10) });
        figure.Segments.Add(new BezierSegment { Point1 = new Windows.Foundation.Point(9, 1), Point2 = new Windows.Foundation.Point(25, 1), Point3 = new Windows.Foundation.Point(25, 10) });
        if (locked) figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(25, 16) });
        var geometry = new PathGeometry(); geometry.Figures.Add(figure); return geometry;
    }

    void UpdateBatteryFill()
    {
        if (batteryFill == null || batteryFillHost == null) return;
        int? percent = current?.Percent;
        batteryColor = BatteryColorFor(percent);
        batteryFill.Background = new SolidColorBrush(batteryColor);
        batteryFill.Width = percent.HasValue ? Math.Max(0, batteryFillHost.ActualWidth * percent.Value / 100d) : 0;
        var onFill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        bool centreOnFill = (percent ?? 0) >= 50;
        if (percentText != null) percentText.Foreground = onFill;
        if (plugIcon != null) plugIcon.Foreground = onFill;
    }

    void Apply()
    {
        if (view != "main" || percentText == null) return;
        var value = current;
        if (value == null) { if (statusText != null) statusText.Text = preview ? L.T("preview") : ""; UpdateBatteryFill(); return; }
        percentText.Text = value.Percent.HasValue ? $"{value.Percent}%" : "--%";
        plugIcon!.Visibility = value.Plugged ? Visibility.Visible : Visibility.Collapsed;
        chargeText!.Text = value.Charging ? L.T("charging") : value.Plugged ? L.T("plugged") : L.T("onbattery");
        UpdateBatteryFill();
        void Set(string id, bool? state, bool supported, string? tip)
        {
            if (!tileButtons.TryGetValue(id, out var button)) return;
            button.IsEnabled = supported && !preview;
            button.IsChecked = state ?? false;
            if (button.Content is FontIcon glyph) glyph.Foreground = state == true ? ((ParseAccent(settings.AccentColor).HasValue && !string.Equals(settings.AccentColor,"#9CDA9B",StringComparison.OrdinalIgnoreCase)) ? Rgb(255,255,255) : Rgb(20,55,27)) : Primary;
            if (id == "fn" && button.Content is Grid lockGrid) foreach (var child in lockGrid.Children) {
                Brush ink = state == true ? ((ParseAccent(settings.AccentColor).HasValue && !string.Equals(settings.AccentColor,"#9CDA9B",StringComparison.OrdinalIgnoreCase)) ? Rgb(255,255,255) : Rgb(20,55,27)) : Primary;
                if (child is Microsoft.UI.Xaml.Shapes.Path path) path.Stroke = ink;
                if (child is Border outline) outline.BorderBrush = ink;
                if (child is TextBlock fnLabel) fnLabel.Foreground = ink;
            }
            if (tip != null) ToolTipService.SetToolTip(button, tip);
        }
        Set("thermal",false,value.Thermal.HasValue,L.Ar ? "اضغط للتبديل بين المتوازن والأداء والهادئ" : "Click to cycle Balanced, Performance, Quiet");
        if(tileButtons.TryGetValue("thermal",out var thermalButton)) {
            thermalButton.Content=ThermalIcon(value.Thermal);
            if(thermalButton.Parent is StackPanel thermalPanel && thermalPanel.Children[1] is TextBlock thermalLabel)
            thermalLabel.Text=value.Thermal switch { ThermalMode.Quiet=>L.Ar ? "هادئ" : "Quiet",ThermalMode.Balanced=>L.Ar ? "متوازن" : "Balanced",ThermalMode.Performance=>L.Ar ? "أداء" : "Performance",_=>L.Ar ? "غير متاح" : "Unavailable" };
        }
        Set("fn", value.FnLocked, value.FnLocked.HasValue, L.T("tip.fn"));
        if (fnShackle != null) fnShackle.Data = LockShackle(value.FnLocked == true);
        Set("usb", value.UsbMode == 2, value.UsbMode.HasValue, L.T("tip.usb"));
        var panelRates = value.AvailableHz?.OrderBy(x => x).ToArray() ?? [];
        bool exactRates = panelRates.Length >= 2;
        Set("refresh", exactRates && value.RefreshHz == panelRates[^1], exactRates, exactRates ? L.T("tip.refresh.ok") : L.T("tip.refresh.no"));
        Set("mic", value.Muted, value.Muted.HasValue, L.T("tip.mic"));
        Set("conserve", value.Mode == ChargeMode.Conservation, value.Mode.HasValue, L.T("tip.conserve"));
        Set("rapid", value.Mode == ChargeMode.Rapid, value.Mode.HasValue, L.T("tip.rapid"));
        Set("touchpad", value.TouchpadLocked.HasValue ? !value.TouchpadLocked.Value : null, value.TouchpadLocked.HasValue, L.T("tip.touchpad"));
        var checkedInk=ParseAccent(settings.AccentColor).HasValue ? Rgb(255,255,255) : Rgb(20,55,27);
        if(tileButtons.TryGetValue("mic",out var microphone))microphone.Content=value.Muted==true ? MicOffIcon(checkedInk) : Glyph("\uE720",24);
        if(tileButtons.TryGetValue("touchpad",out var pad))pad.Content=TouchpadIcon(value.TouchpadLocked==true,value.TouchpadLocked==false ? checkedInk : Primary);
        if (tileButtons.TryGetValue("refresh", out var refreshButton) && refreshButton.Parent is StackPanel panel && panel.Children.Count > 1 && panel.Children[1] is TextBlock label)
            label.Text = exactRates ? $"{panelRates[0]} / {panelRates[^1]} Hz" : value.RefreshHz.HasValue ? $"{value.RefreshHz} Hz" : "-- Hz";
        if (statusText != null) statusText.Text = preview ? L.T("preview") : value.Problems.Length > 0 ? L.T("status.some") : L.T("status.ok");
    }

    void OpenDashboard(){dashboard=new DeviceDashboardWindow(settings.Theme,current,preview);Navigate("device");}

    /// <summary>Windows taskbar theme ("Windows mode"), independent of the app's own theme setting. The white glyph vanishes on a light taskbar.</summary>
    internal static bool TaskbarIsLight()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1; }
        catch { return false; }
    }
    internal static string TrayIconPath() => Path.Combine(AppContext.BaseDirectory, TaskbarIsLight() ? "tray-dark.ico" : "tray-white.ico");


    string UpdateLabel => L.Ar ? "التحقق من التحديثات" : "Check for updates";
    UIElement UpdateCard(bool compact = false)
    {
        var stack = new StackPanel { Spacing=8, HorizontalAlignment=HorizontalAlignment.Stretch };
        if(!compact) stack.Children.Add(Text(L.Ar ? "التحديثات" : "Updates",16,true));
        if(!compact) stack.Children.Add(Text(L.Ar ? "اضغط الزر للتحقق من التحديثات الآن." : "Press the button to check for updates now.",13,false,SecondaryText));
        if(!compact) stack.Children.Add(Text((L.Ar ? "الإصدار الحالي: " : "Current version: ")+"v"+(typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),13,false,SecondaryText));
        var buttonsRow=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        var button=new Button { Content=UpdateLabel, IsEnabled=!checkingUpdates, HorizontalAlignment=compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
        button.Click+=async (_,_)=>await CheckForUpdatesAsync();updateButtons.Add(button);buttonsRow.Children.Add(button);
        var install=new Button { Content=L.Ar ? "تنزيل وتثبيت" : "Download and install", HorizontalAlignment=compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, Visibility=Visibility.Collapsed };
        install.Click+=(_,_)=>InstallUpdate();installButtons.Add(install);buttonsRow.Children.Add(install);
        stack.Children.Add(buttonsRow);
        var result=Text("",13);result.TextWrapping=TextWrapping.Wrap;if(compact) result.TextAlignment=TextAlignment.Center;updateResults.Add(result);stack.Children.Add(result);
        var link=new HyperlinkButton { Content=L.Ar ? "فتح صفحة التنزيل" : "Open download page", HorizontalAlignment=compact ? HorizontalAlignment.Center : HorizontalAlignment.Left };
        updateLinks.Add(link);stack.Children.Add(link);RefreshUpdateCards();
        return compact ? stack : Card(stack,new Thickness(24));
    }
    void RefreshUpdateCards()
    {
        string installed=typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        string result=manualUpdateState switch {
            "checking" => L.Ar ? "جارٍ التحقق..." : "Checking...",
            "available" => L.Ar ? "الإصدار "+manualUpdateVersion+" متاح." : "Version "+manualUpdateVersion+" is available.",
            "current" => L.Ar ? "أنت على أحدث إصدار (v"+installed+")." : "You're up to date (v"+installed+").",
            "error" => L.Ar ? "تعذر التحقق من التحديثات. تحقق من اتصالك بالإنترنت وحاول مرة أخرى." : "Couldn't check for updates. Check your internet connection and try again.",
            _ => ""
        };
        foreach(var button in updateButtons){button.IsEnabled=!checkingUpdates;button.Content=UpdateLabel;}
        foreach(var install in installButtons){install.Content=L.Ar ? "تنزيل وتثبيت" : "Download and install";install.Visibility=manualUpdateAsset==null ? Visibility.Collapsed : Visibility.Visible;}
        foreach(var text in updateResults)text.Text=result;
        foreach(var link in updateLinks){link.NavigateUri=manualUpdateUrl==null ? null : new Uri(manualUpdateUrl);link.Visibility=manualUpdateUrl==null ? Visibility.Collapsed : Visibility.Visible;}
    }
    static Version? ReleaseVersion(string? tag) => Version.TryParse(tag?.TrimStart('v','V'),out var version) ? version : null;
    static string? UpdateDownload(System.Text.Json.JsonElement release, Version installed,bool bundled=true) => UpdateAssetFor(release,installed,bundled)?.Url;
    internal static UpdateAsset? UpdateAssetFor(System.Text.Json.JsonElement release, Version installed,bool bundled=true)
    {
        if(release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())return null;
        var next=ReleaseVersion(release.GetProperty("tag_name").GetString());
        if(next==null || next<=installed)return null;
        foreach(var asset in release.GetProperty("assets").EnumerateArray()) {
            var name=asset.GetProperty("name").GetString();
            // Accept the current installer name and the pre-0.4.0 legacy name, so older installs can still self-update.
            if(name!=$"FluentVantageToolbar-v{next.ToString(3)}-installer-win-x64.exe" && name!=$"FluentVantageToolbar-{next.ToString(3)}-Setup{(bundled ? "-with-dotnet" : "")}.exe")continue;
            var url=asset.GetProperty("browser_download_url").GetString();
            if(Uri.TryCreate(url,UriKind.Absolute,out var uri) && uri.Scheme=="https" && uri.Host=="github.com" && uri.AbsolutePath.StartsWith("/StarsDust01/Fluent-Vantage-Toolbar/releases/download/",StringComparison.Ordinal)) {
                long size=asset.TryGetProperty("size",out var sz) && sz.ValueKind==System.Text.Json.JsonValueKind.Number ? sz.GetInt64() : 0;
                string? sha=null;
                if(asset.TryGetProperty("digest",out var dg) && dg.ValueKind==System.Text.Json.JsonValueKind.String && (dg.GetString() ?? "").StartsWith("sha256:",StringComparison.OrdinalIgnoreCase))sha=dg.GetString()!.Substring(7);
                return new UpdateAsset(url!,name!,size,sha);
            }
        }
        throw new InvalidOperationException("New release has no installer matching this build flavor.");
    }
    // Self-contained .NET builds carry coreclr.dll beside the executable;
    // framework-dependent builds load it from the installed shared runtime.
    bool InstalledBundled => File.Exists(Path.Combine(AppContext.BaseDirectory,"coreclr.dll"));
    async Task AutomaticUpdatesAsync(CancellationToken token)
    {
        try {
            await Task.Delay(TimeSpan.FromSeconds(45),token);
            while(!token.IsCancellationRequested) {
                if(settings.AutoCheckUpdates)await CheckAutomaticUpdateAsync();
                await Task.Delay(TimeSpan.FromHours(6),token);
            }
        } catch(OperationCanceledException) { }
    }
    async Task CheckAutomaticUpdateAsync()
    {
        if(preview || exiting || checkingUpdates || !settings.AutoCheckUpdates)return;
        checkingUpdates=true;
        try {
            using var http=new System.Net.Http.HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentVantageToolbar/"+(typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response=await http.GetAsync("https://api.github.com/repos/StarsDust01/Fluent-Vantage-Toolbar/releases/latest",updateLifetime.Token);
            response.EnsureSuccessStatusCode();
            using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(updateLifetime.Token));
            var installed=ReleaseVersion(typeof(MainWindow).Assembly.GetName().Version?.ToString(3)) ?? new Version(0,0,0);
            var asset=UpdateAssetFor(json.RootElement,installed,InstalledBundled);
            var tag=json.RootElement.GetProperty("tag_name").GetString();
            if(asset==null || !settings.AutoCheckUpdates || exiting || tag==settings.LastNotifiedUpdate)return;
            ShowUpdateNotice(tag ?? "",asset);
            settings.LastNotifiedUpdate=tag;settings.Save();
        } catch(OperationCanceledException) { }
        catch(Exception error) { DiagnosticLog.Write("Automatic update check failed: "+error.Message); }
        finally {checkingUpdates=false;}
    }
    void ShowUpdateNotice(string version,UpdateAsset asset)
    {
        try{updateToast?.Close();}catch{}
        updateToast=new UpdateToast(version.TrimStart('v','V'),asset,"https://github.com/StarsDust01/Fluent-Vantage-Toolbar/releases/latest",Dark);
        if(!preview) {
            tray.Notify(L.Ar ? "يتوفر تحديث جديد" : "Update available",$"Fluent Vantage Toolbar {version}");
            updateToast.ShowToast();
        }
    }

    /// <summary>Opens the update toast from a manual check and starts the in-app download. The installer still runs only after "Install now".</summary>
    void InstallUpdate()
    {
        if(manualUpdateAsset==null || preview)return;
        try{updateToast?.Close();}catch{}
        updateToast=new UpdateToast((manualUpdateVersion ?? "").TrimStart('v','V'),manualUpdateAsset,"https://github.com/StarsDust01/Fluent-Vantage-Toolbar/releases/latest",Dark);
        updateToast.ShowToast();
        updateToast.Begin();
    }

    async Task CheckForUpdatesAsync()
    {
        if(checkingUpdates || preview)return;
        checkingUpdates=true;
        manualUpdateState="checking";manualUpdateUrl=null;manualUpdateAsset=null;RefreshUpdateCards();
        try {
            using var http=new System.Net.Http.HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentVantageToolbar/"+(typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response=await http.GetAsync("https://api.github.com/repos/StarsDust01/Fluent-Vantage-Toolbar/releases/latest");
            response.EnsureSuccessStatusCode();
            using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var installed=ReleaseVersion(typeof(MainWindow).Assembly.GetName().Version?.ToString(3)) ?? new Version(0,0,0);
            manualUpdateAsset=UpdateAssetFor(json.RootElement,installed,InstalledBundled);
            manualUpdateUrl=manualUpdateAsset?.Url;
            manualUpdateVersion=json.RootElement.GetProperty("tag_name").GetString();
            manualUpdateState=manualUpdateUrl==null ? "current" : "available";
        } catch(Exception error) {
            DiagnosticLog.Write("Update check failed: "+error.Message);
            manualUpdateState="error";
        } finally {
            checkingUpdates=false;if(!exiting)RefreshUpdateCards();
        }
    }

    // ---- settings ----
    UIElement BuildSettings()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text(L.T("settings.language"), 14, true));
        var language = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var lg in Extra.Languages) language.Items.Add(lg.name);
        language.SelectedIndex = Math.Max(0, Array.FindIndex(Extra.Languages, x => x.code == settings.Language));
        language.SelectionChanged += (_, _) => { if (language.SelectedIndex < 0) return; string chosen = Extra.Languages[language.SelectedIndex].code; if (chosen == settings.Language) return; settings.Language = chosen; settings.Save(); Render(); };
        stack.Children.Add(language);

        stack.Children.Add(new TextBlock { Height = 4 });
        stack.Children.Add(Text(L.T("settings.theme"), 14, true));
        var theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Items.Add(L.T("theme.system")); theme.Items.Add(L.T("theme.light")); theme.Items.Add(L.T("theme.dark"));
        theme.SelectedIndex = settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        theme.SelectionChanged += (_, _) => { string chosen = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" }; if (chosen == settings.Theme) return; settings.Theme = chosen; settings.Save(); Render(); };
        stack.Children.Add(theme);
        stack.Children.Add(AccentPicker());
        
        stack.Children.Add(new TextBlock { Height = 4 });
        var buttonHeading=new Grid();buttonHeading.ColumnDefinitions.Add(new ColumnDefinition());buttonHeading.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        buttonHeading.Children.Add(Text(L.T("settings.buttons"),14,true));
        var reorder=new Button{Content=reorderMode?(L.Ar?"تم":"Done"):(L.Ar?"ترتيب":"Reorder"),Padding=new Thickness(12,4,12,4),HorizontalAlignment=HorizontalAlignment.Right};
        Grid.SetColumn(reorder,1);buttonHeading.Children.Add(reorder);stack.Children.Add(buttonHeading);
        var rows=new StackPanel{Spacing=2};reorderRows=rows;
        void PopulateRows() {
            rows.Children.Clear();
            foreach(var tile in OrderedTiles()) {
                var row=new Grid{MinHeight=40,Tag=tile.Id,Background=new SolidColorBrush(Colors.Transparent)};
                row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());
                var handle=new Border{ManipulationMode=Microsoft.UI.Xaml.Input.ManipulationModes.None,Width=32,MinHeight=40,Visibility=reorderMode?Visibility.Visible:Visibility.Collapsed,Background=new SolidColorBrush(Colors.Transparent),Child=new FontIcon{Glyph="\uE700",FontSize=16}};
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(handle,L.Ar?"اسحب لترتيب "+L.T(tile.LabelKey):"Drag to reorder "+L.T(tile.LabelKey));
                row.Children.Add(handle);
                var toggleRow=(FrameworkElement)ToggleRow(L.T(tile.LabelKey),settings.IsTileVisible(tile.Id),on=>{settings.SetTileVisible(tile.Id,on);if(!preview)settings.Save();});
                toggleRow.IsHitTestVisible=!reorderMode;toggleRow.Opacity=reorderMode?.65:1;Grid.SetColumn(toggleRow,1);row.Children.Add(toggleRow);
                AttachReorderPointer(handle,row,rows);rows.Children.Add(row);
            }
        }
        reorder.Click+=(_,_)=>{if(reordering)return;reorderMode=!reorderMode;reorder.Content=reorderMode?(L.Ar?"تم":"Done"):(L.Ar?"ترتيب":"Reorder");PopulateRows();};
        PopulateRows();stack.Children.Add(Card(rows,new Thickness(14,6,14,6)));

        stack.Children.Add(new TextBlock { Height = 4 });
        stack.Children.Add(Text(L.T("settings.links"), 14, true));
        var links = new StackPanel { Spacing = 2 };
        links.Children.Add(ToggleRow(L.T("warranty.link"), settings.ShowWarranty, on => { settings.ShowWarranty = on; settings.Save(); }));
        links.Children.Add(ToggleRow(L.T("battery.link"), settings.ShowBatteryDetails, on => { settings.ShowBatteryDetails = on; settings.Save(); }));
        stack.Children.Add(Card(links, new Thickness(14, 6, 14, 6)));

        stack.Children.Add(Text(L.Ar ? "قفل Fn يبدأ مفعّلاً في الواجهة. القراءة الحالية من النظام غير موثوقة؛ هذه ليست إشارة إلى الحالة الفعلية، ولا يغيّر البرنامج قفل Fn عند بدء التشغيل." : "Fn Lock starts ON in the UI. The current system reading is unreliable; this is an assumption, not a verified hardware state. Startup does not change Fn Lock.", 12, false, SecondaryText));
        stack.Children.Add(Text(L.T("status.some"), 12, false, SecondaryText));
        stack.Children.Add(Text(current?.Problems.Length > 0 ? string.Join("\n", current.Problems) : L.T("status.ok"), 12, false, SecondaryText));
        stack.Children.Add(Text(DiagnosticLog.Path, 11, false, SecondaryText));
        var logs = new Button { Content = L.Ar ? "فتح سجل التشخيص" : "Open diagnostic log", HorizontalAlignment = HorizontalAlignment.Stretch };
        logs.Click += (_, _) => OpenUrl(DiagnosticLog.Path);
        stack.Children.Add(logs);
        var exit = new Button { Content = L.T("settings.exit"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
        exit.Click += (_, _) => ExitApp();
        stack.Children.Add(exit);
        stack.Children.Add(ToggleRow(L.Ar ? "البحث التلقائي عن التحديثات" : "Automatically check for updates",settings.AutoCheckUpdates,on=>{settings.AutoCheckUpdates=on;settings.Save();}));
        stack.Children.Add(Text(L.Ar ? "بعد 45 ثانية من التشغيل، ثم كل 6 ساعات. التنزيل والتثبيت بقرارك." : "45 seconds after launch, then every 6 hours. Download and install only when you choose.",12,false,SecondaryText));
        stack.Children.Add(UpdateCard());
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
    }

    void AttachReorderPointer(Border handle,Grid row,StackPanel rows)
    {
        bool dragging=false;double startY=0;TranslateTransform? shift=null;
        void Finish(bool commit) {
            if(!dragging)return;reorderTrace+=$" finish={commit},shift={shift?.Y};";dragging=false;reordering=false;
            row.RenderTransform=new TranslateTransform();row.Opacity=1;row.BorderThickness=new Thickness(0);
            if(commit) {
                int old=rows.Children.IndexOf(row);double midpoint=startY+(shift?.Y??0)+row.ActualHeight/2;
                int target=0;foreach(var child in rows.Children.Cast<FrameworkElement>()) {
                    if(ReferenceEquals(child,row))continue;
                    double y=child.TransformToVisual(rows).TransformPoint(new Windows.Foundation.Point()).Y;
                    if(midpoint>y+child.ActualHeight/2+1)target++;
                }
                if(target!=old){rows.Children.Remove(row);rows.Children.Insert(target,row);}
                settings.TileOrder=rows.Children.Cast<FrameworkElement>().Select(r=>(string)r.Tag).ToList();if(!preview)settings.Save();
            }
            Root.ReleasePointerCaptures();
        }
        handle.PointerPressed+=(_,e)=>{
            reorderPresses++;reorderTrace+=$" press:mode={reorderMode},left={e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed};";if(!reorderMode || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)return;
            dragging=Root.CapturePointer(e.Pointer);reorderTrace+=$" captured={dragging};";if(!dragging)return;reordering=true;
            startY=row.TransformToVisual(rows).TransformPoint(new Windows.Foundation.Point()).Y;
            shift=new TranslateTransform();row.RenderTransform=shift;row.Opacity=.8;
            row.BorderBrush=Primary;row.BorderThickness=new Thickness(1);e.Handled=true;
        };
        Root.PointerMoved+=(_,e)=>{reorderMoves++;if(!dragging)return;double y=e.GetCurrentPoint(rows).Position.Y;shift!.Y=Math.Clamp(y-startY-row.ActualHeight/2,-startY,Math.Max(0,rows.ActualHeight-startY-row.ActualHeight));e.Handled=true;};
        Root.PointerReleased+=(_,e)=>{reorderReleases++;Finish(true);e.Handled=true;};
        Root.PointerCanceled+=(_,_)=>Finish(false);Root.PointerCaptureLost+=(_,_)=>Finish(false);
    }

    UIElement ToggleRow(string label, bool isOn, Action<bool> changed)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Text(label, 14));
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        Grid.SetColumn(toggle, 1); grid.Children.Add(toggle);
        return grid;
    }

    UIElement BuildAbout()
    {
        var stack = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
        try
        {
            var image = new Image { Width = 96, Height = 96, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "legion-logo.png"))) };
            // The supplied logo is white; give it a dark rounded tile so it stays visible in the light theme.
            stack.Children.Add(new Border { Width = 120, Height = 120, CornerRadius = new CornerRadius(28), Background = Rgb(32, 32, 32), Child = image, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(12) });
        }
        catch { }
        var name = Text("Fluent Vantage Toolbar", 22, true); name.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(name);
        var version = Text(L.T("about.version") + " " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.2.0"), 13, false, SecondaryText); version.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(version);
        var dev = Text(L.T("about.developer"), 14); dev.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(dev);
        var contrib = Text(L.T("about.contrib"), 13, false); contrib.HorizontalAlignment = HorizontalAlignment.Center; contrib.TextAlignment = TextAlignment.Center; contrib.TextWrapping = TextWrapping.Wrap; stack.Children.Add(contrib);
        var credit = new HyperlinkButton { Content=L.T("about.credit"), NavigateUri=new Uri("https://instinct.com"), FontSize=13, HorizontalAlignment=HorizontalAlignment.Center }; stack.Children.Add(credit);
        var note = Text(L.T("about.note"), 12, false, SecondaryText); note.TextAlignment = TextAlignment.Center; note.Margin = new Thickness(8, 12, 8, 0); stack.Children.Add(note);
        stack.Children.Add(new HyperlinkButton { Content="GitHub Repo Link", NavigateUri=new Uri("https://github.com/StarsDust01/Fluent-Vantage-Toolbar"), HorizontalAlignment=HorizontalAlignment.Center });
        stack.Children.Add(UpdateCard(true));
        return new ScrollViewer { Content=stack, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    }

    // ---- battery details ----
    async Task LoadBatteryAsync()
    {
        batteryDetails = null;
        try { batteryDetails = await Details.ReadBatteryAsync(); } catch { }
        if (view == "battery") body.Content = BuildBattery();
    }

    Grid DetailRow(string label, string value, Brush? valueBrush = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Text(label, 14, false, Primary));
        var v = Text(value, 14, true, valueBrush); v.HorizontalAlignment = HorizontalAlignment.Right; v.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(v, 1); grid.Children.Add(v);
        return grid;
    }

    UIElement ProgressBarFor(double percent, Brush fill, Brush track)
    {
        var host = new Grid { Height = 8, FlowDirection = FlowDirection.LeftToRight };
        host.Children.Add(new Border { Background = track, CornerRadius = new CornerRadius(4) });
        var bar = new Border { Background = fill, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        host.Children.Add(bar);
        host.SizeChanged += (_, _) => bar.Width = host.ActualWidth * Math.Clamp(percent, 0, 100) / 100d;
        return host;
    }

    UIElement BuildBattery()
    {
        var d = batteryDetails;
        string na = L.T("bat.unavailable");
        string Number(double? v, string unit) => v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) + unit : na;
        var stack = new StackPanel { Spacing = 14 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var headerIcon = DashboardIcons.Create("Battery");
        header.Children.Add(headerIcon); header.Children.Add(Text(L.T("bat.header"), 20, true));
        stack.Children.Add(header);
        double? charge = current?.Percent;
        stack.Children.Add(DetailRow(L.T("bat.charge"), charge.HasValue ? charge.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : na));
        stack.Children.Add(DetailRow(L.T("bat.health"), Number(d?.HealthPercent, "%")));
        var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 76, 175, 80), Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(255, 0, 150, 136), Offset = 1 });
        stack.Children.Add(ProgressBarFor(d?.HealthPercent ?? 0, gradient, Dark ? Rgb(255, 255, 255, 0x22) : Rgb(0, 0, 0, 0x18)));
        stack.Children.Add(DetailRow(L.T("bat.design"), Number(d?.DesignWh, " Wh")));
        stack.Children.Add(DetailRow(L.T("bat.full"), Number(d?.FullWh, " Wh")));
        stack.Children.Add(DetailRow(L.T("bat.cycles"), d?.Cycles?.ToString(CultureInfo.InvariantCulture) ?? na));
        stack.Children.Add(DetailRow(L.T("bat.date"), d?.Manufactured?.ToString("M/d/yyyy", CultureInfo.InvariantCulture) ?? na));
        if (d != null && d.Problems.Length > 0 && !preview) stack.Children.Add(Text(string.Join("\n", d.Problems), 11, false, SecondaryText));
        return new ScrollViewer { Content = Card(stack, new Thickness(20, 18, 20, 20)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalAlignment = VerticalAlignment.Top };
    }

    // ---- warranty ----
    async Task LoadWarrantyAsync(bool force)
    {
        if (preview) return;
        if (!force && warranty == null) warranty = Details.ReadCachedWarranty();
        if (!force && warranty != null) { if (view == "warranty") body.Content = BuildWarranty(); return; }
        warrantyLoading = true; warrantyError = null;
        if (view == "warranty") body.Content = BuildWarranty();
        try { var fresh = await Details.FetchWarrantyAsync(); if (fresh != null) warranty = fresh; else warrantyError = L.T("war.failed"); }
        catch { warrantyError = L.T("war.failed"); }
        warrantyLoading = false;
        if (view == "warranty") body.Content = BuildWarranty();
    }

    UIElement BuildWarranty()
    {
        var green = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 175, 80));
        var red = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 86, 86));
        var stack = new StackPanel { Spacing = 14 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        bool? active = warranty?.End == null ? null : (warranty.End.Value.Date - DateTime.Today).Days > 0;
        var statusIcon = Glyph(active == true ? "\uE73E" : "\uE711", 20);
        statusIcon.Foreground = active == true ? green : active == false ? red : SecondaryText;
        if (active == false) statusIcon.Glyph = "\uEA39";
        left.Children.Add(statusIcon); left.Children.Add(Text(L.T("war.title"), 20, true));
        header.Children.Add(left);
        var refresh = IconButton("\uE72C", L.T("war.refresh"), () => _ = LoadWarrantyAsync(true));
        refresh.Width = 32; refresh.Height = 32; refresh.IsEnabled = !warrantyLoading;
        Grid.SetColumn(refresh, 1); header.Children.Add(refresh);
        stack.Children.Add(header);

        if (warrantyLoading) { stack.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center }); stack.Children.Add(Text(L.T("war.loading"), 12, false, SecondaryText)); }
        else if (warranty == null) stack.Children.Add(Text(warrantyError ?? L.T("war.failed"), 13, false, SecondaryText));
        else
        {
            int days = warranty.End.HasValue ? (warranty.End.Value.Date - DateTime.Today).Days : 0;
            stack.Children.Add(Text(days > 0 ? L.T("war.active") : L.T("war.expired"), 15, true, days > 0 ? green : red));
            double remaining = 0;
            if (days > 0 && warranty.Start.HasValue && warranty.End.HasValue)
            {
                double total = (warranty.End.Value - warranty.Start.Value).TotalDays, elapsed = (DateTime.Today - warranty.Start.Value).TotalDays;
                remaining = total > 0 ? Math.Clamp((1 - elapsed / total) * 100, 0, 100) : 100;
            }
            stack.Children.Add(ProgressBarFor(remaining, green, Dark ? Rgb(255, 255, 255, 0x22) : Rgb(0, 0, 0, 0x18)));
            string Date(DateTime? v) => v?.ToString("M/d/yyyy", CultureInfo.InvariantCulture) ?? "-";
            stack.Children.Add(DetailRow(L.T("war.start"), Date(warranty.Start)));
            stack.Children.Add(DetailRow(L.T("war.end"), Date(warranty.End)));
            stack.Children.Add(DetailRow(L.T("war.days"), days > 0 ? days.ToString(CultureInfo.InvariantCulture) : L.T("war.expired"), green));
            var support = new HyperlinkButton { Padding = new Thickness(0, 4, 0, 4) };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(Glyph("\uE8A7", 14)); content.Children.Add(new TextBlock { Text = L.T("war.support"), FontSize = 14 });
            support.Content = content;
            string url = warranty.Link ?? "https://pcsupport.lenovo.com/us/en/products/laptops-and-netbooks/legion-series/legion-5-15ith6h/82jh";
            support.Click += (_, _) => OpenUrl(url);
            stack.Children.Add(support);
        }
        return new ScrollViewer { Content = Card(stack, new Thickness(20, 18, 20, 18)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalAlignment = VerticalAlignment.Top };
    }

    // ---- capture (CI previews with fixture data) ----
    async Task SaveImage(string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
        using var stream = File.Open(path, FileMode.Create).AsRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    async Task Capture()
    {
        // Offline decision tests never download or launch an installer during captures.
        string fixture="""
        {"draft":false,"prerelease":false,"tag_name":"v9.0.0","assets":[{"name":"FluentVantageToolbar-v9.0.0-installer-win-x64.exe","browser_download_url":"https://github.com/StarsDust01/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe"}]}
        """;
        using(var test=System.Text.Json.JsonDocument.Parse(fixture)) {
            if(UpdateDownload(test.RootElement,new Version(1,0,0))==null || UpdateDownload(test.RootElement,new Version(9,0,0))!=null || UpdateDownload(test.RootElement,new Version(10,0,0))!=null)throw new InvalidOperationException("Update version comparison failed");
            if(!UpdateDownload(test.RootElement,new Version(1,0,0),false)!.EndsWith("-installer-win-x64.exe") || !UpdateDownload(test.RootElement,new Version(1,0,0),true)!.EndsWith("-installer-win-x64.exe"))throw new InvalidOperationException("Installed update flavor mismatch");
            if(!new AppSettings().AutoCheckUpdates)throw new InvalidOperationException("Automatic updates must default on");
        }
        foreach(var replacement in new[]{fixture.Replace("\"draft\":false","\"draft\":true"),fixture.Replace("\"prerelease\":false","\"prerelease\":true")}) {
            using var test=System.Text.Json.JsonDocument.Parse(replacement);
            if(UpdateDownload(test.RootElement,new Version(1,0,0))!=null)throw new InvalidOperationException("Non-stable update accepted");
        }
        if(LegionDevice.DecodeThermal(1)!=ThermalMode.Quiet || LegionDevice.DecodeThermal(2)!=ThermalMode.Balanced || LegionDevice.DecodeThermal(3)!=ThermalMode.Performance || LegionDevice.NextThermal(ThermalMode.Balanced)!=ThermalMode.Performance || LegionDevice.NextThermal(ThermalMode.Performance)!=ThermalMode.Quiet || LegionDevice.NextThermal(ThermalMode.Quiet)!=ThermalMode.Balanced)throw new InvalidOperationException("Thermal protocol mapping failed");
        bool unknownRejected=false;try{LegionDevice.DecodeThermal(0);}catch(InvalidOperationException){unknownRejected=true;}
        if(!unknownRejected)throw new InvalidOperationException("Unknown thermal mode accepted");
        // Deterministic render fixture, clearly labelled as preview. Hardware services and the network are never called.
        int index = Array.IndexOf(args, "--capture"); string path = args[index + 1];
        if(args.Contains("--tray-capture")) {
            // Real taskbar evidence: report the live shell icon anchor and the icon file this build chose for the current Windows taskbar theme.
            bool anchored=false;int anchorX=0,anchorY=0;
            for(int attempt=0;attempt<320 && !anchored;attempt++) {
                await Task.Delay(250);
                anchored=tray.TryGetAnchor(out anchorX,out anchorY);
                if(!anchored)tray.EnsureAdded();
                if(attempt%20==19)File.AppendAllText(path+".log",$"attempt {attempt} added={tray.IsAdded} ico={tray.IconLoaded} addErr={tray.LastAddError} modErr={tray.LastModifyError} anchored={anchored} light={TaskbarIsLight()} icon={Path.GetFileName(TrayIconPath())}\n");
            }
            if(!anchored)throw new InvalidOperationException("Tray icon anchor unavailable");
            bool light=TaskbarIsLight();
            string chosen=Path.GetFileName(TrayIconPath());
            if(light && chosen!="tray-dark.ico")throw new InvalidOperationException("Light taskbar must get the dark glyph");
            if(!light && chosen!="tray-white.ico")throw new InvalidOperationException("Dark taskbar must get the white glyph");
            tray.OnTrayButton = () => { try { File.WriteAllText(path+".interact","clicked"); } catch { } };
            File.WriteAllText(path+".anchor",$"{anchorX},{anchorY},{(light?"light":"dark")},{chosen}");
            File.WriteAllText(path+".ready","ready");
            for(int wait=0;wait<440 && !File.Exists(path+".done");wait++) {
                await Task.Delay(250);
                if(File.Exists(path+".query")) {
                    try { File.Delete(path+".query"); } catch { }
                    if(tray.TryGetAnchor(out int qx,out int qy))File.WriteAllText(path+".anchor2",$"{qx},{qy}");
                }
            }
            Close();return;
        }
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", name = Path.GetFileName(path);
        await geometryReady;await PresentPreparedAsync();SetCloaked(false);flyoutVisible=true;
        current = new DeviceState(60, true, false, ChargeMode.Conservation, false, false, "Visual fixture", [], true, 2, 165, [60, 165],ThermalMode.Balanced);
        batteryDetails = new BatteryDetails(99.9, 60.0, 59.9, 36, new DateTime(2022, 1, 22), []);
        warranty = new WarrantyResult(new DateTime(2022, 7, 29), new DateTime(2023, 7, 28), null);
        if(args.Contains("--updates-only")) {
            int stateIndex=Array.IndexOf(args,"--update-state");manualUpdateState=args[stateIndex+1];checkingUpdates=manualUpdateState=="checking";
            manualUpdateVersion="v9.0.0";manualUpdateUrl=manualUpdateState=="available" ? "https://github.com/StarsDust01/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe" : null;
            manualUpdateAsset=manualUpdateState=="available" ? new UpdateAsset(manualUpdateUrl!,"FluentVantageToolbar-v9.0.0-installer-win-x64.exe",0,null) : null;
            view=args.Contains("--updates-settings") ? "settings" : "about";Render();await geometryReady;
            if(body.Content is ScrollViewer updateScroll)updateScroll.ChangeView(null,args.Contains("--updates-top") ? 0 : updateScroll.ScrollableHeight,null,true);
            await Task.Delay(700);File.WriteAllText(path+".ready","ready");await Task.Delay(60000);Close();return;
        }
        foreach (var target in new[] { "main", "settings", "battery", "warranty", "about" })
        {
            view = target; Render(); await geometryReady;
            foreach (var button in tileButtons.Values) button.IsEnabled = true;
            await Task.Delay(target == "main" ? 2500 : 1200);
            await SaveImage(target == "main" ? path : Path.Combine(dir, target + "-" + name));
        }
        foreach(var state in new[] { "current", "available", "checking", "error" }) {
            manualUpdateState=state;checkingUpdates=state=="checking";manualUpdateVersion="v9.0.0";
            manualUpdateUrl=state=="available" ? "https://github.com/StarsDust01/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe" : null;
            manualUpdateAsset=state=="available" ? new UpdateAsset(manualUpdateUrl!,"FluentVantageToolbar-v9.0.0-installer-win-x64.exe",0,null) : null;
            view="about";Render();await geometryReady;
            if(body.Content is ScrollViewer updateScroll)updateScroll.ChangeView(null,updateScroll.ScrollableHeight,null,true);
            await Task.Delay(500);
            await SaveImage(Path.Combine(dir,"updates-"+state+"-"+name));
        }
        checkingUpdates=false;manualUpdateState="idle";manualUpdateUrl=null;manualUpdateAsset=null;
        // Verify dynamic height with a single tile row and then with no quick tiles.
        foreach (var count in new[] { 5, 0 }) {
            settings.HiddenTiles = Tiles.Skip(count).Select(t => t.Id).ToList();
            view = "main"; Render(); await geometryReady;
            foreach (var button in tileButtons.Values) button.IsEnabled = true;
            await Task.Delay(1200);
            await SaveImage(Path.Combine(dir, "tiles" + count + "-" + name));
        }
        settings.HiddenTiles=Tiles.Where(t=>t.Id!="thermal").Select(t=>t.Id).ToList();
        foreach(ThermalMode? mode in new ThermalMode?[]{ThermalMode.Quiet,ThermalMode.Balanced,ThermalMode.Performance,null}) {
            current=current! with { Thermal=mode };view="main";Render();await geometryReady;Apply();
            if(tileButtons.TryGetValue("thermal",out var thermal))thermal.IsEnabled=mode.HasValue;
            await Task.Delay(100);await SaveImage(Path.Combine(dir,$"thermal-{mode?.ToString() ?? "unavailable"}-"+name));
        }
        current=current! with { Thermal=ThermalMode.Balanced };
        // Every tile-row/link combination must fit without a main-page scroll host.
        foreach(var count in new[]{7,5,0}) foreach(var warrantyVisible in new[]{true,false}) foreach(var detailsVisible in new[]{true,false}) {
            settings.HiddenTiles=Tiles.Skip(count).Select(t=>t.Id).ToList();settings.ShowWarranty=warrantyVisible;settings.ShowBatteryDetails=detailsVisible;
            view="main";Render();await Task.Delay(400);
            Root.UpdateLayout();
            if(body.Content is not StackPanel main)throw new InvalidOperationException("Main page must not scroll");
            foreach(FrameworkElement child in main.Children) {
                var bottom=child.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0,child.ActualHeight)).Y;
                if(bottom>Root.ActualHeight+1 || child.ActualHeight+1<child.DesiredSize.Height)
                    throw new InvalidOperationException($"Main child clipped: tiles={count}, warranty={warrantyVisible}, details={detailsVisible}, bottom={bottom}, root={Root.ActualHeight}");
            }
            var lastChild=(FrameworkElement)main.Children.Last();
            var paintedHeight=await PaintedHeightAsync(lastChild);
            var paintedBottom=lastChild.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0,paintedHeight)).Y;
            if(Math.Abs(Root.ActualHeight-paintedBottom-24)>1)throw new InvalidOperationException($"Main painted gap mismatch: {Root.ActualHeight-paintedBottom}");
            var pt=lastChild.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0,0));
            File.AppendAllText(Path.Combine(dir,"layout-"+name+".txt"),$"tiles={count} warranty={warrantyVisible} details={detailsVisible} root={Root.ActualWidth}x{Root.ActualHeight} client={AppWindow.ClientSize.Width}x{AppWindow.ClientSize.Height} canvas={canvas.ActualHeight}/{canvas.DesiredSize.Height} title={titleBar.ActualHeight} body={body.ActualHeight} main={main.ActualHeight}/{main.DesiredSize.Height} inkH={paintedHeight} lastY={pt.Y} lastH={lastChild.ActualHeight}/{lastChild.DesiredSize.Height}\n");
            await SaveImage(Path.Combine(dir,$"main-{count}-warranty{warrantyVisible}-details{detailsVisible}-"+name));
        }
        // Model names are machine-specific and long names must fit the caption too.
        foreach(var model in new[]{"LOQ 15IRX9","Legion Pro 7 16IRX10H Long Device Model Name"}) {
            DeviceInfo.Override=model;settings.HiddenTiles=Tiles.Skip(5).Select(t=>t.Id).ToList();settings.ShowWarranty=false;view="main";Render();await geometryReady;await Task.Delay(400);
            if(titleBar.ActualHeight+1<titleBar.DesiredSize.Height)throw new InvalidOperationException("Dynamic model header clipped");
            await SaveImage(Path.Combine(dir,"model-"+(model.StartsWith("LOQ")?"loq":"long")+"-"+name));
        }
        DeviceInfo.Override="Legion 5 15ITH6H";
        settings.ShowWarranty=true;settings.ShowBatteryDetails=true;
        settings.HiddenTiles.Clear();
        // Low and critical battery colours.
        foreach (var level in new[] { 20, 5 })
        {
            view = "main"; current = current with { Percent = level, Plugged = false, Charging = false }; Render(); await geometryReady;
            foreach (var button in tileButtons.Values) button.IsEnabled = true;
            await Task.Delay(1200);
            await SaveImage(Path.Combine(dir, "level" + level + "-" + name));
        }
        var devicePreview=new DeviceDashboardWindow(settings.Theme,current,true);
        dashboard=devicePreview;view="device";Render();await geometryReady;
        await Task.Delay(1500);
        devicePreview.AssertPreviewReady();
        await SaveImage(Path.Combine(dir,"device-"+name));
        await devicePreview.PreviewPositionAsync(false,true);
        await SaveImage(Path.Combine(dir,"device-revealed-"+name));
        await devicePreview.PreviewPositionAsync(true);
        await SaveImage(Path.Combine(dir,"device-bottom-"+name));

        view="main";settings.HiddenTiles=Tiles.Skip(5).Select(t=>t.Id).ToList();
        foreach(bool off in new[]{false,true}) {
            current=current! with { Muted=off,TouchpadLocked=off };Render();await geometryReady;
            foreach(var button in tileButtons.Values)button.IsEnabled=true;
            await SaveImage(Path.Combine(dir,$"icons-{(off ? "off" : "on")}-{name}"));
        }
        foreach(string accent in new[]{"#00A6A6","#0078D4","#744DA9","#D83B01"}) {
            settings.AccentColor=accent;current=current! with { Percent=60 };view="main";Render();await geometryReady;
            if(BatteryColorFor(60)!=Windows.UI.Color.FromArgb(255,156,218,155) || BatteryColorFor(20)!=Windows.UI.Color.FromArgb(255,252,225,0) || BatteryColorFor(5)!=Windows.UI.Color.FromArgb(255,255,153,164))throw new InvalidOperationException("Battery accent or warning thresholds failed");
            var hoverTile=tileButtons["conserve"];hoverTile.IsEnabled=true;hoverTile.IsChecked=true;
            var normal=((SolidColorBrush)hoverTile.Resources["ToggleButtonBackgroundChecked"]).Color;
            var hover=((SolidColorBrush)hoverTile.Resources["ToggleButtonBackgroundCheckedPointerOver"]).Color;
            if(hover.R!=Math.Max(0,normal.R-16) || hover.G!=Math.Max(0,normal.G-13) || hover.B!=Math.Max(0,normal.B-16))throw new InvalidOperationException("Hover must preserve default tint steps for the current accent");
            VisualStateManager.GoToState(hoverTile,"Checked",false);await Task.Delay(100);
            await SaveImage(Path.Combine(dir,$"tile-accent-{accent[1..]}-{name}"));
            VisualStateManager.GoToState(hoverTile,"CheckedPointerOver",false);await Task.Delay(100);
            await SaveImage(Path.Combine(dir,$"tile-hover-{accent[1..]}-{name}"));
            settings.AccentColor=accent;view="settings";Render();await geometryReady;await Task.Delay(250);
            await SaveImage(Path.Combine(dir,$"accent-{accent[1..]}-{name}"));
        }
        if(body.Content is ScrollViewer settingsScroll) {
            settingsScroll.ChangeView(null,settingsScroll.ScrollableHeight,null,true);await Task.Delay(250);
            await SaveImage(Path.Combine(dir,"settings-bottom-"+name));
        }
        settings.AccentColor=AppSettings.DefaultAccentColor;ApplyAccent();
        if(ParseAccent("#12ABEF") is not {} parsed || parsed.R!=0x12 || ParseAccent("invalid")!=null)throw new InvalidOperationException("Accent parsing failed");
        var previewAsset=new UpdateAsset("https://github.com/StarsDust01/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe","FluentVantageToolbar-v9.0.0-installer-win-x64.exe",0,null);
        foreach(var toastState in new[]{"offer","downloading","ready","error"}) {
            using(var scope=new UpdateToastCaptureScope(new UpdateToast("9.0.0",previewAsset,"https://github.com/StarsDust01/Fluent-Vantage-Toolbar/releases/latest",Dark))) {
                scope.Window.ShowToast();scope.Window.PreviewState(toastState);await Task.Delay(400);
                var expectedAccent=ParseAccent(AppSettings.DefaultAccentColor)!.Value;
                if(toastState!="downloading" && scope.Window.PinnedAccentColor()!=expectedAccent)throw new InvalidOperationException("Toast accent button lost the current accent colour");
                await scope.Window.CaptureAsync(Path.Combine(dir,$"update-toast-{toastState}-"+name));
            }
        }
        if(args.Contains("--update-live")) {
            // Runs the real toast against the published release, so the download actually happens and the ready state is real.
            using var http=new System.Net.Http.HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentVantageToolbar-ci");
            using var liveJson=System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/StarsDust01/Fluent-Vantage-Toolbar/releases/latest"));
            var liveAsset=UpdateAssetFor(liveJson.RootElement,new Version(0,3,9),true);
            if(liveAsset==null)throw new InvalidOperationException("Live update preview found no published installer");
            var liveTag=(liveJson.RootElement.GetProperty("tag_name").GetString() ?? "v0.0.0").TrimStart('v','V');
            using(var scope=new UpdateToastCaptureScope(new UpdateToast(liveTag,liveAsset,"https://github.com/StarsDust01/Fluent-Vantage-Toolbar/releases/latest",Dark))) {
                scope.Window.ShowToast();await Task.Delay(400);
                await scope.Window.CaptureAsync(Path.Combine(dir,"update-toast-live-offer-"+name));
                scope.Window.Begin();
                var wait=0;
                while(!File.Exists(Path.Combine(UpdateInstaller.Dir,liveAsset.Name)) && wait<180000){await Task.Delay(1000);wait+=1000;}
                if(!File.Exists(Path.Combine(UpdateInstaller.Dir,liveAsset.Name)))throw new InvalidOperationException("Live update download did not finish");
                await Task.Delay(600);
                await scope.Window.CaptureAsync(Path.Combine(dir,"update-toast-live-ready-"+name));
            }
        }
        if(args.Contains("--interactive")) {
            settings.HiddenTiles=Tiles.Skip(5).Select(t=>t.Id).ToList();settings.ShowWarranty=true;
            view="main";Render();await geometryReady;
            var phases=Path.Combine(dir,"interactive-phases.txt");
            await VerifyPointerReorderAsync(dir);
            await Task.WhenAll(NavigateAsync("settings"),NavigateAsync("battery"),NavigateAsync("about"));
            await NavigateAsync("main");
            HideFlyout();
            for(int cycle=0;cycle<3;cycle++) {
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} open {cycle}\n");
                ShowFlyout("main",false);while(showingFlyout)await Task.Delay(10);await Task.Delay(500);
                int before=nativeResizeCount;
                for(int refresh=0;refresh<5;refresh++){Apply();await NextFrameAsync();}
                if(nativeResizeCount!=before)throw new InvalidOperationException("Unchanged refresh resized the window");
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} settings {cycle}\n");
                await NavigateAsync("settings");await Task.Delay(350);
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} main {cycle}\n");
                await NavigateAsync("main");await Task.Delay(350);
                foreach(string page in new[]{"battery","device","warranty","about"}) {
                    File.AppendAllText(phases,$"{DateTime.UtcNow:O} {page} {cycle}\n");
                    await NavigateAsync(page);await Task.Delay(350);
                    File.AppendAllText(phases,$"{DateTime.UtcNow:O} main-from-{page} {cycle}\n");
                    await NavigateAsync("main");await Task.Delay(350);
                }
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} hide {cycle}\n");
                HideFlyout();await Task.Delay(250);
            }
            for(int cycle=0;cycle<20;cycle++) {
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} reopen-stress {cycle}\n");
                ShowFlyout("main",false);while(showingFlyout)await Task.Delay(10);
                if(!flyoutVisible)throw new InvalidOperationException("Reopen was cancelled");
                await Task.Delay(180);HideFlyout();await Task.Delay(260);
            }
            File.AppendAllText(phases,$"PASS serialized geometry, three open/settings/main/hide cycles; unchanged refresh native resize count unchanged. Actual DPI={GetDpiForWindow(hwnd)}\n");
        }
        SetCloaked(true);Close();
    }
}

















internal sealed class UpdateToastCaptureScope : IDisposable
{
    public UpdateToast Window {get;}
    public UpdateToastCaptureScope(UpdateToast window){Window=window;}
    public void Dispose()=>Window.Close();
}
