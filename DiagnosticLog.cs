namespace FluentLegionToolbar;
static class DiagnosticLog
{
    static readonly object Gate = new();
    public static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentLegionToolbar", "diagnostic.log");
    public static void Write(string message)
    {
        try { lock (Gate) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!); if (File.Exists(Path) && new FileInfo(Path).Length > 2_000_000) File.Move(Path, Path + ".old", true); File.AppendAllText(Path, $"{DateTime.Now:O} {message}\n"); } } catch { }
    }
}
