using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace FluentLegionToolbar;

/// <summary>Acrylic backdrop that stays "active" even though the window never takes focus.</summary>
sealed class ActiveAcrylic : SystemBackdrop
{
    Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController? _c;
    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _c = new Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController();
        var cfg = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        cfg.IsInputActive = true;
        _c.SetSystemBackdropConfiguration(cfg);
        _c.AddSystemBackdropTarget(target);
    }
    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _c?.RemoveSystemBackdropTarget(target);
        _c?.Dispose(); _c = null;
    }
}

/// <summary>Fluent toast-style notification shown above the taskbar when a newer release exists. The button downloads the installer inside the app, checks it, and runs it only after the user presses Install now.</summary>
sealed class UpdateToast : Window
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    readonly IntPtr _hwnd;
    readonly Grid _root = new();
    readonly string _version, _fallback;
    readonly UpdateAsset _asset;
    readonly TextBlock _title = new() { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _sub;
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    readonly StackPanel _btns = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    CancellationTokenSource? _cts;
    string? _file;

    static TextBlock Secondary(TextBlock source)
    {
        var themed = (TextBlock)Microsoft.UI.Xaml.Markup.XamlReader.Load("<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/>");
        themed.Text = source.Text; themed.FontSize = source.FontSize; themed.TextWrapping = source.TextWrapping;
        return themed;
    }

    /// <summary>Removes the thin light border Windows 11 draws around borderless WinUI windows.</summary>
    void ApplyChrome()
    {
        try
        {
            long style = GetWindowLongPtr(_hwnd, -16).ToInt64() & ~0x00C70000L; // WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME
            SetWindowLongPtr(_hwnd, -16, new IntPtr(style));
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x2 | 0x1 | 0x4 | 0x10 | 0x20);
        }
        catch { }
        try
        {
            int none = unchecked((int)0xFFFFFFFE), round = 2;
            DwmSetWindowAttribute(_hwnd, 34, ref none, 4);   // DWMWA_BORDER_COLOR = none
            DwmSetWindowAttribute(_hwnd, 33, ref round, 4);  // DWMWA_WINDOW_CORNER_PREFERENCE = round
        }
        catch { }
    }

    public UpdateToast(string version, UpdateAsset asset, string fallbackUrl, bool dark)
    {
        _version = version; _asset = asset; _fallback = fallbackUrl;
        _sub = Secondary(new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap });
        Title = "Fluent Vantage Toolbar Update";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        }
        try { AppWindow.IsShownInSwitchers = false; } catch { }
        try { SystemBackdrop = new ActiveAcrylic(); } catch { }
        long ex = (long)GetWindowLongPtr(_hwnd, -20);
        SetWindowLongPtr(_hwnd, -20, (IntPtr)(ex | 0x80 | 0x08000000 | 0x8));
        ApplyChrome();

        var tint = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource SolidBackgroundFillColorBaseBrush}' Opacity='0.35'/>");
        var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), VerticalAlignment = VerticalAlignment.Top };
        icon.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        icon.Child = new FontIcon { Glyph = "\uE896", FontSize = 18, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var texts = new StackPanel { Spacing = 2, MaxWidth = 260 };
        texts.Children.Add(_title); texts.Children.Add(_sub);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(icon); head.Children.Add(texts);

        var st = new StackPanel { Spacing = 12, Padding = new Thickness(18, 16, 18, 16), VerticalAlignment = VerticalAlignment.Center };
        st.Children.Add(head); st.Children.Add(_bar); st.Children.Add(_btns);
        _root.Children.Add(tint); _root.Children.Add(st);
        _root.FlowDirection = L.Ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Content = _root;
        Closed += (a, b) => { try { _cts?.Cancel(); } catch { } };
        SetState("offer");
    }

    Button AddButton(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text, MinWidth = 96 };
        if (accent)
        {
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            // Pin the accent on the element itself: the shared accent style's ThemeResource
            // values do not reliably re-resolve app-level overrides in a new window.
            var brush = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            var c = brush.Color;
            var hover = new SolidColorBrush(Scale(c, 0.88));
            var pressed = new SolidColorBrush(Scale(c, 0.76));
            var fg = new SolidColorBrush(Microsoft.UI.Colors.White);
            button.Resources["AccentButtonBackground"] = brush;
            button.Resources["AccentButtonBackgroundPointerOver"] = hover;
            button.Resources["AccentButtonBackgroundPressed"] = pressed;
            button.Resources["AccentButtonForeground"] = fg;
            button.Resources["AccentButtonForegroundPointerOver"] = fg;
            button.Resources["AccentButtonForegroundPressed"] = fg;
            button.Resources["AccentButtonBorderBrush"] = brush;
            button.Resources["AccentButtonBorderBrushPointerOver"] = hover;
            button.Resources["AccentButtonBorderBrushPressed"] = pressed;
            button.Tag = "accent";
        }
        button.Click += (a, b) => click();
        _btns.Children.Add(button);
        return button;
    }

    static Windows.UI.Color Scale(Windows.UI.Color c, double f) =>
        Windows.UI.Color.FromArgb(255, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    public Windows.UI.Color? PinnedAccentColor()
    {
        foreach (var child in _btns.Children)
            if (child is Button { Tag: "accent" } b && b.Resources.TryGetValue("AccentButtonBackground", out var v) && v is SolidColorBrush sb)
                return sb.Color;
        return null;
    }

    void SetState(string state, string? detail = null)
    {
        _btns.Children.Clear();
        _bar.Visibility = state == "downloading" ? Visibility.Visible : Visibility.Collapsed;
        switch (state)
        {
            case "downloading":
                _title.Text = L.T("Downloading the update", "جارٍ تنزيل التحديث");
                _sub.Text = "v" + _version + "  0%";
                _bar.Value = 0;
                AddButton(L.T("Cancel", "إلغاء"), CancelDownload);
                break;
            case "ready":
                _title.Text = L.T("Downloaded and verified", "تم التنزيل والتحقق من الملف");
                _sub.Text = L.T("The app will close and the installer will finish the update.", "سيُغلق التطبيق ويكمل المثبّت التحديث.");
                AddButton(L.T("Install now", "تثبيت الآن"), InstallNow, true);
                AddButton(L.T("Later", "لاحقًا"), () => Close());
                break;
            case "error":
                _title.Text = L.T("Couldn't download the update", "تعذر تنزيل التحديث");
                _sub.Text = detail ?? "";
                AddButton(L.T("Retry", "إعادة المحاولة"), Begin, true);
                AddButton(L.T("Open in browser", "فتح في المتصفح"), OpenInBrowser);
                break;
            default:
                _title.Text = L.T("New update available", "تحديث جديد متاح");
                _sub.Text = L.T("Version ", "الإصدار ") + _version + L.T(" is ready to download", " جاهز للتنزيل");
                AddButton(L.T("Download and install", "تنزيل وتثبيت"), Begin, true);
                AddButton(L.T("Later", "لاحقًا"), () => Close());
                break;
        }
    }

    /// <summary>Screenshot previews only: shows a state without downloading anything.</summary>
    public void PreviewState(string state)
    {
        SetState(state, state == "error" ? L.T("The download didn't finish. Check your internet connection and try again.", "لم يكتمل التنزيل. تحقق من اتصالك بالإنترنت وحاول مرة أخرى.") : null);
        if (state == "downloading") { _bar.Value = 46; _sub.Text = "v" + _version + "  46%"; }
    }

    void OpenInBrowser()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_fallback) { UseShellExecute = true }); } catch { }
        Close();
    }

    void CancelDownload() { try { _cts?.Cancel(); } catch { } }

    /// <summary>Starts the download after the user pressed the download button. The installer is not started until "Install now" is pressed.</summary>
    public async void Begin()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        SetState("downloading");
        try
        {
            _file = await UpdateInstaller.DownloadAsync(_asset, pct => { _bar.Value = pct; _sub.Text = "v" + _version + "  " + (int)pct + "%"; }, cts.Token);
            _cts = null;
            SetState("ready");
        }
        catch (OperationCanceledException)
        {
            _cts = null;
            SetState("offer");
        }
        catch (Exception error)
        {
            _cts = null;
            try { Directory.CreateDirectory(AppSettings.Folder); File.AppendAllText(Path.Combine(AppSettings.Folder, "update.log"), DateTime.Now + " download: " + error.Message + "\n"); } catch { }
            SetState("error", error is InvalidDataException ? L.T("The downloaded file failed verification. Nothing was run.", "فشل التحقق من الملف المنزّل. لم يتم تشغيل أي شيء.") : L.T("The download didn't finish. Check your internet connection and try again.", "لم يكتمل التنزيل. تحقق من اتصالك بالإنترنت وحاول مرة أخرى."));
        }
    }

    async void InstallNow()
    {
        try
        {
            var file = _file;
            if (file == null || !File.Exists(file)) throw new FileNotFoundException();
            await Task.Run(() => UpdateInstaller.Verify(file, _asset));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
            Close();
        }
        catch (Exception error)
        {
            try { Directory.CreateDirectory(AppSettings.Folder); File.AppendAllText(Path.Combine(AppSettings.Folder, "update.log"), DateTime.Now + " install: " + error.Message + "\n"); } catch { }
            _cts = null;
            SetState("error", L.T("The downloaded file failed verification. Nothing was run.", "فشل التحقق من الملف المنزّل. لم يتم تشغيل أي شيء."));
        }
    }

    internal async Task CaptureAsync(string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(_root);
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
        using var stream = File.Open(path, FileMode.Create).AsRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }



    public void ShowToast()
    {
        var wa = DisplayArea.Primary.WorkArea;
        double dpi = GetDpiForWindow(_hwnd) / 96.0;
        if (dpi < 1) dpi = 1;
        int w = (int)(380 * dpi), h = (int)(190 * dpi);
        var rect = new RectInt32(wa.X + wa.Width - w - (int)(12 * dpi), wa.Y + wa.Height - h - (int)(12 * dpi), w, h);
        AppWindow.MoveAndResize(rect);
        ShowWindow(_hwnd, 4);
        AppWindow.MoveAndResize(rect);
        ApplyChrome();
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x2 | 0x1 | 0x10);
        _root.InvalidateMeasure(); _root.UpdateLayout();
    }
}
