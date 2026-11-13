using System.Globalization;
using System.Text.Json;
namespace FluentLegionToolbar;

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "system";
    public const string DefaultAccentColor = "#00A6A6";
    public string? AccentColor { get; set; } = DefaultAccentColor;
    public bool AutoCheckUpdates { get; set; } = true;
    public string? LastNotifiedUpdate { get; set; }
    public bool ThermalDefaultLayoutApplied { get; set; } = true;
    public List<string> TileOrder { get; set; } = new();
    public List<string> HiddenTiles { get; set; } = new() { "fn", "usb", "refresh" };
    public bool ShowWarranty { get; set; } = true;
    public bool ShowBatteryDetails { get; set; } = true;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentLegionToolbar");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) {
            string json=File.ReadAllText(FilePath);var loaded=JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            using var document=JsonDocument.Parse(json);
            if(!document.RootElement.TryGetProperty(nameof(ThermalDefaultLayoutApplied),out _)) {
                loaded.HiddenTiles.Remove("thermal");
                if(!loaded.HiddenTiles.Contains("fn"))loaded.HiddenTiles.Add("fn");
                loaded.ThermalDefaultLayoutApplied=true;loaded.Save();
            }
            if(loaded.AccentColor==null || !System.Text.RegularExpressions.Regex.IsMatch(loaded.AccentColor,"^#[0-9A-Fa-f]{6}$"))loaded.AccentColor=DefaultAccentColor;
            return loaded;
        }} catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        } catch { }
    }

    public bool IsTileVisible(string id) => !HiddenTiles.Contains(id);
    public void SetTileVisible(string id, bool visible)
    {
        HiddenTiles.Remove(id);
        if (!visible) HiddenTiles.Add(id);
    }
}

