using System.Management;
namespace FluentLegionToolbar.Hardware;

/// <summary>Reads the machine identity once. Support is decided from the vendor and the Legion machine types listed
/// in LenovoLegionToolkit's compatibility table (used as evidence only); every control is still gated and read back separately.</summary>
static class DeviceInfo
{
    static readonly string[] MachineTypes = { "82JH", "82JQ", "82N6", "82RB", "82RC", "82RD", "82RE", "82TB", "82TD", "82UH", "82WK", "82WM", "82WQ", "82WR", "82WS", "82Y5", "82Y9", "82YA", "83D6", "83DE", "83DF", "83DG", "83DH", "83E1", "83EF", "83EG", "83EW", "83EX", "83EY", "83F0", "83F1", "83F2", "83F3", "83F5", "83FD", "83G0", "83JJ", "83KY", "83LT", "83LU", "83LY", "83M0", "83N2", "83NN", "83NX", "83Q6", "83Q7", "83RU", "83RW", "83VK" };
    static readonly Lazy<(string name, string machineType, bool lenovo)> Info = new(Read);
    public static string? Override;
    public static string Name => Override ?? Info.Value.name;
    public static string MachineType => Info.Value.machineType;
    public static bool FnInverted => MachineTypes.Contains(MachineType, StringComparer.OrdinalIgnoreCase) && MachineType != "82JH";
    public static bool IsSupported
    {
        get
        {
            var (name, type, lenovo) = Info.Value;
            return lenovo && ((name.Contains("Legion", StringComparison.OrdinalIgnoreCase) || name.Contains("LOQ", StringComparison.OrdinalIgnoreCase)) || MachineTypes.Contains(type, StringComparer.OrdinalIgnoreCase));
        }
    }

    internal static bool ValidName(string value) => !string.IsNullOrWhiteSpace(value) && !new[]{"Not", "None", "Unknown", "Default", "To Be Filled", "System Product Name"}.Any(x=>value.Contains(x,StringComparison.OrdinalIgnoreCase));

    static (string, string, bool) Read()
    {
        string name = "", model = "", type = "", vendor = "";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"); using var r = s.Get();
            foreach (ManagementObject o in r) using (o) { vendor = Convert.ToString(o["Manufacturer"]) ?? ""; var m = (Convert.ToString(o["Model"]) ?? "").Trim(); model=m; if (m.Length >= 4) type = m[..4]; }
        } catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Version FROM Win32_ComputerSystemProduct"); using var r = s.Get();
            foreach (ManagementObject o in r) using (o) { var v = (Convert.ToString(o["Version"]) ?? "").Trim(); if (ValidName(v)) name = v; }
        } catch { }
        if(!ValidName(name))name=ValidName(model)?model:"PC";
        return (name, type, vendor.Contains("LENOVO", StringComparison.OrdinalIgnoreCase));
    }
}
