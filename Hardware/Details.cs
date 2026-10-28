using System.Globalization;
using System.Management;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace FluentLegionToolbar.Hardware;

public sealed record BatteryDetails(double? HealthPercent, double? DesignWh, double? FullWh, int? Cycles, DateTime? Manufactured, string[] Problems);
public sealed record WarrantyResult(DateTime? Start, DateTime? End, string? Link);

public static class Details
{
    static double? FirstDouble(string query, string property)
    {
        using var search = new ManagementObjectSearcher(@"root\WMI", query);
        using var rows = search.Get();
        foreach (ManagementObject row in rows)
        {
            using (row)
            {
                var value = Convert.ToDouble(row[property], CultureInfo.InvariantCulture);
                if (value > 0) return value;
            }
        }
        return null;
    }

    public static Task<BatteryDetails> ReadBatteryAsync() => Task.Run(() =>
    {
        var problems = new List<string>();
        double? design = null, full = null; int? cycles = null; DateTime? date = null;
        try { design = FirstDouble("SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity"); } catch (Exception e) { problems.Add("Design capacity: " + e.Message); }
        try { full = FirstDouble("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity"); } catch (Exception e) { problems.Add("Full capacity: " + e.Message); }
        try { var c = FirstDouble("SELECT CycleCount FROM BatteryCycleCount", "CycleCount"); if (c.HasValue) cycles = (int)c.Value; } catch (Exception e) { problems.Add("Cycle count: " + e.Message); }
        try
        {
            for (uint index = 0; index < 3 && date == null; index++)
            {
                var record = LegionDevice.ReadLenovoBatteryRecord(index);
                if (record == null || record.Value.Temperature is 0 or ushort.MaxValue) continue;
                date = DecodeDate(record.Value.Manufacture);
            }
        }
        catch (Exception e) { problems.Add("Manufacture date: " + e.Message); }
        double? health = design.HasValue && full.HasValue && design > 0 ? Math.Min(100, Math.Round(full.Value / design.Value * 100, 1)) : null;
        return new BatteryDetails(health, design.HasValue ? design / 1000 : null, full.HasValue ? full / 1000 : null, cycles, date, problems.ToArray());
    });

    // FAT-style packed date: bits 15-9 year since 1980, bits 8-5 month, bits 4-0 day.
    static DateTime? DecodeDate(ushort s)
    {
        try
        {
            if (s < 1) return null;
            var date = new DateTime((s >> 9) + 1980, (s >> 5) & 15, s & 31, 0, 0, 0, DateTimeKind.Unspecified);
            return date.Year is < 2018 or > 2030 ? null : date;
        }
        catch { return null; }
    }

    static string CachePath => Path.Combine(AppSettings.Folder, "warranty.json");

    public static WarrantyResult? ReadCachedWarranty()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize<WarrantyResult>(File.ReadAllText(CachePath)) : null; } catch { return null; }
    }

    public static async Task<WarrantyResult?> FetchWarrantyAsync()
    {
        string machineType = "", serial = "";
        await Task.Run(() =>
        {
            using var search = new ManagementObjectSearcher("SELECT Name, IdentifyingNumber FROM Win32_ComputerSystemProduct");
            using var rows = search.Get();
            foreach (ManagementObject row in rows) { using (row) { machineType = Convert.ToString(row["Name"]) ?? ""; serial = Convert.ToString(row["IdentifyingNumber"]) ?? ""; } }
        });
        if (serial.Length == 0) return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        var body = new StringContent(JsonSerializer.Serialize(new { serialNumber = serial, machineType }), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("https://pcsupport.lenovo.com/dk/en/api/v4/upsell/redport/getIbaseInfo", body);
        response.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        if (node?["code"]?.GetValue<int>() != 0) return null;
        var items = new List<JsonNode?>();
        foreach (var key in new[] { "baseWarranties", "upgradeWarranties" })
            if (node["data"]?[key] is JsonArray array) items.AddRange(array);
        var starts = items.Select(n => n?["startDate"]?.ToString()).Where(v => !string.IsNullOrEmpty(v)).Select(v => DateTime.Parse(v!, CultureInfo.InvariantCulture)).ToList();
        var ends = items.Select(n => n?["endDate"]?.ToString()).Where(v => !string.IsNullOrEmpty(v)).Select(v => DateTime.Parse(v!, CultureInfo.InvariantCulture)).ToList();
        if (starts.Count == 0 && ends.Count == 0) return null;
        string? link = null;
        try
        {
            var products = JsonNode.Parse(await http.GetStringAsync($"https://pcsupport.lenovo.com/dk/en/api/v4/mse/getproducts?productId={Uri.EscapeDataString(serial)}")) as JsonArray;
            var id = products?.FirstOrDefault()?["Id"]?.ToString();
            if (!string.IsNullOrEmpty(id)) link = "https://pcsupport.lenovo.com/products/" + id;
        }
        catch { }
        var result = new WarrantyResult(starts.Count > 0 ? starts.Min() : null, ends.Count > 0 ? ends.Max() : null, link);
        try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllText(CachePath, JsonSerializer.Serialize(result)); } catch { }
        return result;
    }
}
