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



    string UpdateLabel => L.Ar ? "التحقق من التحديثات" : "Check for updates";
    UIElement UpdateCard(bool compact = false)
    {
        var stack = new StackPanel { Spacing=8, HorizontalAlignment=HorizontalAlignment.Stretch };
        if(!compact) stack.Children.Add(Text(L.Ar ? "التحديثات" : "Updates",16,true));
        if(!compact) stack.Children.Add(Text(L.Ar ? "اضغط الزر للتحقق من التحديثات الآن." : "Press the button to check for updates now.",13,false,SecondaryText));
        if(!compact) stack.Children.Add(Text((L.Ar ? "الإصدار الحالي: " : "Current version: ")+"v"+(typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),13,false,SecondaryText));
        var buttonsRow=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
        var button=new Button { Content=UpdateLabel, IsEnabled=!checkingUpdates, HorizontalAlignment=compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
        button.Click+=async (_,_)=>await CheckForUpdatesAsync();updateButtons.Add(button);buttonsRow.Children.Add(button);
        var install=new Button { Content=L.Ar ? "تنزيل وتثبيت" : "Download and install", HorizontalAlignment=compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, Visibility=Visibility.Collapsed };
        install.Click+=(_,_)=>InstallUpdate();installButtons.Add(install);buttonsRow.Children.Add(install);
        stack.Children.Add(buttonsRow);
        var result=Text("",13);result.TextWrapping=TextWrapping.Wrap;if(compact) result.TextAlignment=TextAlignment.Center;updateResults.Add(result);stack.Children.Add(result);
        var link=new HyperlinkButton { Content=L.Ar ? "فتح صفحة التنزيل" : "Open download page", HorizontalAlignment=compact ? HorizontalAlignment.Center : HorizontalAlignment.Left };
        updateLinks.Add(link);stack.Children.Add(link);RefreshUpdateCards();
        return compact ? stack : Card(stack,new Thickness(24));
    }
    void RefreshUpdateCards()
    {
        string installed=typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        string result=manualUpdateState switch {
            "checking" => L.Ar ? "جارٍ التحقق..." : "Checking...",
            "available" => L.Ar ? "الإصدار "+manualUpdateVersion+" متاح." : "Version "+manualUpdateVersion+" is available.",
            "current" => L.Ar ? "أنت على أحدث إصدار (v"+installed+")." : "You're up to date (v"+installed+").",
            "error" => L.Ar ? "تعذر التحقق من التحديثات. تحقق من اتصالك بالإنترنت وحاول مرة أخرى." : "Couldn't check for updates. Check your internet connection and try again.",
            _ => ""
        };
        foreach(var button in updateButtons){button.IsEnabled=!checkingUpdates;button.Content=UpdateLabel;}
        foreach(var install in installButtons){install.Content=L.Ar ? "تنزيل وتثبيت" : "Download and install";install.Visibility=manualUpdateAsset==null ? Visibility.Collapsed : Visibility.Visible;}
        foreach(var text in updateResults)text.Text=result;
        foreach(var link in updateLinks){link.NavigateUri=manualUpdateUrl==null ? null : new Uri(manualUpdateUrl);link.Visibility=manualUpdateUrl==null ? Visibility.Collapsed : Visibility.Visible;}
    }
    static Version? ReleaseVersion(string? tag) => Version.TryParse(tag?.TrimStart('v','V'),out var version) ? version : null;
    static string? UpdateDownload(System.Text.Json.JsonElement release, Version installed,bool bundled=true) => UpdateAssetFor(release,installed,bundled)?.Url;
    internal static UpdateAsset? UpdateAssetFor(System.Text.Json.JsonElement release, Version installed,bool bundled=true)
    {
        if(release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())return null;
        var next=ReleaseVersion(release.GetProperty("tag_name").GetString());
        if(next==null || next<=installed)return null;
        foreach(var asset in release.GetProperty("assets").EnumerateArray()) {
            var name=asset.GetProperty("name").GetString();
            // Accept the current installer name and the pre-0.4.0 legacy name, so older installs can still self-update.
            if(name!=$"FluentVantageToolbar-v{next.ToString(3)}-installer-win-x64.exe" && name!=$"FluentVantageToolbar-{next.ToString(3)}-Setup{(bundled ? "-with-dotnet" : "")}.exe")continue;
            var url=asset.GetProperty("browser_download_url").GetString();
            if(Uri.TryCreate(url,UriKind.Absolute,out var uri) && uri.Scheme=="https" && uri.Host=="github.com" && uri.AbsolutePath.StartsWith("/Star-Dust-0/Fluent-Vantage-Toolbar/releases/download/",StringComparison.Ordinal)) {
                long size=asset.TryGetProperty("size",out var sz) && sz.ValueKind==System.Text.Json.JsonValueKind.Number ? sz.GetInt64() : 0;
                string? sha=null;
                if(asset.TryGetProperty("digest",out var dg) && dg.ValueKind==System.Text.Json.JsonValueKind.String && (dg.GetString() ?? "").StartsWith("sha256:",StringComparison.OrdinalIgnoreCase))sha=dg.GetString()!.Substring(7);
                return new UpdateAsset(url!,name!,size,sha);
            }
        }
        throw new InvalidOperationException("New release has no installer matching this build flavor.");
    }
    // Self-contained .NET builds carry coreclr.dll beside the executable;
    // framework-dependent builds load it from the installed shared runtime.
    bool InstalledBundled => File.Exists(Path.Combine(AppContext.BaseDirectory,"coreclr.dll"));
    async Task AutomaticUpdatesAsync(CancellationToken token)
    {
        try {
            await Task.Delay(TimeSpan.FromSeconds(45),token);
            while(!token.IsCancellationRequested) {
                if(settings.AutoCheckUpdates)await CheckAutomaticUpdateAsync();
                await Task.Delay(TimeSpan.FromHours(6),token);
            }
        } catch(OperationCanceledException) { }
    }
    async Task CheckAutomaticUpdateAsync()
    {
        if(preview || exiting || checkingUpdates || !settings.AutoCheckUpdates)return;
        checkingUpdates=true;
        try {
            using var http=new System.Net.Http.HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentVantageToolbar/"+(typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response=await http.GetAsync("https://api.github.com/repos/Star-Dust-0/Fluent-Vantage-Toolbar/releases/latest",updateLifetime.Token);
            response.EnsureSuccessStatusCode();
            using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(updateLifetime.Token));
            var installed=ReleaseVersion(typeof(MainWindow).Assembly.GetName().Version?.ToString(3)) ?? new Version(0,0,0);
            var asset=UpdateAssetFor(json.RootElement,installed,InstalledBundled);
            var tag=json.RootElement.GetProperty("tag_name").GetString();
            if(asset==null || !settings.AutoCheckUpdates || exiting || tag==settings.LastNotifiedUpdate)return;
            ShowUpdateNotice(tag ?? "",asset);
            settings.LastNotifiedUpdate=tag;settings.Save();
        } catch(OperationCanceledException) { }
        catch(Exception error) { DiagnosticLog.Write("Automatic update check failed: "+error.Message); }
        finally {checkingUpdates=false;}
    }
    void ShowUpdateNotice(string version,UpdateAsset asset)
    {
        try{updateToast?.Close();}catch{}
        updateToast=new UpdateToast(version.TrimStart('v','V'),asset,"https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar/releases/latest",Dark);
        if(!preview) {
            tray.Notify(L.Ar ? "يتوفر تحديث جديد" : "Update available",$"Fluent Vantage Toolbar {version}");
            updateToast.ShowToast();
        }
    }

    /// <summary>Opens the update toast from a manual check and starts the in-app download. The installer still runs only after "Install now".</summary>
    void InstallUpdate()
    {
        if(manualUpdateAsset==null || preview)return;
        try{updateToast?.Close();}catch{}
        updateToast=new UpdateToast((manualUpdateVersion ?? "").TrimStart('v','V'),manualUpdateAsset,"https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar/releases/latest",Dark);
        updateToast.ShowToast();
        updateToast.Begin();
    }

    async Task CheckForUpdatesAsync()
    {
        if(checkingUpdates || preview)return;
        checkingUpdates=true;
        manualUpdateState="checking";manualUpdateUrl=null;manualUpdateAsset=null;RefreshUpdateCards();
        try {
            using var http=new System.Net.Http.HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentVantageToolbar/"+(typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response=await http.GetAsync("https://api.github.com/repos/Star-Dust-0/Fluent-Vantage-Toolbar/releases/latest");
            response.EnsureSuccessStatusCode();
            using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var installed=ReleaseVersion(typeof(MainWindow).Assembly.GetName().Version?.ToString(3)) ?? new Version(0,0,0);
            manualUpdateAsset=UpdateAssetFor(json.RootElement,installed,InstalledBundled);
            manualUpdateUrl=manualUpdateAsset?.Url;
            manualUpdateVersion=json.RootElement.GetProperty("tag_name").GetString();
            manualUpdateState=manualUpdateUrl==null ? "current" : "available";
        } catch(Exception error) {
            DiagnosticLog.Write("Update check failed: "+error.Message);
            manualUpdateState="error";
        } finally {
            checkingUpdates=false;if(!exiting)RefreshUpdateCards();
        }
    }}
