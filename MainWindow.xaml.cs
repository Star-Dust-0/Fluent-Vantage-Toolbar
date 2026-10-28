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
        new("rapid", "tile.rapid", "\uE945"), new("touchpad", "tile.touchpad", "\uE7C9"),
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
    readonly Grid titleBar = new() { Height = 36 };
    readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Dictionary<string, ToggleButton> tileButtons = new();
    DeviceState? current;
    BatteryDetails? batteryDetails;
    WarrantyResult? warranty;
    string? warrantyError;
    bool warrantyLoading;
    bool busy, dialogOpen;
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
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr insertAfter,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd,IntPtr rect,IntPtr region,uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);

    public MainWindow(bool startHidden)
    {
        InitializeComponent();
        preview = args.Contains("--capture");
        settings = preview ? new AppSettings { Language = args.Contains("--arabic") ? "ar" : "en", Theme = args.Contains("--dark") ? "dark" : "light" } : AppSettings.Load();
        L.Ar = settings.Language == "ar";
        Title = "Fluent Legion Toolbar";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")); } catch { }
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false; presenter.IsAlwaysOnTop = true; }
        // Presenter flag setters may restore WS_BORDER after SetBorderAndTitleBar.
        var windowStyle=GetWindowLongPtr(hwnd,-16).ToInt64() & ~0x00C40000L;
        SetWindowLongPtr(hwnd,-16,new IntPtr(windowStyle));
        SetWindowPos(hwnd,IntPtr.Zero,0,0,0,0,0x0037);
        uint noBorder=0xFFFFFFFE;DwmSetWindowAttribute(hwnd,34,ref noBorder,4);
        HideFromTaskbar();
        double scale = GetDpiForWindow(hwnd) / 96d;
        AppWindow.Resize(new SizeInt32((int)(520 * scale), (int)(645 * scale)));

        Root.Padding = new Thickness(24, 8, 24, 20);
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.Children.Add(titleBar);
        Grid.SetRow(body, 1); Root.Children.Add(body);
        // Flyout uses a compact custom caption row; the gear and X share one baseline.
        if (preview)
        {
            Root.Width = 472; Root.Height = 610;
            AppWindow.Resize(new SizeInt32((int)(580 * scale), (int)(730 * scale)));
            Root.Background = new SolidColorBrush(Colors.Transparent);
        }
        // Paint the entire borderless client, including pixels allocated by resizing.
        Root.Background=new SolidColorBrush(Colors.Transparent);
        ApplyTheme();

        tray = new TrayIcon(hwnd, Path.Combine(AppContext.BaseDirectory, "app.ico"), TrayItems(), () => ShowFlyout(null, true), OnTrayMenu);
        tray.NativeTip = true; tray.SetTip("Fluent Legion Toolbar");
        Closed += (_, _) => { timer.Stop(); tray.Dispose(); };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated && !preview && !dialogOpen && !pageNavigating) HideFlyout(); };
        Root.ActualThemeChanged += (_, _) => { if (!preview) Render(); };
        timer.Tick += async (_, _) => { if (AppWindow.IsVisible && view == "main") await Refresh(); };
        Root.Loaded += async (_, _) => { if (preview) await Capture(); };

        Render();
        if (!preview)
        {
            timer.Start();
            _ = AutomaticUpdatesAsync(updateLifetime.Token);
            _ = Refresh();
            ListenForShowRequests();
            if (startHidden) AppWindow.Hide();
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

    void HideFlyout() { if (AppWindow.IsVisible) { lastHidden = DateTime.UtcNow; AppWindow.Hide(); } }

    async void ShowFlyout(string? target, bool toggle)
    {
        if (toggle && (AppWindow.IsVisible || (DateTime.UtcNow - lastHidden).TotalMilliseconds < 350)) { HideFlyout(); return; }
        view = target ?? "main";
        Render();
        await geometryReady;
        if (!AppWindow.IsVisible && view != (target ?? "main")) return;
        if (tray.TryGetAnchor(out int x, out int y))
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest).WorkArea;
            int left = Math.Clamp(x - AppWindow.Size.Width / 2, area.X, Math.Max(area.X, area.X + area.Width - AppWindow.Size.Width));
            int top = Math.Clamp(y - AppWindow.Size.Height - 16, area.Y, Math.Max(area.Y, area.Y + area.Height - AppWindow.Size.Height));
            AppWindow.Move(new PointInt32(left, top));
        }
        HideFromTaskbar();
        AppWindow.Show(); Activate(); SetForegroundWindow(hwnd);
        PositionTitleBar();
        _ = Refresh();
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

    (int, string)[] TrayItems() => [(1, L.T("menu.open")), (2, L.T("menu.settings")), (3, L.T("menu.about"))];

    void OnTrayMenu(int id) => ShowFlyout(id switch { 2 => "settings", 3 => "about", _ => "main" }, false);

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
        Root.Background=new SolidColorBrush(Colors.Transparent);
        Root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        Root.FlowDirection = L.Ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    static Windows.UI.Color? ParseAccent(string? hex)
    {
        if(hex?.Length!=7 || hex[0]!='#' || !uint.TryParse(hex[1..],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint rgb))return null;
        return Windows.UI.Color.FromArgb(255,(byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb);
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
        foreach(string hex in new[]{"#0078D4","#00A6A6","#744DA9","#D83B01","#C239B3","#107C10"}) {
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
        reset.Click+=(_,_)=>{settings.AccentColor=null;settings.Save();dashboard=null;Render();};
        actions.Children.Add(custom);actions.Children.Add(reset);stack.Children.Add(actions);
        stack.Children.Add(Text(settings.AccentColor ?? (L.Ar ? "لون Windows الافتراضي" : "Windows default accent"),12,false,SecondaryText));
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
        catch (Exception e) { current = await device.ReadAsync(); Apply(); if (statusText != null) statusText.Text = L.T("notconfirmed") + e.Message; }
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
            case "refresh": await Run(() => device.SetRefreshAsync(c?.RefreshHz == 144 ? 60 : 144)); break;
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
    static Windows.UI.Color BatteryColorFor(int? percent) => percent switch
    {
        null => Windows.UI.Color.FromArgb(255, 156, 218, 155),
        <= 5 => Windows.UI.Color.FromArgb(255, 255, 153, 164),   // Windows 11 critical
        <= 20 => Windows.UI.Color.FromArgb(255, 252, 225, 0),    // Windows 11 caution
        _ => Windows.UI.Color.FromArgb(255, 156, 218, 155),      // green sampled from the supplied Windows 11 tray battery
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
    Image? outgoingPage;

    Brush? navigationBackground;
    bool MotionEnabled => preview ? args.Contains("--interactive") : new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    async void Navigate(string target)
    {
        if(pageNavigating || target==view)return;
        pageNavigating=true;
        bool animate=AppWindow.IsVisible && MotionEnabled;
        try {
            await geometryReady;
            if(animate) {
                navigationBackground=Root.Background;
                Root.Background=new SolidColorBrush(Colors.Transparent);
                // Keep the outgoing page painted while the incoming page is arranged.
                var snapshot=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await snapshot.RenderAsync(canvas);
                outgoingPage=new Image { Source=snapshot,Width=canvas.ActualWidth,Height=canvas.ActualHeight,
                    Stretch=Stretch.Fill,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false };
                Root.Children.Add(outgoingPage);
            }
            view=target;
            Render(animate);
            await geometryReady;
            if(target=="battery" && !preview)_ = LoadBatteryAsync();
            if(target=="warranty" && !preview)_ = LoadWarrantyAsync(false);
        } catch(Exception error) { DiagnosticLog.Write("Page navigation failed: "+error); }
        finally {
            if(outgoingPage!=null){Root.Children.Remove(outgoingPage);outgoingPage=null;}
            canvas.Opacity=1;canvas.RenderTransform=new TranslateTransform();canvas.Clip=null;
            if(animate){await NextFrameAsync();Root.Background=navigationBackground;}
            canvas.Margin=new Thickness(0);SetWindowRgn(hwnd,IntPtr.Zero,true);
            pageNavigating=false;
        }
    }

    void Render(bool animatePage=false)
    {
        L.Ar = settings.Language == "ar";
        ApplyTheme();
        if (tray != null) tray.SetItems(TrayItems());
        titleBar.Children.Clear(); titleBar.ColumnDefinitions.Clear();
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string titleText = view switch { "settings" => L.T("settings.title"), "about" => L.T("about.title"), "battery" => L.T("bat.title"), "warranty" => L.T("war.title"), _ => L.T("title") };
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
        tileButtons.Clear();updateButtons.Clear();
        body.Content = view switch { "settings" => BuildSettings(), "about" => BuildAbout(), "battery" => BuildBattery(), "warranty" => BuildWarranty(), _ => BuildMain() };
        if (view == "main") Apply();
        if(animatePage){canvas.Opacity=0;canvas.RenderTransform=new TranslateTransform();}
        ResizeForContent(animatePage);
    }

    bool restoreAfterGeometry;
    Task geometryReady=Task.CompletedTask;
    readonly SemaphoreSlim geometryLock=new(1,1);
    int geometryGeneration;
    int nativeResizeCount;
    double frameWidthPixels=double.NaN,frameHeightPixels=double.NaN;

    void ResizeForContent(bool animatePage=false) => geometryReady=FitGeometryAsync(++geometryGeneration,animatePage);

    static Task NextFrameAsync()
    {
        var done=new TaskCompletionSource<bool>();
        EventHandler<object>? handler=null;
        handler=(_,_)=>{CompositionTarget.Rendering-=handler;done.TrySetResult(true);};
        CompositionTarget.Rendering+=handler;
        return done.Task;
    }

    async Task FitGeometryAsync(int generation,bool animatePage=false)
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
                // Window chrome is the difference between outer and client size. Never derive it from Root.ActualWidth:
                // at logon the cloaked window may not be laid out yet (ActualWidth 0), which made the "frame" equal to
                // the whole window and doubled the size to full screen.
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
            var work=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest).WorkArea;
            wanted=Math.Min(wanted,work.Height/scale-16);
            var desired=new SizeInt32((int)Math.Round(520*scale+frameWidthPixels),(int)Math.Round(wanted*scale+frameHeightPixels));
            desired=new SizeInt32(Math.Min(desired.Width,(int)Math.Round(520*scale+48*scale)),Math.Min(desired.Height,work.Height));
            if(animatePage) {
                // Anchor the native bottom edge. A single atomic rect update per frame
                // prevents a resize/move mismatch; the snapshot covers layout preparation.
                var pos=AppWindow.Position;var actual=AppWindow.Size;
                var visible=new RectInt32(pos.X,pos.Y,actual.Width,actual.Height);
                var start=new PointInt32(visible.X,visible.Y);var size=new SizeInt32(visible.Width,visible.Height);
                int bottom=start.Y+size.Height;
                int left=Math.Clamp(start.X+(size.Width-desired.Width)/2,work.X,Math.Max(work.X,work.X+work.Width-desired.Width));
                int top=Math.Clamp(bottom-desired.Height,work.Y,Math.Max(work.Y,work.Y+work.Height-desired.Height));
                await AnimatePageGeometryAsync(generation,new RectInt32(left,top,desired.Width,desired.Height));
            } else {
                // Hidden/static paths still make exactly one final size commit.
                if(AppWindow.Size.Width!=desired.Width || AppWindow.Size.Height!=desired.Height) {
                    AppWindow.Resize(desired);nativeResizeCount++;
                }
                await NextFrameAsync();Root.UpdateLayout();await NextFrameAsync();
                if(!preview && tray!=null && tray.TryGetAnchor(out int x,out int y)) {
                    var area=DisplayArea.GetFromPoint(new PointInt32(x,y),DisplayAreaFallback.Nearest).WorkArea;
                    int left=Math.Clamp(x-AppWindow.Size.Width/2,area.X,Math.Max(area.X,area.X+area.Width-AppWindow.Size.Width));
                    int top=Math.Clamp(y-AppWindow.Size.Height-16,area.Y,Math.Max(area.Y,area.Y+area.Height-AppWindow.Size.Height-12));
                    if(AppWindow.Position.X!=left || AppWindow.Position.Y!=top)AppWindow.Move(new PointInt32(left,top));
                }
            }
            if(restoreAfterGeometry && generation==geometryGeneration){restoreAfterGeometry=false;AppWindow.Show();}
        } finally {geometryLock.Release();}
    }

    async Task AnimatePageGeometryAsync(int generation,RectInt32 target)
    {
        var position=AppWindow.Position;var size=AppWindow.Size;
        var start=new RectInt32(position.X,position.Y,size.Width,size.Height);
        const int duration=300;
        var transform=new TranslateTransform { Y=96 };canvas.RenderTransform=transform;
        var motion=new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var ease=new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode=Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
        void Add(DependencyObject element,string property,double from,double to) {
            var animation=new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From=from,To=to,Duration=new Duration(TimeSpan.FromMilliseconds(duration)),EasingFunction=ease,EnableDependentAnimation=true };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation,element);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation,property);motion.Children.Add(animation);
        }
        Add(transform,"Y",96,0);Add(canvas,"Opacity",0,1);
        if(outgoingPage!=null)Add(outgoingPage,"Opacity",1,0);
        canvas.Margin=new Thickness(0);SetWindowRgn(hwnd,IntPtr.Zero,true);
        int bottom=start.Y+start.Height;
        var envelope=new RectInt32(target.X,Math.Min(start.Y,target.Y),target.Width,Math.Max(start.Height,target.Height));
        AppWindow.MoveAndResize(envelope);
        canvas.Margin=new Thickness(0,start.Y-envelope.Y,0,0);
        var initialRegion=CreateRectRgn(0,start.Y-envelope.Y,start.Width,bottom-envelope.Y);
        SetWindowRgn(hwnd,initialRegion,true);
        await NextFrameAsync();Root.UpdateLayout();await NextFrameAsync();
        motion.Begin();
        var clock=Stopwatch.StartNew();int steps=0;
        var trace=new List<string>();
        while(generation==geometryGeneration && AppWindow.IsVisible && !exiting) {
            double t=Math.Clamp(clock.Elapsed.TotalMilliseconds/duration,0,1);
            double eased=1-Math.Pow(1-t,3);
            int Mix(int a,int b)=>(int)Math.Round(a+(b-a)*eased);
            var rect=new RectInt32(Mix(start.X,target.X),Mix(start.Y,target.Y),Mix(start.Width,target.Width),Mix(start.Height,target.Height));
            if(t>0) {
                canvas.Margin=new Thickness(0,(rect.Y-envelope.Y)/(GetDpiForWindow(hwnd)/96d),0,0);
                var region=CreateRectRgn(0,rect.Y-envelope.Y,rect.Width,rect.Y+rect.Height-envelope.Y);
                SetWindowRgn(hwnd,region,true);steps++;
            }
            Root.UpdateLayout();
            Root.Clip=new RectangleGeometry { Rect=new Windows.Foundation.Rect(-Root.Padding.Left,-Root.Padding.Top,Root.ActualWidth,Root.ActualHeight) };
            trace.Add($"{clock.Elapsed.TotalMilliseconds:F1},{rect.X},{rect.Y},{rect.Width},{rect.Height},{rect.Y+rect.Height}");
            if(t>=1)break;
            await Task.Delay(10);await NextFrameAsync();
        }
        if(generation==geometryGeneration && AppWindow.IsVisible) {
            SetWindowPos(hwnd,IntPtr.Zero,target.X,target.Y,target.Width,target.Height,0x0104);nativeResizeCount++;
            canvas.Margin=new Thickness(0);SetWindowRgn(hwnd,IntPtr.Zero,true);
            Root.UpdateLayout();await NextFrameAsync();
        }
        motion.Stop();canvas.Opacity=1;transform.Y=0;

        DiagnosticLog.Write($"Page motion {view}: {start.Height}->{target.Height}; {steps} atomic geometry steps; {clock.ElapsedMilliseconds}ms; dpi={GetDpiForWindow(hwnd)}");
        if(preview && args.Contains("--interactive")) {
            var dir=Path.GetDirectoryName(args[Array.IndexOf(args,"--capture")+1])!;
            File.AppendAllLines(Path.Combine(dir,"navigation-geometry.csv"),new[]{$"PAGE,{view},{start.Height},{target.Height},{steps},{clock.ElapsedMilliseconds}"}.Concat(trace));
            if(start.Height!=target.Height && steps<3)throw new InvalidOperationException("Page size changed without intermediate animation frames");
            if(AppWindow.IsVisible && (AppWindow.Size.Height!=target.Height || AppWindow.Position.Y!=target.Y))throw new InvalidOperationException("Page animation missed its final bounds");
        }
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

        stack.Children.Add(HeaderRow(L.T("quick.header"), L.T("all.settings"), () => OpenUrl("ms-settings:")));
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
        stack.Children.Add(statusText);
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
        button.Resources["ToggleButtonBackgroundChecked"] = Rgb(0, 105, 112);
        button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = Rgb(0, 119, 126);
        button.Resources["ToggleButtonBackgroundCheckedPressed"] = Rgb(0, 89, 96);
        button.Resources["ToggleButtonForegroundChecked"] = Rgb(255, 255, 255);
        button.Resources["ToggleButtonForegroundCheckedPointerOver"] = Rgb(255, 255, 255);
        button.Resources["ToggleButtonForegroundCheckedPressed"] = Rgb(255, 255, 255);
        if (tile.Id == "fn") button.Content = MakeFnIcon();
        else button.Content = Glyph(tile.Glyph, 24);
        button.Click += (_, _) => OnTile(tile.Id);
        tileButtons[tile.Id] = button;
        var label = Text(L.T(tile.LabelKey), 11);
        label.TextAlignment = TextAlignment.Center; label.HorizontalAlignment = HorizontalAlignment.Center; label.MaxLines = 2;
        var panel = new StackPanel { Width = 86, Spacing = 8 };
        panel.Children.Add(button); panel.Children.Add(label);
        return panel;
    }

    Microsoft.UI.Xaml.Shapes.Line? fnSlash;
    UIElement MakeFnIcon()
    {
        // "FnLock" wordmark with a diagonal slash while Fn Lock is off.
        var grid = new Grid { Width = 44, Height = 32, IsHitTestVisible = false };
        var text = new TextBlock { Text = "FnLock", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fnSlash = new Microsoft.UI.Xaml.Shapes.Line { X1 = 5, Y1 = 3, X2 = 39, Y2 = 29, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        fnSlash.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, new Microsoft.UI.Xaml.Data.Binding { Source = text, Path = new PropertyPath("Foreground") });
        grid.Children.Add(text); grid.Children.Add(fnSlash);
        return grid;
    }

    void UpdateBatteryFill()
    {
        if (batteryFill == null || batteryFillHost == null) return;
        int? percent = current?.Percent;
        batteryColor = BatteryColorFor(percent);
        batteryFill.Background = new SolidColorBrush(batteryColor);
        batteryFill.Width = percent.HasValue ? Math.Max(0, batteryFillHost.ActualWidth * percent.Value / 100d) : 0;
        var onFill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 27, 27, 27));
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
            if (button.Content is FontIcon glyph) glyph.Foreground = state == true ? Rgb(255, 255, 255) : Primary;
            if (id == "fn" && button.Content is Grid lockGrid) foreach (var child in lockGrid.Children) {
                Brush ink = state == true ? Rgb(255, 255, 255) : Primary;
                if (child is Microsoft.UI.Xaml.Shapes.Path path) path.Stroke = ink;
                if (child is Border outline) outline.BorderBrush = ink;
                if (child is TextBlock fnLabel) fnLabel.Foreground = ink;
            }
            if (tip != null) ToolTipService.SetToolTip(button, tip);
        }
        Set("thermal",false,value.Thermal.HasValue,L.Ar ? "اضغط للتبديل بين المتوازن والأداء والهادئ" : "Click to cycle Balanced, Performance, Quiet");
        if(tileButtons.TryGetValue("thermal",out var thermalButton) && thermalButton.Parent is StackPanel thermalPanel && thermalPanel.Children[1] is TextBlock thermalLabel)
            thermalLabel.Text=value.Thermal switch { ThermalMode.Quiet=>L.Ar ? "هادئ" : "Quiet",ThermalMode.Balanced=>L.Ar ? "متوازن" : "Balanced",ThermalMode.Performance=>L.Ar ? "أداء" : "Performance",_=>L.Ar ? "غير متاح" : "Unavailable" };
        Set("fn", value.FnLocked, value.FnLocked.HasValue, L.T("tip.fn"));
        if (fnSlash != null) fnSlash.Visibility = value.FnLocked == true ? Visibility.Collapsed : Visibility.Visible;
        Set("usb", value.UsbMode == 2, value.UsbMode.HasValue, L.T("tip.usb"));
        bool exactRates = value.AvailableHz?.Contains(60) == true && value.AvailableHz.Contains(144);
        Set("refresh", value.RefreshHz == 144, exactRates, exactRates ? L.T("tip.refresh.ok") : L.T("tip.refresh.no"));
        Set("mic", value.Muted, value.Muted.HasValue, L.T("tip.mic"));
        Set("conserve", value.Mode == ChargeMode.Conservation, value.Mode.HasValue, L.T("tip.conserve"));
        Set("rapid", value.Mode == ChargeMode.Rapid, value.Mode.HasValue, L.T("tip.rapid"));
        Set("touchpad", value.TouchpadLocked, value.TouchpadLocked.HasValue, L.T("tip.touchpad"));
        if (tileButtons.TryGetValue("refresh", out var refreshButton) && refreshButton.Parent is StackPanel panel && panel.Children.Count > 1 && panel.Children[1] is TextBlock label)
            label.Text = value.RefreshHz.HasValue ? $"{value.RefreshHz} Hz" : "-- Hz";
        if (statusText != null) statusText.Text = preview ? L.T("preview") : value.Problems.Length > 0 ? L.T("status.some") : L.T("status.ok");
    }

    /// <summary>Windows taskbar theme ("Windows mode"), independent of the app's own theme setting. The white glyph vanishes on a light taskbar.</summary>
    internal static bool TaskbarIsLight()
    {
        try { return Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1; }
        catch { return false; }
    }
    internal static string TrayIconPath() => Path.Combine(AppContext.BaseDirectory, TaskbarIsLight() ? "tray-dark.ico" : "tray-white.ico");

    string UpdateLabel => L.Ar ? "التحقق من التحديثات" : "Check for updates";
    Button UpdateButton()
    {
        var button=new Button { Content=checkingUpdates ? (L.Ar ? "جارٍ التحقق..." : "Checking...") : UpdateLabel, IsEnabled=!checkingUpdates, HorizontalAlignment=HorizontalAlignment.Stretch };
        button.Click+=async (_,_)=>await CheckForUpdatesAsync();updateButtons.Add(button);return button;
    }
    static Version? ReleaseVersion(string? tag) => Version.TryParse(tag?.TrimStart('v','V'),out var version) ? version : null;
    static string? UpdateDownload(System.Text.Json.JsonElement release, Version installed,bool bundled=true)
    {
        if(release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())return null;
        var next=ReleaseVersion(release.GetProperty("tag_name").GetString());
        if(next==null || next<=installed)return null;
        foreach(var asset in release.GetProperty("assets").EnumerateArray()) {
            var name=asset.GetProperty("name").GetString();
            if(name!=$"FluentVantageToolbar-{next.ToString(3)}-Setup{(bundled ? "-with-dotnet" : "")}.exe")continue;
            var url=asset.GetProperty("browser_download_url").GetString();
            if(Uri.TryCreate(url,UriKind.Absolute,out var uri) && uri.Scheme=="https" && uri.Host=="github.com" && uri.AbsolutePath.StartsWith("/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/download/",StringComparison.Ordinal))return url;
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
            using var response=await http.GetAsync("https://api.github.com/repos/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/latest",updateLifetime.Token);
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
        updateToast=new UpdateToast(version.TrimStart('v','V'),asset,"https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/latest",Dark);
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
        updateToast=new UpdateToast((manualUpdateVersion ?? "").TrimStart('v','V'),manualUpdateAsset,"https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/latest",Dark);
        updateToast.ShowToast();
        updateToast.Begin();
    }

    async Task CheckForUpdatesAsync()
    {
        if(checkingUpdates || preview)return;
        checkingUpdates=true;
        foreach(var button in updateButtons){button.IsEnabled=false;button.Content=L.Ar ? "جارٍ التحقق..." : "Checking...";}
        string message;
        try {
            using var http=new System.Net.Http.HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentVantageToolbar/"+(typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response=await http.GetAsync("https://api.github.com/repos/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/latest");
            response.EnsureSuccessStatusCode();
            using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var installed=ReleaseVersion(typeof(MainWindow).Assembly.GetName().Version?.ToString(3)) ?? new Version(0,0,0);
            var url=UpdateDownload(json.RootElement,installed,InstalledBundled);
            if(url==null)message=L.Ar ? "أنت تستخدم أحدث إصدار." : "You're using the latest release.";
            else {
                Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
                message=L.Ar ? "تم فتح رابط تنزيل التحديث في المتصفح. بعد اكتمال التنزيل، شغّل المثبّت للتحديث." : "Opened the update download in your browser. When the download finishes, run the installer to update.";
            }
        } catch(Exception error) {
            DiagnosticLog.Write("Update check failed: "+error.Message);
            message=L.Ar ? "تعذّر التحقق من التحديثات أو فتح التنزيل. تحقق من اتصال الإنترنت وحاول مجدداً." : "Couldn't check for updates or open the download. Check your internet connection and try again.";
        } finally {
            checkingUpdates=false;foreach(var button in updateButtons){button.IsEnabled=true;button.Content=UpdateLabel;}
        }
        if(exiting)return;
        dialogOpen=true;
        try { await new ContentDialog { XamlRoot=Root.XamlRoot,Title=UpdateLabel,Content=message,CloseButtonText=L.Ar ? "إغلاق" : "Close" }.ShowAsync(); }
        finally {dialogOpen=false;}
    }

    // ---- settings ----
    UIElement BuildSettings()
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Text(L.T("settings.language"), 14, true));
        var language = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        language.Items.Add("العربية"); language.Items.Add("English");
        language.SelectedIndex = settings.Language == "ar" ? 0 : 1;
        language.SelectionChanged += (_, _) => { string chosen = language.SelectedIndex == 0 ? "ar" : "en"; if (chosen == settings.Language) return; settings.Language = chosen; settings.Save(); Render(); };
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

        var exit = new Button { Content = L.T("settings.exit"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
        exit.Click += (_, _) => Close();
        stack.Children.Add(exit);
        stack.Children.Add(Text(L.Ar ? "التحديثات" : "Updates",14,true));
        stack.Children.Add(ToggleRow(L.Ar ? "البحث التلقائي عن التحديثات" : "Automatically check for updates",settings.AutoCheckUpdates,on=>{settings.AutoCheckUpdates=on;settings.Save();}));
        stack.Children.Add(Text(L.Ar ? "بعد 45 ثانية من التشغيل، ثم كل 6 ساعات. التنزيل والتثبيت بقرارك." : "45 seconds after launch, then every 6 hours. Download and install only when you choose.",12,false,SecondaryText));
        stack.Children.Add(UpdateButton());
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
        var name = Text("Fluent Legion Toolbar", 22, true); name.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(name);
        var version = Text(L.T("about.version") + " " + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.2.0"), 13, false, SecondaryText); version.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(version);
        var dev = Text(L.T("about.developer"), 14); dev.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(dev);
        var credit = Text(L.T("about.credit"), 13, false, SecondaryText); credit.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(credit);
        var note = Text(L.T("about.note"), 12, false, SecondaryText); note.TextAlignment = TextAlignment.Center; note.Margin = new Thickness(8, 12, 8, 0); stack.Children.Add(note);
        var repo=new HyperlinkButton { Content="GitHub Repo Link",NavigateUri=new Uri("https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar"),HorizontalAlignment=HorizontalAlignment.Center };
        stack.Children.Add(repo);
        stack.Children.Add(UpdateButton());
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
        var headerIcon = Glyph("\uE83F", 22); headerIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 218, 155));
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
        {"draft":false,"prerelease":false,"tag_name":"v9.0.0","assets":[{"name":"FluentVantageToolbar-9.0.0-Setup.exe","browser_download_url":"https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-9.0.0-Setup.exe"},{"name":"FluentVantageToolbar-9.0.0-Setup-with-dotnet.exe","browser_download_url":"https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-9.0.0-Setup-with-dotnet.exe"}]}
        """;
        using(var test=System.Text.Json.JsonDocument.Parse(fixture)) {
            if(UpdateDownload(test.RootElement,new Version(1,0,0))==null || UpdateDownload(test.RootElement,new Version(9,0,0))!=null || UpdateDownload(test.RootElement,new Version(10,0,0))!=null)throw new InvalidOperationException("Update version comparison failed");
            if(!UpdateDownload(test.RootElement,new Version(1,0,0),false)!.EndsWith("-Setup.exe") || !UpdateDownload(test.RootElement,new Version(1,0,0),true)!.EndsWith("-Setup-with-dotnet.exe"))throw new InvalidOperationException("Installed update flavor mismatch");
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
            for(int attempt=0;attempt<60 && !anchored;attempt++){await Task.Delay(250);anchored=tray.TryGetAnchor(out anchorX,out anchorY);}
            if(!anchored)throw new InvalidOperationException("Tray icon anchor unavailable");
            bool light=TaskbarIsLight();
            string chosen=Path.GetFileName(TrayIconPath());
            if(light && chosen!="tray-dark.ico")throw new InvalidOperationException("Light taskbar must get the dark glyph");
            if(!light && chosen!="tray-white.ico")throw new InvalidOperationException("Dark taskbar must get the white glyph");
            File.WriteAllText(path+".anchor",$"{anchorX},{anchorY},{(light?"light":"dark")},{chosen}");
            File.WriteAllText(path+".ready","ready");
            await Task.Delay(60000);Close();return;
        }
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", name = Path.GetFileName(path);
        current = new DeviceState(60, true, false, ChargeMode.Conservation, false, false, "Visual fixture", [], false, 2, 144, [60, 144]);
        batteryDetails = new BatteryDetails(99.9, 60.0, 59.9, 36, new DateTime(2022, 1, 22), []);
        warranty = new WarrantyResult(new DateTime(2022, 7, 29), new DateTime(2023, 7, 28), null);
        foreach (var target in new[] { "main", "settings", "battery", "warranty", "about" })
        {
            view = target; Render(); await geometryReady;
            foreach (var button in tileButtons.Values) button.IsEnabled = true;
            await Task.Delay(target == "main" ? 2500 : 1200);
            await SaveImage(target == "main" ? path : Path.Combine(dir, target + "-" + name));
        }
        // Verify dynamic height with a single tile row and then with no quick tiles.
        foreach (var count in new[] { 5, 0 }) {
            settings.HiddenTiles = Tiles.Skip(count).Select(t => t.Id).ToList();
            view = "main"; Render(); await geometryReady;
            foreach (var button in tileButtons.Values) button.IsEnabled = true;
            await Task.Delay(1200);
            await SaveImage(Path.Combine(dir, "tiles" + count + "-" + name));
        }
        // Every tile-row/link combination must fit without a main-page scroll host.
        foreach(var count in new[]{7,5,0}) foreach(var warrantyVisible in new[]{true,false}) foreach(var detailsVisible in new[]{true,false}) {
            settings.HiddenTiles=Tiles.Skip(count).Select(t=>t.Id).ToList();settings.ShowWarranty=warrantyVisible;settings.ShowBatteryDetails=detailsVisible;
            view="main";Render();await geometryReady;await Task.Delay(400);
            if(body.Content is not FrameworkElement main || main.ActualHeight+1<main.DesiredSize.Height)throw new InvalidOperationException("Main panel is clipped for tile/link combination");
            await SaveImage(Path.Combine(dir,$"main-{count}-warranty{warrantyVisible}-details{detailsVisible}-"+name));
        }
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
        foreach(string accent in new[]{"#0078D4","#744DA9"}) {
            settings.AccentColor=accent;view="settings";Render();await geometryReady;await Task.Delay(250);
            await SaveImage(Path.Combine(dir,$"accent-{accent[1..]}-{name}"));
        }
        settings.AccentColor=null;ApplyAccent();
        if(ParseAccent("#12ABEF") is not {} parsed || parsed.R!=0x12 || ParseAccent("invalid")!=null)throw new InvalidOperationException("Accent parsing failed");
        var previewAsset=new UpdateAsset("https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe","FluentVantageToolbar-v9.0.0-installer-win-x64.exe",0,null);
        foreach(var toastState in new[]{"offer","downloading","ready","error"}) {
            using(var scope=new UpdateToastCaptureScope(new UpdateToast("9.0.0",previewAsset,"https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/latest",Dark))) {
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
            using var liveJson=System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/latest"));
            var liveAsset=UpdateAssetFor(liveJson.RootElement,new Version(0,3,9),true);
            if(liveAsset==null)throw new InvalidOperationException("Live update preview found no published installer");
            var liveTag=(liveJson.RootElement.GetProperty("tag_name").GetString() ?? "v0.0.0").TrimStart('v','V');
            using(var scope=new UpdateToastCaptureScope(new UpdateToast(liveTag,liveAsset,"https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/latest",Dark))) {
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
            AppWindow.Hide();
            for(int cycle=0;cycle<3;cycle++) {
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} open {cycle}\n");
                ShowFlyout("main",false);await geometryReady;await Task.Delay(500);
                int before=nativeResizeCount;
                for(int refresh=0;refresh<5;refresh++){Apply();await NextFrameAsync();}
                if(nativeResizeCount!=before)throw new InvalidOperationException("Unchanged refresh resized the window");
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} settings {cycle}\n");
                Navigate("settings");await geometryReady;await Task.Delay(300);
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} main {cycle}\n");
                Navigate("main");await geometryReady;await Task.Delay(350);
                foreach(string page in new[]{"battery","device","warranty","about"}) {
                    File.AppendAllText(phases,$"{DateTime.UtcNow:O} {page} {cycle}\n");
                    Navigate(page);await geometryReady;await Task.Delay(350);
                    File.AppendAllText(phases,$"{DateTime.UtcNow:O} main-from-{page} {cycle}\n");
                    Navigate("main");await geometryReady;await Task.Delay(350);
                }
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} hide {cycle}\n");
                HideFlyout();await Task.Delay(250);
            }
            File.AppendAllText(phases,$"PASS serialized geometry, three open/settings/main/hide cycles; unchanged refresh native resize count unchanged. Actual DPI={GetDpiForWindow(hwnd)}\n");
        }
        Close();
    }
}



internal sealed class UpdateToastCaptureScope : IDisposable
{
    public UpdateToast Window {get;}
    public UpdateToastCaptureScope(UpdateToast window){Window=window;}
    public void Dispose()=>Window.Close();
}
