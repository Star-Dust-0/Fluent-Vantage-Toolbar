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
    async void Navigate(string target) { try { await NavigateAsync(target); } catch (Exception ex) { DiagnosticLog.Write("Navigate " + target + " failed: " + ex); } }
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
        // Same text drawn a second time in the normal text colour. The white copy is clipped to the filled part of the bar and
        // the dark copy to the empty part, so the text stays readable where it crosses the edge of the fill.
        plugIconDark = Glyph("\uE945", 28);
        percentTextDark = Text("--%", 44, true);
        percentTextDark.TextWrapping = TextWrapping.NoWrap;
        batteryCentreDark = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        batteryCentreDark.Children.Add(plugIconDark); batteryCentreDark.Children.Add(percentTextDark);
        inner.Children.Add(batteryCentreDark);
        batteryCentre = centre;
        centre.SizeChanged += (_, _) => UpdateBatteryFill();
        inner.SizeChanged += (_, _) => UpdateBatteryFill();
        outline.Child = inner;
        graphic.Children.Add(outline);
        var cap = new Border { Background = SecondaryText, Height = 30, CornerRadius = new CornerRadius(0, 3, 3, 0) };
        Grid.SetColumn(cap, 1); graphic.Children.Add(cap);
        batteryFillHost = inner;
        stack.Children.Add(graphic);
        chargeText = Text("", 12, false, SecondaryText);
        stack.Children.Add(chargeText);
        // Hardware access needs administrator rights; without them every control fails, so say so instead of failing silently.
        if (preview ? args.Contains("--admin-banner") : !Startup.IsAdmin())
            stack.Children.Add(Text(L.T("Hardware controls need administrator rights. Reinstall with \"Start with Windows\" ticked, or run the app as administrator.", "تحتاج عناصر التحكم في العتاد إلى صلاحيات المسؤول. أعد التثبيت مع تحديد \"البدء مع ويندوز\"، أو شغّل البرنامج بصلاحيات المسؤول."), 12, false, SecondaryText));

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

    void OpenDashboard(){dashboard=new DeviceDashboardWindow(settings.Theme,current,preview);Navigate("device");}

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
        var repo = new HyperlinkButton { Content="GitHub Repo Link", NavigateUri=new Uri("https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar"), HorizontalAlignment=HorizontalAlignment.Center }; stack.Children.Add(repo);
        stack.Children.Add(UpdateCard(true));
        return new ScrollViewer { Content=stack, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
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
    }}
