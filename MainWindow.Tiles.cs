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

    sealed record TileDef(string Id, string LabelKey, string Glyph);
    static readonly TileDef[] Tiles =
    [
        new("thermal", "tile.thermal", "\uE713"), new("mic", "tile.mic", "\uE720"), new("conserve", "tile.conserve", "\uEA93"),
        new("rapid", "tile.rapid", "\uE945"), new("touchpad", "tile.touchpad", "\uEFA5"),
        new("fn", "tile.fn", ""), new("refresh", "tile.refresh", "\uE7F4"), new("usb", "tile.usb", "\uE88E"),
    ];

    IEnumerable<TileDef> OrderedTiles() => settings.TileOrder.Distinct().Select(id=>Tiles.FirstOrDefault(t=>t.Id==id)).Where(t=>t!=null).Cast<TileDef>().Concat(Tiles.Where(t=>!settings.TileOrder.Contains(t.Id)));

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
        catch (Exception e)
        {
            DiagnosticLog.Write("Control failed: " + e);
            try { current = await device.ReadAsync(); Apply(); } catch (Exception again) { DiagnosticLog.Write("Re-read after failure also failed: " + again.Message); }
            bool refused = e is DeviceMessageException;
            dialogOpen = true;
            try
            {
                await new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = refused ? L.T("Can't change this setting", "تعذّر تغيير هذا الإعداد") : L.T("notconfirmed"),
                    Content = refused ? L.T(((DeviceMessageException)e).English, ((DeviceMessageException)e).Arabic) : (L.Ar ? "لم يتأكد تغيير الإعداد. أعد المحاولة، وإذا استمرت المشكلة افتح سجل التشخيص من الإعدادات وأرسله للدعم." : "The setting change could not be confirmed. Try again. If it continues, open the diagnostic log in Settings and send it to support."),
                    CloseButtonText = "OK"
                }.ShowAsync();
            }
            catch (Exception dialogError) { DiagnosticLog.Write("Dialog failed: " + dialogError.Message); }
            finally { dialogOpen = false; }
        }
        finally { busy = false; }
    }

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
        try { await OnTileAsync(id); } catch (Exception ex) { DiagnosticLog.Write("Tile " + id + " failed: " + ex); }
    }
    async Task OnTileAsync(string id)
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
    }}
