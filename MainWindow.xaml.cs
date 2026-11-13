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
    TextBlock? percentText, chargeText, statusText, percentTextDark;
    FontIcon? plugIconDark;
    StackPanel? batteryCentre, batteryCentreDark;
    FontIcon? plugIcon;
    Border? batteryFill; Grid? batteryFillHost;
    Windows.UI.Color batteryColor = Windows.UI.Color.FromArgb(255, 156, 218, 155);

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
        Root.Loaded += async (_, _) => { if (preview) { await Capture(); } };

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
        if (hex == null || hex.Length != 7 || hex[0] != '#') return null;
        var digits = hex.Substring(1, hex.Length - 1);
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) return null;
        return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
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

    // Only web links and the app's own diagnostic log are handed to the shell; anything else is ignored.
    static void OpenUrl(string url)
    {
        try
        {
            bool web = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
            bool log = string.Equals(url, DiagnosticLog.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(url);
            if (web || log) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        } catch { }
    }

    // ---- colours ----
    bool Dark => Root.ActualTheme == ElementTheme.Dark;
    static SolidColorBrush Rgb(byte r, byte g, byte b, byte a = 255) => new(Windows.UI.Color.FromArgb(a, r, g, b));
    Brush SecondaryText => Dark ? Rgb(255, 255, 255, 0xC5) : Rgb(0, 0, 0, 0x9E);
    Brush CardBackground => Dark ? Rgb(255, 255, 255, 0x0D) : Rgb(255, 255, 255, 0xB3);
    Brush CardStroke => Dark ? Rgb(255, 255, 255, 0x29) : Rgb(0, 0, 0, 0x0F);
    Brush Primary => Dark ? Rgb(255, 255, 255) : Rgb(0, 0, 0, 0xE4);

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
        var checkedInk=ParseAccent(settings.AccentColor).HasValue && settings.AccentColor.Equals("#9CDA9B",StringComparison.OrdinalIgnoreCase) ? Rgb(20,55,27) : Rgb(255,255,255);
        if(tileButtons.TryGetValue("mic",out var microphone))microphone.Content=value.Muted==true ? MicOffIcon(checkedInk) : Glyph("\uE720",24);
        if(tileButtons.TryGetValue("touchpad",out var pad))pad.Content=TouchpadIcon(value.TouchpadLocked==true,value.TouchpadLocked==false ? checkedInk : Primary);
        if (tileButtons.TryGetValue("refresh", out var refreshButton) && refreshButton.Parent is StackPanel panel && panel.Children.Count > 1 && panel.Children[1] is TextBlock label)
            label.Text = exactRates ? $"{panelRates[0]} / {panelRates[^1]} Hz" : value.RefreshHz.HasValue ? $"{value.RefreshHz} Hz" : "-- Hz";
        if (statusText != null) statusText.Text = preview ? L.T("preview") : value.Problems.Length > 0 ? L.T("status.some") : L.T("status.ok");
    }

    // Drag state is shared; the window-level pointer handlers are added once, so rebuilding the rows no longer piles up handlers.
    bool dragging, reorderHandlersAttached;
    double dragStartY;
    TranslateTransform? dragShift;
    Grid? dragRow;
    StackPanel? dragRows;
}

















internal sealed class UpdateToastCaptureScope : IDisposable
{
    public UpdateToast Window {get;}
    public UpdateToastCaptureScope(UpdateToast window){Window=window;}
    public void Dispose()=>Window.Close();
}
