using System.Globalization;
using System.Runtime.InteropServices;
using FluentLegionToolbar.Hardware;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
namespace FluentLegionToolbar;

internal sealed class DeviceDashboardWindow : UserControl
{
    readonly Grid root=new();
    readonly Grid columns=new();
    readonly StackPanel[] lanes=[new(){Spacing=16},new(){Spacing=16},new(){Spacing=16}];
    readonly ScrollViewer scroll=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    readonly bool fixture;
    readonly DeviceState? state;
    DeviceSnapshot? snapshot;
    BatteryDetails? battery;
    WarrantyResult? warranty;
    string warrantyStatus="";
    bool reveal,loadingWarranty,closed;
    TextBlock? serialText;
    Button? serialToggle;
    readonly Button reload=new(){Content="Refresh",HorizontalAlignment=HorizontalAlignment.Right};
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    bool Dark=>root.ActualTheme==ElementTheme.Dark;
    string T(string en,string ar)=>L.Ar?ar:en;
    Brush Ink=>new SolidColorBrush(Dark?Colors.White:Colors.Black);
    Brush Muted=>new SolidColorBrush(Dark?Windows.UI.Color.FromArgb(255,185,185,185):Windows.UI.Color.FromArgb(255,90,90,90));
    public DeviceDashboardWindow(string theme,DeviceState? state,bool fixture=false)
    {
        this.state=state;this.fixture=fixture;
        string Title=T("About your device","عن جهازك");
        root.RequestedTheme=theme switch{"dark"=>ElementTheme.Dark,"light"=>ElementTheme.Light,_=>ElementTheme.Default};
        root.FlowDirection=L.Ar?FlowDirection.RightToLeft:FlowDirection.LeftToRight;
        root.Padding=new Thickness(0,0,12,0);root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());
        var header=new Grid{Margin=new Thickness(0,0,0,18),ColumnSpacing=16};header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});Grid.SetColumn(reload,1);
        reload.Content=T("Refresh","تحديث");reload.Click+=async(_,_)=>await Load();header.Children.Add(reload);
        root.Children.Add(header);scroll.Content=columns;Grid.SetRow(scroll,1);root.Children.Add(scroll);Content=root;
        var stacked=new StackPanel{Spacing=14};foreach(var lane in lanes)stacked.Children.Add(lane);scroll.Content=stacked;
        root.ActualThemeChanged+=(_,_)=>Render();Unloaded+=(_,_)=>closed=true;Loaded+=(_,_)=>closed=false;
        Render();_ = Load();
    }
    void Reflow() { }
    async Task Load()
    {
        reload.IsEnabled=false;
        try {
            if(fixture) {snapshot=Fixture();battery=new(99.9,60,59.9,36,new DateTime(2022,1,22),[]);warranty=new(new DateTime(2022,7,29),new DateTime(2023,7,28),null);}
            else {snapshot=await DeviceDashboard.ReadAsync();battery=await Details.ReadBatteryAsync();warranty=Details.ReadCachedWarranty();}
        }catch{warrantyStatus=T("Some information is unavailable.","بعض المعلومات غير متاحة.");}
        if(closed)return;reload.IsEnabled=true;Render();
    }
    void Render()
    {
        foreach(var lane in lanes)lane.Children.Clear();
        if(snapshot==null){lanes[0].Children.Add(Text(T("Reading local device details…","جارٍ قراءة تفاصيل الجهاز…"),16));return;}
        var device=snapshot.Cards.FirstOrDefault(c=>c.Title=="Device");
        if(device!=null){var contents=Fields(device.Fields);var serialRow=new Grid{ColumnSpacing=12};serialRow.ColumnDefinitions.Add(new());serialRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            serialText=Text("",14);serialText.IsTextSelectionEnabled=false;serialRow.Children.Add(serialText);UpdateSerial();
            var toggle=new Button{Content=T(reveal?"Hide":"Reveal",reveal?"إخفاء":"إظهار")};serialToggle=toggle;Grid.SetColumn(toggle,1);toggle.Click+=(_,_)=>{reveal=!reveal;UpdateSerial();toggle.Content=T(reveal?"Hide":"Reveal",reveal?"إخفاء":"إظهار");};serialRow.Children.Add(toggle);contents.Children.Add(serialRow);
            lanes[0].Children.Add(Card(T("Device","الجهاز"),device.Icon,contents));}
        lanes[0].Children.Add(WarrantyCard());
        if(fixture)lanes[0].Children.Add(Text(T("Visual fixture only. Values are sample data, not this PC.","معاينة ببيانات تجريبية فقط، ليست بيانات هذا الجهاز."),12));
        var capabilities=new StackPanel{Spacing=10};
        capabilities.Children.Add(Text(T("Only controls successfully read on this device are listed as available. Fn Lock is an initial UI assumption, not a verified hardware state.","يتم عرض أدوات التحكم المقروءة بنجاح فقط كمتاحة. قفل Fn افتراض أولي للواجهة وليس حالة عتاد مؤكدة."),13));
        capabilities.Children.Add(Row(T("Device family","عائلة الجهاز"),(fixture||DeviceInfo.IsSupported)?T("Recognized Legion / LOQ","Legion / LOQ معروف"):T("Not recognized","غير معروف")));
        string Availability(bool available)=>available?T("Available","متاح"):T("Not verified","غير مؤكد");
        foreach(var p in new (string,bool)[]{("Charging modes",state?.Mode!=null),("Microphone mute",state?.Muted!=null),("Touchpad lock",state?.TouchpadLocked!=null),("Refresh rate",state?.AvailableHz?.Length>=2),("Always-on USB",state?.UsbMode!=null)})capabilities.Children.Add(Row(p.Item1,Availability(p.Item2)));
        capabilities.Children.Add(Text(T("Generation and extra firmware features are not inferred from the model name.","لا يتم استنتاج الجيل أو ميزات الفيرموير الإضافية من اسم الموديل."),12));
        lanes[0].Children.Add(Card(T("Platform & Capabilities","المنصة والإمكانيات"),"\uE946",capabilities));
        var networkCards=new List<DeviceCard>();
        foreach(var c in snapshot.Cards.Where(c=>c.Title!="Device" && !c.Title.StartsWith("Battery"))) {
            if(c.Title.StartsWith("Network adapters")){networkCards.Add(c);continue;}
            int lane=c.Title is "Storage" or "Volumes" or "Displays" || c.Title.StartsWith("Storage ") || c.Title.StartsWith("Volumes ") || c.Title.StartsWith("Network adapters")?2:1;
            lanes[lane].Children.Add(Card(c.Title,c.Icon,Fields(c.Fields)));
        }
        var b=new StackPanel{Spacing=10};
        b.Children.Add(Row(T("Current charge","الشحن الحالي"),state?.Percent is int percent?percent+"%":"Unavailable"));
        b.Children.Add(Row(T("Health","صحة البطارية"),Number(battery?.HealthPercent,"%")));
        b.Children.Add(Row(T("Design capacity","السعة التصميمية"),Number(battery?.DesignWh," Wh")));
        b.Children.Add(Row(T("Full charge capacity","سعة الشحن الكامل"),Number(battery?.FullWh," Wh")));
        b.Children.Add(Row(T("Cycle count","عدد الدورات"),battery?.Cycles?.ToString()??"Unavailable"));b.Children.Add(Row(T("Manufacture date","تاريخ التصنيع"),battery?.Manufactured?.ToString("yyyy-MM-dd")??"Unavailable"));
        foreach(var c in snapshot.Cards.Where(c=>c.Title.StartsWith("Battery")))foreach(var field in c.Fields)b.Children.Add(Row(field.Label,field.Value));
        lanes[2].Children.Add(Card(T("Battery details","تفاصيل البطارية"),"\uE83F",b));foreach(var c in networkCards)lanes[2].Children.Add(Card(c.Title,c.Icon,Fields(c.Fields)));Reflow();
    }
    void UpdateSerial(){if(serialText==null)return;serialText.Text=T("Serial number: ","الرقم التسلسلي: ")+(reveal?(snapshot?.Serial is {Length:>0} s?s:"Unavailable"):"••••••••");serialText.IsTextSelectionEnabled=reveal;if(serialToggle!=null)serialToggle.Content=T(reveal?"Hide":"Reveal",reveal?"إخفاء":"إظهار");}
    UIElement WarrantyCard()
    {
        var p=new StackPanel{Spacing=10};string Date(DateTime? d)=>d?.ToString("yyyy-MM-dd")??T("Unavailable","غير متاح");
        if(warranty!=null){p.Children.Add(Text(warranty.End is DateTime end?(end.Date>=DateTime.Today?T("Active","ساري"):T("Expired","منتهي")):T("Status unavailable","الحالة غير متاحة"),16,true));p.Children.Add(Row(T("Start date","تاريخ البداية"),Date(warranty.Start)));p.Children.Add(Row(T("End date","تاريخ النهاية"),Date(warranty.End)));}
        else p.Children.Add(Text(T("No cached warranty details. Check with Lenovo to retrieve them.","لا توجد بيانات ضمان محفوظة. تحقق مع Lenovo لجلبها."),13));
        if(warrantyStatus.Length>0)p.Children.Add(Text(warrantyStatus,12));
        var fetch=new Button{Content=T("Check warranty with Lenovo","التحقق من الضمان مع Lenovo"),IsEnabled=!loadingWarranty&&!fixture};
        ToolTipService.SetToolTip(fetch,T("Sends your serial number and machine type to Lenovo only when clicked.","يرسل الرقم التسلسلي ونوع الجهاز إلى Lenovo عند الضغط فقط."));
        fetch.Click+=async(_,_)=>{loadingWarranty=true;warrantyStatus=T("Checking…","جارٍ التحقق…");Render();try{var result=await Details.FetchWarrantyAsync();if(result!=null){warranty=result;warrantyStatus="";}else warrantyStatus=T("Lenovo did not return warranty dates.","لم يرجع Lenovo تواريخ الضمان.");}catch{warrantyStatus=T("Could not reach Lenovo. Cached dates are unchanged.","تعذر الاتصال بـ Lenovo. لم تتغير التواريخ المحفوظة.");}loadingWarranty=false;if(!closed)Render();};p.Children.Add(fetch);
        p.Children.Add(new HyperlinkButton{Content="Lenovo Support",NavigateUri=new Uri("https://pcsupport.lenovo.com/")});
        return Card(T("Warranty","الضمان"),"\uE73E",p);
    }
    string Number(double? n,string unit)=>n.HasValue?n.Value.ToString("0.0",CultureInfo.InvariantCulture)+unit:T("Unavailable","غير متاح");
    TextBlock Text(string value,double size=14,bool bold=false)=>new(){Text=value,FontSize=size,FontWeight=bold?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Foreground=Ink,IsTextSelectionEnabled=true};
    StackPanel Fields(DeviceField[] fields){var p=new StackPanel{Spacing=10};foreach(var f in fields)p.Children.Add(Row(f.Label,f.Value));return p;}
    Grid Row(string name,string value){var g=new Grid{ColumnSpacing=12};g.ColumnDefinitions.Add(new(){Width=new GridLength(0.43,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=new GridLength(0.57,GridUnitType.Star)});var label=Text(name,13);label.Foreground=Muted;g.Children.Add(label);var text=Text(value,14,true);Grid.SetColumn(text,1);g.Children.Add(text);return g;}
    Border Card(string title,string icon,UIElement content){var p=new StackPanel{Spacing=16};var heading=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};heading.Children.Add(DashboardIcons.Create(title));heading.Children.Add(Text(title,20,true));p.Children.Add(heading);p.Children.Add(content);return new(){CornerRadius=new CornerRadius(12),Padding=new Thickness(20),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Dark?Windows.UI.Color.FromArgb(35,255,255,255):Windows.UI.Color.FromArgb(18,0,0,0)),Background=new SolidColorBrush(Dark?Windows.UI.Color.FromArgb(255,40,43,47):Windows.UI.Color.FromArgb(255,251,251,251)),Child=p};}
    public void AssertPreviewReady(){if(snapshot==null||lanes.Sum(l=>l.Children.Count)<8)throw new InvalidOperationException("Device dashboard did not render all fixture cards");}
    public async Task PreviewPositionAsync(bool bottom,bool showSerial=false){reveal=showSerial;UpdateSerial();scroll.ChangeView(null,bottom?scroll.ScrollableHeight:0,null,true);await Task.Delay(350);}
    public async Task CaptureAsync(string path){await Task.Delay(1500);var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(root);var pixels=await bitmap.GetPixelsAsync();var folder=await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(Path.GetFullPath(path))!);var file=await folder.CreateFileAsync(Path.GetFileName(path),CreationCollisionOption.ReplaceExisting);using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,PixelBytes(pixels));await encoder.FlushAsync();}
    static byte[] PixelBytes(Windows.Storage.Streams.IBuffer buffer){using var reader=DataReader.FromBuffer(buffer);var bytes=new byte[buffer.Length];reader.ReadBytes(bytes);return bytes;}
    static DeviceSnapshot Fixture()=>new("DEMO-SERIAL",[
        new("Device","\uE770",[new("Device","Legion visual fixture (not this device)"),new("OS","Windows 11 · deterministic preview"),new("Model","Fixture 82JH"),new("BIOS","H1CN58WW (sample)")]),
        new("CPU","\uE950",[new("Processor","11th Gen Intel Core i5-11400H @ 2.70 GHz (sample)"),new("Cores / threads","6 / 12")]),
        new("GPU","\uE7F4",[new("Adapter","NVIDIA GeForce RTX 3060 Laptop GPU (sample)"),new("Memory","Unavailable from reliable local API"),new("Driver","Fixture")]),
        new("RAM","\uE964",[new("Slot","DIMM 0"),new("Capacity","8 GiB"),new("Type","DDR4"),new("Configured speed","3200 MT/s")]),
        new("RAM 2","\uE964",[new("Slot","DIMM 1"),new("Capacity","8 GiB"),new("Type","DDR4")]),
        new("Motherboard","\uE977",[new("Product","LNVNB161216 (sample)"),new("Manufacturer","LENOVO")]),
        new("Storage","\uEDA2",[new("Drive","Samsung NVMe sample"),new("Capacity","953.87 GiB"),new("Firmware","Fixture")]),
        new("Volumes","\uEDA2",[new("Volume","C: OS"),new("Capacity","300 GiB"),new("Free","204.5 GiB")]),
        new("Displays","\uE7F4",[new("Display 1","Generic display (sample)"),new("Active mode","1920 × 1080 · 165 Hz"),new("Supported rates at this mode","60 / 165 Hz")]),
        new("Network adapters","\uE839",[new("Adapter","Intel Wi-Fi 6 AX201 (sample)"),new("MAC","00:00:00:00:00:00 (fixture)")])]);
}
