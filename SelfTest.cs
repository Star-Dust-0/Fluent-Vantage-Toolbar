using System.Net.Http;
using System.Text.Json;
namespace FluentLegionToolbar;

/// <summary>CI-only end-to-end checks (--update-e2e): release-asset decisions offline, then the real published installer through the app's own download code - progress, cancel, checksum failure and host rejection. Nothing is installed or launched.</summary>
static class SelfTest
{
    public static void RunUpdateE2E()
    {
        var log = new List<string>(); int fails = 0;
        void Check(string name, bool ok) { log.Add((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
        try
        {
            JsonElement Rel(string json) => JsonDocument.Parse(json).RootElement;
            string Rj(string tag, bool draft, bool pre, string asset) => "{\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") + ",\"tag_name\":\"" + tag + "\",\"assets\":[{\"name\":\"" + asset + "\",\"browser_download_url\":\"https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/download/" + tag + "/" + asset + "\"}]}";
            Check("newer stable release gives installer asset", MainWindow.UpdateAssetFor(Rel(Rj("v9.0.0", false, false, "FluentVantageToolbar-v9.0.0-installer-win-x64.exe")), new Version(1, 0, 0)) != null);
            Check("legacy bundled installer name accepted for bundled installs", MainWindow.UpdateAssetFor(Rel(Rj("v9.0.0", false, false, "FluentVantageToolbar-9.0.0-Setup-with-dotnet.exe")), new Version(1, 0, 0), true) != null);
            Check("legacy plain installer name accepted for runtime installs", MainWindow.UpdateAssetFor(Rel(Rj("v9.0.0", false, false, "FluentVantageToolbar-9.0.0-Setup.exe")), new Version(1, 0, 0), false) != null);
            bool legacyRejected = false; try { MainWindow.UpdateAssetFor(Rel(Rj("v9.0.0", false, false, "FluentVantageToolbar-9.0.0-Setup.exe")), new Version(1, 0, 0), true); } catch (InvalidOperationException) { legacyRejected = true; }
            Check("legacy plain installer name rejected for bundled installs", legacyRejected);
            var withDigest = MainWindow.UpdateAssetFor(Rel("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v9.0.0\",\"assets\":[{\"name\":\"FluentVantageToolbar-v9.0.0-installer-win-x64.exe\",\"size\":1234,\"digest\":\"sha256:ABCDEF\",\"browser_download_url\":\"https://github.com/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe\"}]}"), new Version(1, 0, 0));
            Check("update asset carries size and sha256 digest", withDigest != null && withDigest.Size == 1234 && withDigest.Sha256 == "ABCDEF");
            bool foreign = false; try { MainWindow.UpdateAssetFor(Rel("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v9.0.0\",\"assets\":[{\"name\":\"FluentVantageToolbar-v9.0.0-installer-win-x64.exe\",\"browser_download_url\":\"https://github.com/someone-else/other/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe\"}]}"), new Version(1, 0, 0)); } catch (InvalidOperationException) { foreign = true; }
            Check("foreign repository download url is rejected", foreign);
            Check("same version gives none", MainWindow.UpdateAssetFor(Rel(Rj("v1.0.0", false, false, "FluentVantageToolbar-v1.0.0-installer-win-x64.exe")), new Version(1, 0, 0)) == null);
            Check("older gives none", MainWindow.UpdateAssetFor(Rel(Rj("v0.4.0", false, false, "FluentVantageToolbar-v0.4.0-installer-win-x64.exe")), new Version(1, 0, 0)) == null);
            Check("prerelease ignored", MainWindow.UpdateAssetFor(Rel(Rj("v9.0.0", false, true, "FluentVantageToolbar-v9.0.0-installer-win-x64.exe")), new Version(1, 0, 0)) == null);
            Check("draft ignored", MainWindow.UpdateAssetFor(Rel(Rj("v9.0.0", true, false, "FluentVantageToolbar-v9.0.0-installer-win-x64.exe")), new Version(1, 0, 0)) == null);
            Check("download host: github.com and its asset hosts only", UpdateInstaller.IsTrustedSource(new Uri("https://github.com/a/b")) && UpdateInstaller.IsTrustedSource(new Uri("https://release-assets.githubusercontent.com/x")) && !UpdateInstaller.IsTrustedSource(new Uri("https://evil.example/x")) && !UpdateInstaller.IsTrustedSource(new Uri("http://github.com/a")) && !UpdateInstaller.IsTrustedSource(new Uri("https://githubusercontent.com.evil.example/x")));
            {
                var tmp = Path.Combine(Path.GetTempPath(), "fvt-verify-" + Guid.NewGuid().ToString("N"));
                var good = new byte[4096]; good[0] = (byte)'M'; good[1] = (byte)'Z'; File.WriteAllBytes(tmp, good);
                var goodHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(good));
                bool Fails(UpdateAsset a) { try { UpdateInstaller.Verify(tmp, a); return false; } catch (InvalidDataException) { return true; } }
                UpdateInstaller.Verify(tmp, new UpdateAsset("u", "n.exe", 4096, goodHash.ToLowerInvariant()));
                Check("installer verification accepts matching size and checksum", true);
                Check("installer verification rejects wrong size", Fails(new UpdateAsset("u", "n.exe", 4097, null)));
                Check("installer verification rejects wrong checksum", Fails(new UpdateAsset("u", "n.exe", 4096, new string('0', 64))));
                var bad = (byte[])good.Clone(); bad[0] = 0; File.WriteAllBytes(tmp, bad);
                Check("installer verification rejects non-executable file", Fails(new UpdateAsset("u", "n.exe", 4096, null)));
                File.Delete(tmp);
            }
            bool threw = false; try { MainWindow.UpdateAssetFor(Rel(Rj("v9.0.0", false, false, "other.zip")), new Version(1, 0, 0)); } catch (InvalidOperationException) { threw = true; }
            Check("missing installer asset reports error", threw);
            Check("tray icons: both glyphs ship and differ", File.Exists(Path.Combine(AppContext.BaseDirectory, "tray-dark.ico")) && File.Exists(Path.Combine(AppContext.BaseDirectory, "tray-white.ico")) && !File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tray-dark.ico")).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "tray-white.ico"))));
        }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fails++; }

        try { Task.Run(async () =>
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentVantageToolbar-ci");
            var json = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/MohamedElnaggar00/Fluent-Vantage-Toolbar/releases/latest"));
            var asset = MainWindow.UpdateAssetFor(json.RootElement, new Version(0, 3, 9));
            Check("live release offers an installer to 0.3.9", asset != null && asset.Sha256 != null && asset.Size > 1_000_000);
            if (asset == null) return;
            log.Add("asset " + asset.Name + " " + asset.Size + " bytes");
            var steps = new List<double>();
            var file = await UpdateInstaller.DownloadAsync(asset, p => steps.Add(p), CancellationToken.None);
            Check("download finished and file exists", File.Exists(file) && new FileInfo(file).Length == asset.Size);
            Check("progress reported and reached 100", steps.Count > 5 && steps[^1] >= 99.9 && steps.Zip(steps.Skip(1), (a, b) => b >= a).All(x => x));
            Check("downloaded installer passes verification", Verifies(file, asset));
            Check("no partial file left after success", !Directory.GetFiles(UpdateInstaller.Dir, "*.part").Any());
            using var cts = new CancellationTokenSource();
            bool canceled = false;
            try { await UpdateInstaller.DownloadAsync(asset, p => { if (p > 3) cts.Cancel(); }, cts.Token); } catch (OperationCanceledException) { canceled = true; }
            Check("cancel stops the download", canceled);
            Check("cancel leaves no files behind", Directory.GetFiles(UpdateInstaller.Dir).Length == 0);
            bool badHash = false;
            try { await UpdateInstaller.DownloadAsync(asset with { Sha256 = new string('0', 64) }, _ => { }, CancellationToken.None); } catch (InvalidDataException) { badHash = true; }
            Check("wrong checksum is rejected", badHash);
            Check("rejected download leaves no installer behind", Directory.GetFiles(UpdateInstaller.Dir).Length == 0);
            bool badHost = false;
            try { await UpdateInstaller.DownloadAsync(asset with { Url = "https://example.com/" + asset.Name }, _ => { }, CancellationToken.None); } catch (InvalidOperationException) { badHost = true; }
            Check("untrusted host is refused before any download", badHost);
        }).GetAwaiter().GetResult(); }
        catch (Exception ex) { log.Add("EXCEPTION " + ex); fails++; }
        try { Directory.Delete(UpdateInstaller.Dir, true); } catch { }
        try { Directory.CreateDirectory(AppSettings.Folder); File.WriteAllLines(Path.Combine(AppSettings.Folder, "update-e2e.log"), log); } catch { }
        Environment.Exit(fails == 0 ? 0 : 1);
    }
    static bool Verifies(string file, UpdateAsset asset) { try { UpdateInstaller.Verify(file, asset); return true; } catch { return false; } }
}
