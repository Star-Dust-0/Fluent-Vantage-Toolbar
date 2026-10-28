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
