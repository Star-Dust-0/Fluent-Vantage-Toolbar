using System.Globalization;
using System.Management;
using WindowsDisplayAPI;
namespace FluentLegionToolbar.Hardware;

internal sealed record DeviceField(string Label, string Value);
internal sealed record DeviceCard(string Title, string Icon, DeviceField[] Fields);
internal sealed record DeviceSnapshot(string Serial, DeviceCard[] Cards);

internal static class DeviceDashboard
{
    // Read-only, local inventory. Each class can fail independently on a different device.
    // No serial, MAC, IP or inventory values are written to diagnostics or sent to a server.
    public static Task<DeviceSnapshot> ReadAsync() => Task.Run(() =>
    {
        var cards = new List<DeviceCard>();
        string serial = "", model = "", machine = "";
        var identity = new List<DeviceField>();
        Query("Win32_ComputerSystemProduct", "Name,Version,IdentifyingNumber,Vendor", row => {
            serial = Value(row, "IdentifyingNumber"); model = Value(row, "Version"); machine = Value(row, "Name");
            identity.Add(new("Device", DeviceInfo.ValidName(model)?model:DeviceInfo.Name)); identity.Add(new("Manufacturer", Value(row,"Vendor")));
            identity.Add(new("Model", machine)); identity.Add(new("Machine type", machine.Length >= 4 && machine != "Unavailable" ? machine[..4] : "Unavailable"));
        });
        Query("Win32_OperatingSystem", "Caption,Version,OSArchitecture,BuildNumber", row => {
            identity.Add(new("OS", Value(row,"Caption"))); identity.Add(new("Version / build", Value(row,"Version") + " / " + Value(row,"BuildNumber")));
            identity.Add(new("Architecture",Value(row,"OSArchitecture")));
        });
        Query("Win32_BIOS", "SMBIOSBIOSVersion,ReleaseDate", row => {
            identity.Add(new("BIOS",Value(row,"SMBIOSBIOSVersion"))); identity.Add(new("BIOS date",Date(Value(row,"ReleaseDate"))));
        });
        cards.Add(new("Device", "\uE770", identity.ToArray()));
        Add("CPU", "\uE950", "Win32_Processor", "Name,Manufacturer,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed", r => [
            new("Processor",Value(r,"Name")),new("Manufacturer",Value(r,"Manufacturer")),new("Cores / threads",Value(r,"NumberOfCores")+" / "+Value(r,"NumberOfLogicalProcessors")),new("Maximum clock (reported)",Value(r,"MaxClockSpeed")+" MHz")]);
        Add("GPU", "\uE7F4", "Win32_VideoController", "Name,VideoProcessor,DriverVersion,DriverDate,AdapterRAM", r => [
            new("Adapter",Value(r,"Name")),new("Processor",Value(r,"VideoProcessor")),new("Driver",Value(r,"DriverVersion")),new("Driver date",Date(Value(r,"DriverDate"))),new("Memory", "Unavailable from reliable local API")]);
        // AdapterRAM is only UInt32 and often misleading. Do not label it as dedicated VRAM.
        Add("RAM", "\uE964", "Win32_PhysicalMemory", "Capacity,ConfiguredClockSpeed,Speed,Manufacturer,PartNumber,DeviceLocator,SMBIOSMemoryType", r => [
            new("Slot",Value(r,"DeviceLocator")),new("Capacity",Bytes(r,"Capacity")),new("Type",MemoryType(Value(r,"SMBIOSMemoryType"))),new("Configured speed",Value(r,"ConfiguredClockSpeed")+" MT/s"),new("Rated speed",Value(r,"Speed")+" MT/s"),new("Manufacturer",Value(r,"Manufacturer")),new("Part",Value(r,"PartNumber"))]);
        Add("Motherboard", "\uE977", "Win32_BaseBoard", "Manufacturer,Product,Version", r => [new("Manufacturer",Value(r,"Manufacturer")),new("Product",Value(r,"Product")),new("Version",Value(r,"Version"))]);
        Add("Storage", "\uEDA2", "Win32_DiskDrive", "Model,Size,InterfaceType,MediaType,FirmwareRevision", r => [new("Drive",Value(r,"Model")),new("Capacity",Bytes(r,"Size")),new("Interface (reported)",Value(r,"InterfaceType")),new("Media",Value(r,"MediaType")),new("Firmware",Value(r,"FirmwareRevision"))]);
        Add("Volumes", "\uEDA2", "Win32_LogicalDisk", "DeviceID,VolumeName,Size,FreeSpace,FileSystem", r => [new("Volume",Value(r,"DeviceID")+" "+Value(r,"VolumeName")),new("Capacity",Bytes(r,"Size")),new("Free",Bytes(r,"FreeSpace")),new("File system",Value(r,"FileSystem"))], "DriveType=3");
        var displayFields = new List<DeviceField>();
        try {
            int i=0;
            foreach(var display in Display.GetDisplays()) {
                var mode = display.CurrentSetting; ++i;
                displayFields.Add(new("Display "+i,display.DeviceName));
                displayFields.Add(new("Active mode",$"{mode.Resolution.Width} × {mode.Resolution.Height} · {mode.Frequency} Hz"));
                var rates=display.GetPossibleSettings().Where(m=>m.Resolution==mode.Resolution && m.ColorDepth==mode.ColorDepth && m.IsInterlaced==mode.IsInterlaced).Select(m=>m.Frequency).Distinct().OrderBy(x=>x);
                displayFields.Add(new("Supported rates at this mode",string.Join(" / ",rates)+" Hz"));
            }
        } catch { displayFields.Add(new("Display information","Unavailable")); }
        if(displayFields.Count==0)displayFields.Add(new("Display information","Unavailable"));
        cards.Add(new("Displays","\uE7F4",displayFields.ToArray()));
        Add("Battery", "\uE83F", "Win32_Battery", "Name,EstimatedChargeRemaining,DesignVoltage,Status", r => [new("Battery",Value(r,"Name")),new("Charge",Value(r,"EstimatedChargeRemaining")+"%"),new("Design voltage",Value(r,"DesignVoltage")+" mV"),new("Status (reported)",Value(r,"Status"))]);
        Add("Network adapters", "\uE839", "Win32_NetworkAdapter", "Name,Manufacturer,MACAddress,NetEnabled,PhysicalAdapter", r => [new("Adapter",Value(r,"Name")),new("Manufacturer",Value(r,"Manufacturer")),new("MAC",Value(r,"MACAddress")),new("Enabled",Value(r,"NetEnabled"))], "PhysicalAdapter=True");
        return new DeviceSnapshot(serial,cards.ToArray());
        void Add(string title,string icon,string cls,string properties,Func<ManagementObject,DeviceField[]> map,string? filter=null) {
            int number=0;
            Query(cls,properties,r=> {++number;cards.Add(new(title+(number>1?" "+number:""),icon,map(r)));},filter);
            if(number==0) cards.Add(new(title,icon,[new("Information","Unavailable")]));
        }
    });
    static void Query(string cls,string properties,Action<ManagementObject> read,string? filter=null) {
        try {
            using var search=new ManagementObjectSearcher("SELECT "+properties+" FROM "+cls+(filter==null?"":" WHERE "+filter));
            search.Options.Timeout=TimeSpan.FromSeconds(6);
            using var rows=search.Get();foreach(ManagementObject row in rows) using(row) read(row);
        } catch { /* Missing class/provider is not an empty hardware claim. */ }
    }
    static string Value(ManagementObject r,string field) {
        try { string text=Convert.ToString(r[field],CultureInfo.InvariantCulture)?.Trim()??"";return DeviceInfo.ValidName(text)?text:"Unavailable"; }catch{return "Unavailable";}
    }
    static string Date(string value) {try{return ManagementDateTimeConverter.ToDateTime(value).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);}catch{return "Unavailable";}}
    static string Bytes(ManagementObject r,string field) {try{ulong bytes=Convert.ToUInt64(r[field]);return bytes>0?(bytes/1073741824d).ToString("0.##",CultureInfo.InvariantCulture)+" GiB":"Unavailable";}catch{return "Unavailable";}}
    static string MemoryType(string code)=>code switch {"20"=>"DDR","21"=>"DDR2","24"=>"DDR3","26"=>"DDR4","30"=>"LPDDR4","34"=>"DDR5","35"=>"LPDDR5",_=>"Unavailable (SMBIOS "+code+")"};
}
EOF
python3 - <<'PY'
p='/tmp/dashboard030/DeviceDashboard.cs'
s=open(p).read().replace('"Name,EstimatedChargeRemaining,DesignVoltage,Status"','"Name,EstimatedChargeRemaining,DesignVoltage,Status"').replace('new("Charge"','new("Charge"')
s=s.replace('        Add("Volumes", "\uEDA2", "Win32_LogicalDisk", "DeviceID,VolumeName,Size,FreeSpace,FileSystem", r => [new("Volume",Value(r,"DeviceID")+" "+Value(r,"VolumeName")),new("Capacity",Bytes(r,"Size")),new("Free",Bytes(r,"FreeSpace")),new("File system",Value(r,"FileSystem"))], "DriveType=3");
        var displayFields', '''        Add("Volumes", "\\uEDA2", "Win32_LogicalDisk", "DeviceID,VolumeName,Size,FreeSpace,FileSystem", r => [new("Volume",Value(r,"DeviceID")+" "+Value(r,"VolumeName")),new("Capacity",Bytes(r,"Size")),new("Free",Bytes(r,"FreeSpace")),new("File system",Value(r,"FileSystem"))], "DriveType=3");
        Add("Volumes", "\uEDA2", "Win32_LogicalDisk", "DeviceID,VolumeName,Size,FreeSpace,FileSystem", r => [new("Volume",Value(r,"DeviceID")+" "+Value(r,"VolumeName")),new("Capacity",Bytes(r,"Size")),new("Free",Bytes(r,"FreeSpace")),new("File system",Value(r,"FileSystem"))], "DriveType=3");
        var displayFields''')
open(p,'w').write(s)
PY
cat > /tmp/dashboard030/DeviceDashboardWindow.cs <<'EOF'
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

internal sealed class DeviceDashboardWindow : Window
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
    readonly Button reload=new(){Content="Refresh",HorizontalAlignment=HorizontalAlignment.Right};
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    bool Dark=>root.ActualTheme==ElementTheme.Dark;
    string T(string en,string ar)=>L.Ar?ar:en;
    Brush Ink=>new SolidColorBrush(Dark?Colors.White:Colors.Black);
    Brush Muted=>new SolidColorBrush(Dark?Windows.UI.Color.FromArgb(255,185,185,185):Windows.UI.Color.FromArgb(255,90,90,90));
    public DeviceDashboardWindow(string theme,DeviceState? state,bool fixture=false)
    {
        this.state=state;this.fixture=fixture;
        Title=T("About your device","عن جهازك");SystemBackdrop=new MicaBackdrop();
        root.RequestedTheme=theme switch{"dark"=>ElementTheme.Dark,"light"=>ElementTheme.Light,_=>ElementTheme.Default};
        root.FlowDirection=L.Ar?FlowDirection.RightToLeft:FlowDirection.LeftToRight;
        root.Padding=new Thickness(24,18,24,24);root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());
        var header=new Grid{Margin=new Thickness(0,0,0,18)};header.Children.Add(Text(Title,26,true));
        reload.Content=T("Refresh local details","تحديث التفاصيل المحلية");reload.Click+=async(_,_)=>await Load();header.Children.Add(reload);
        root.Children.Add(header);scroll.Content=columns;Grid.SetRow(scroll,1);root.Children.Add(scroll);Content=root;
        columns.ColumnSpacing=16;for(int i=0;i<3;i++){columns.ColumnDefinitions.Add(new());Grid.SetColumn(lanes[i],i);columns.Children.Add(lanes[i]);}
        root.SizeChanged+=(_,_)=>Reflow();root.ActualThemeChanged+=(_,_)=>Render();Closed+=(_,_)=>closed=true;
        var hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);double scale=GetDpiForWindow(hwnd)/96d;
        var work=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
        int width=Math.Min((int)(1180*scale),work.Width-40),height=Math.Min((int)(880*scale),work.Height-48);
        AppWindow.ResizeClient(new SizeInt32(Math.Max(380,width),Math.Max(360,height)));
        AppWindow.Move(new PointInt32(work.X+Math.Max(0,(work.Width-AppWindow.Size.Width)/2),work.Y+Math.Max(0,(work.Height-AppWindow.Size.Height)/2)));
        try{AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"app.ico"));}catch{}
        Render();_ = Load();
    }
    void Reflow()
    {
        int count=root.ActualWidth<700?1:root.ActualWidth<1000?2:3;
        if(columns.ColumnDefinitions.Count==count)return;
        columns.ColumnDefinitions.Clear();for(int i=0;i<count;i++)columns.ColumnDefinitions.Add(new());
        columns.RowDefinitions.Clear();for(int i=0;i<(3+count-1)/count;i++)columns.RowDefinitions.Add(new(){Height=GridLength.Auto});
        columns.RowSpacing=16;
        for(int i=0;i<3;i++){Grid.SetColumn(lanes[i],i%count);Grid.SetRow(lanes[i],i/count);}
    }
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
            var toggle=new Button{Content=T(reveal?"Hide":"Reveal",reveal?"إخفاء":"إظهار")};Grid.SetColumn(toggle,1);toggle.Click+=(_,_)=>{reveal=!reveal;UpdateSerial();toggle.Content=T(reveal?"Hide":"Reveal",reveal?"إخفاء":"إظهار");};serialRow.Children.Add(toggle);contents.Children.Add(serialRow);
            lanes[0].Children.Add(Card(T("Device","الجهاز"),device.Icon,contents));}
        lanes[0].Children.Add(WarrantyCard());
        var capabilities=new StackPanel{Spacing=10};
        capabilities.Children.Add(Text(T("Only controls successfully read on this device are listed as available. Fn Lock is an initial UI assumption, not a verified hardware state.","يتم عرض أدوات التحكم المقروءة بنجاح فقط كمتاحة. قفل Fn افتراض أولي للواجهة وليس حالة عتاد مؤكدة."),13));
        capabilities.Children.Add(Row(T("Device family","عائلة الجهاز"),DeviceInfo.IsSupported?T("Recognized Legion / LOQ","Legion / LOQ معروف"):T("Not recognized","غير معروف")));
        string Availability(bool available)=>available?T("Available","متاح"):T("Not verified","غير مؤكد");
        foreach(var p in new (string,bool)[]{("Charging modes",state?.Mode!=null),("Microphone mute",state?.Muted!=null),("Touchpad lock",state?.TouchpadLocked!=null),("Refresh rate",state?.AvailableHz?.Length>=2),("Always-on USB",state?.UsbMode!=null)})capabilities.Children.Add(Row(p.Item1,Availability(p.Item2)));
        capabilities.Children.Add(Text(T("Generation and extra firmware features are not inferred from the model name.","لا يتم استنتاج الجيل أو ميزات الفيرموير الإضافية من اسم الموديل."),12));
        lanes[0].Children.Add(Card(T("Platform & Capabilities","المنصة والإمكانيات"),"\uE946",capabilities));
        foreach(var c in snapshot.Cards.Where(c=>c.Title!="Device" && !c.Title.StartsWith("Battery"))) {
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
        lanes[2].Children.Add(Card(T("Battery details","تفاصيل البطارية"),"\uE83F",b));Reflow();
    }
    void UpdateSerial(){if(serialText==null)return;serialText.Text=T("Serial number: ","الرقم التسلسلي: ")+(reveal?(snapshot?.Serial is {Length:>0} s?s:"Unavailable"):"••••••••");serialText.IsTextSelectionEnabled=reveal;}
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
    string Number(double? n,string unit)=>n?.ToString("0.0",CultureInfo.InvariantCulture)+unit is string v && n.HasValue?v:T("Unavailable","غير متاح");
    TextBlock Text(string value,double size=14,bool bold=false)=>new(){Text=value,FontSize=size,FontWeight=bold?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Foreground=Ink,IsTextSelectionEnabled=true};
    StackPanel Fields(DeviceField[] fields){var p=new StackPanel{Spacing=10};foreach(var f in fields)p.Children.Add(Row(f.Label,f.Value));return p;}
    Grid Row(string name,string value){var g=new Grid{ColumnSpacing=12};g.ColumnDefinitions.Add(new(){Width=new GridLength(0.43,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=new GridLength(0.57,GridUnitType.Star)});var label=Text(name,13);label.Foreground=Muted;g.Children.Add(label);var text=Text(value,14,true);Grid.SetColumn(text,1);g.Children.Add(text);return g;}
    Border Card(string title,string icon,UIElement content){var p=new StackPanel{Spacing=16};var heading=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};heading.Children.Add(new FontIcon{Glyph=icon,FontSize=20,Foreground=new SolidColorBrush(Windows.UI.Color.FromArgb(255,90,162,214))});heading.Children.Add(Text(title,19,true));p.Children.Add(heading);p.Children.Add(content);return new(){CornerRadius=new CornerRadius(16),Padding=new Thickness(20),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Dark?Windows.UI.Color.FromArgb(35,255,255,255):Windows.UI.Color.FromArgb(18,0,0,0)),Background=new SolidColorBrush(Dark?Windows.UI.Color.FromArgb(255,40,43,47):Windows.UI.Color.FromArgb(255,251,251,251)),Child=p};}
    public async Task CaptureAsync(string path){await Task.Delay(1500);var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(root);var pixels=await bitmap.GetPixelsAsync();var file=await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));await encoder.FlushAsync();}
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
