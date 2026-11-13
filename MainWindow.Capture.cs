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


    // ---- capture (CI previews with fixture data) ----
    async Task SaveImage(string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        var buffer = await bitmap.GetPixelsAsync();
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
        using var stream = File.Open(path, FileMode.Create).AsRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    async Task Capture()
    {
        // Offline decision tests never download or launch an installer during captures.
        string fixture="""
        {"draft":false,"prerelease":false,"tag_name":"v9.0.0","assets":[{"name":"FluentVantageToolbar-v9.0.0-installer-win-x64.exe","browser_download_url":"https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe"}]}
        """;
        using(var test=System.Text.Json.JsonDocument.Parse(fixture)) {
            if(UpdateDownload(test.RootElement,new Version(1,0,0))==null || UpdateDownload(test.RootElement,new Version(9,0,0))!=null || UpdateDownload(test.RootElement,new Version(10,0,0))!=null)throw new InvalidOperationException("Update version comparison failed");
            if(!UpdateDownload(test.RootElement,new Version(1,0,0),false)!.EndsWith("-installer-win-x64.exe") || !UpdateDownload(test.RootElement,new Version(1,0,0),true)!.EndsWith("-installer-win-x64.exe"))throw new InvalidOperationException("Installed update flavor mismatch");
            if(!new AppSettings().AutoCheckUpdates)throw new InvalidOperationException("Automatic updates must default on");
        }
        foreach(var replacement in new[]{fixture.Replace("\"draft\":false","\"draft\":true"),fixture.Replace("\"prerelease\":false","\"prerelease\":true")}) {
            using var test=System.Text.Json.JsonDocument.Parse(replacement);
            if(UpdateDownload(test.RootElement,new Version(1,0,0))!=null)throw new InvalidOperationException("Non-stable update accepted");
        }
        if(LegionDevice.DecodeThermal(1)!=ThermalMode.Quiet || LegionDevice.DecodeThermal(2)!=ThermalMode.Balanced || LegionDevice.DecodeThermal(3)!=ThermalMode.Performance || LegionDevice.NextThermal(ThermalMode.Balanced)!=ThermalMode.Performance || LegionDevice.NextThermal(ThermalMode.Performance)!=ThermalMode.Quiet || LegionDevice.NextThermal(ThermalMode.Quiet)!=ThermalMode.Balanced)throw new InvalidOperationException("Thermal protocol mapping failed");
        bool unknownRejected=false;try{LegionDevice.DecodeThermal(0);}catch(InvalidOperationException){unknownRejected=true;}
        if(!unknownRejected)throw new InvalidOperationException("Unknown thermal mode accepted");
        // Deterministic render fixture, clearly labelled as preview. Hardware services and the network are never called.
        int index = Array.IndexOf(args, "--capture"); string path = args[index + 1];
        if(args.Contains("--tray-capture")) {
            // Real taskbar evidence: report the live shell icon anchor and the icon file this build chose for the current Windows taskbar theme.
            bool anchored=false;int anchorX=0,anchorY=0;
            for(int attempt=0;attempt<320 && !anchored;attempt++) {
                await Task.Delay(250);
                anchored=tray.TryGetAnchor(out anchorX,out anchorY);
                if(!anchored)tray.EnsureAdded();
                if(attempt%20==19)File.AppendAllText(path+".log",$"attempt {attempt} added={tray.IsAdded} ico={tray.IconLoaded} addErr={tray.LastAddError} modErr={tray.LastModifyError} anchored={anchored} light={TaskbarIsLight()} icon={Path.GetFileName(TrayIconPath())}\n");
            }
            if(!anchored)throw new InvalidOperationException("Tray icon anchor unavailable");
            bool light=TaskbarIsLight();
            string chosen=Path.GetFileName(TrayIconPath());
            if(light && chosen!="tray-dark.ico")throw new InvalidOperationException("Light taskbar must get the dark glyph");
            if(!light && chosen!="tray-white.ico")throw new InvalidOperationException("Dark taskbar must get the white glyph");
            tray.OnTrayButton = () => { try { File.WriteAllText(path+".interact","clicked"); } catch { } };
            File.WriteAllText(path+".anchor",$"{anchorX},{anchorY},{(light?"light":"dark")},{chosen}");
            File.WriteAllText(path+".ready","ready");
            for(int wait=0;wait<440 && !File.Exists(path+".done");wait++) {
                await Task.Delay(250);
                if(File.Exists(path+".query")) {
                    try { File.Delete(path+".query"); } catch { }
                    if(tray.TryGetAnchor(out int qx,out int qy))File.WriteAllText(path+".anchor2",$"{qx},{qy}");
                }
            }
            Close();return;
        }
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", name = Path.GetFileName(path);
        await geometryReady;await PresentPreparedAsync();SetCloaked(false);flyoutVisible=true;
        current = new DeviceState(60, true, false, ChargeMode.Conservation, false, false, "Visual fixture", [], true, 2, 165, [60, 165],ThermalMode.Balanced);
        batteryDetails = new BatteryDetails(99.9, 60.0, 59.9, 36, new DateTime(2022, 1, 22), []);
        warranty = new WarrantyResult(new DateTime(2022, 7, 29), new DateTime(2023, 7, 28), null);
        if(args.Contains("--updates-only")) {
            int stateIndex=Array.IndexOf(args,"--update-state");manualUpdateState=args[stateIndex+1];checkingUpdates=manualUpdateState=="checking";
            manualUpdateVersion="v9.0.0";manualUpdateUrl=manualUpdateState=="available" ? "https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe" : null;
            manualUpdateAsset=manualUpdateState=="available" ? new UpdateAsset(manualUpdateUrl!,"FluentVantageToolbar-v9.0.0-installer-win-x64.exe",0,null) : null;
            view=args.Contains("--updates-settings") ? "settings" : "about";Render();await geometryReady;
            if(body.Content is ScrollViewer updateScroll)updateScroll.ChangeView(null,args.Contains("--updates-top") ? 0 : updateScroll.ScrollableHeight,null,true);
            await Task.Delay(700);File.WriteAllText(path+".ready","ready");await Task.Delay(60000);Close();return;
        }
        foreach (var target in new[] { "main", "settings", "battery", "warranty", "about" })
        {
            view = target; Render(); await geometryReady;
            foreach (var button in tileButtons.Values) button.IsEnabled = true;
            await Task.Delay(target == "main" ? 2500 : 1200);
            await SaveImage(target == "main" ? path : Path.Combine(dir, target + "-" + name));
        }
        foreach(var state in new[] { "current", "available", "checking", "error" }) {
            manualUpdateState=state;checkingUpdates=state=="checking";manualUpdateVersion="v9.0.0";
            manualUpdateUrl=state=="available" ? "https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe" : null;
            manualUpdateAsset=state=="available" ? new UpdateAsset(manualUpdateUrl!,"FluentVantageToolbar-v9.0.0-installer-win-x64.exe",0,null) : null;
            view="about";Render();await geometryReady;
            if(body.Content is ScrollViewer updateScroll)updateScroll.ChangeView(null,updateScroll.ScrollableHeight,null,true);
            await Task.Delay(500);
            await SaveImage(Path.Combine(dir,"updates-"+state+"-"+name));
        }
        checkingUpdates=false;manualUpdateState="idle";manualUpdateUrl=null;manualUpdateAsset=null;
        // Verify dynamic height with a single tile row and then with no quick tiles.
        foreach (var count in new[] { 5, 0 }) {
            settings.HiddenTiles = Tiles.Skip(count).Select(t => t.Id).ToList();
            view = "main"; Render(); await geometryReady;
            foreach (var button in tileButtons.Values) button.IsEnabled = true;
            await Task.Delay(1200);
            await SaveImage(Path.Combine(dir, "tiles" + count + "-" + name));
        }
        settings.HiddenTiles=Tiles.Where(t=>t.Id!="thermal").Select(t=>t.Id).ToList();
        foreach(ThermalMode? mode in new ThermalMode?[]{ThermalMode.Quiet,ThermalMode.Balanced,ThermalMode.Performance,null}) {
            current=current! with { Thermal=mode };view="main";Render();await geometryReady;Apply();
            if(tileButtons.TryGetValue("thermal",out var thermal))thermal.IsEnabled=mode.HasValue;
            await Task.Delay(100);await SaveImage(Path.Combine(dir,$"thermal-{mode?.ToString() ?? "unavailable"}-"+name));
        }
        current=current! with { Thermal=ThermalMode.Balanced };
        // Every tile-row/link combination must fit without a main-page scroll host.
        foreach(var count in new[]{7,5,0}) foreach(var warrantyVisible in new[]{true,false}) foreach(var detailsVisible in new[]{true,false}) {
            settings.HiddenTiles=Tiles.Skip(count).Select(t=>t.Id).ToList();settings.ShowWarranty=warrantyVisible;settings.ShowBatteryDetails=detailsVisible;
            view="main";Render();await geometryReady;await Task.Delay(400);
            Root.UpdateLayout();
            if(body.Content is not StackPanel main)throw new InvalidOperationException("Main page must not scroll");
            foreach(FrameworkElement child in main.Children) {
                var bottom=child.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0,child.ActualHeight)).Y;
                if(bottom>Root.ActualHeight+1 || child.ActualHeight+1<child.DesiredSize.Height)
                    throw new InvalidOperationException($"Main child clipped: tiles={count}, warranty={warrantyVisible}, details={detailsVisible}, bottom={bottom}, root={Root.ActualHeight}");
            }
            var lastChild=(FrameworkElement)main.Children.Last();
            var paintedHeight=await PaintedHeightAsync(lastChild);
            var paintedBottom=lastChild.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0,paintedHeight)).Y;
            if(Math.Abs(Root.ActualHeight-paintedBottom-24)>1)throw new InvalidOperationException($"Main painted gap mismatch: {Root.ActualHeight-paintedBottom}");
            var pt=lastChild.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0,0));
            File.AppendAllText(Path.Combine(dir,"layout-"+name+".txt"),$"tiles={count} warranty={warrantyVisible} details={detailsVisible} root={Root.ActualWidth}x{Root.ActualHeight} client={AppWindow.ClientSize.Width}x{AppWindow.ClientSize.Height} canvas={canvas.ActualHeight}/{canvas.DesiredSize.Height} title={titleBar.ActualHeight} body={body.ActualHeight} main={main.ActualHeight}/{main.DesiredSize.Height} inkH={paintedHeight} lastY={pt.Y} lastH={lastChild.ActualHeight}/{lastChild.DesiredSize.Height}\n");
            await SaveImage(Path.Combine(dir,$"main-{count}-warranty{warrantyVisible}-details{detailsVisible}-"+name));
        }
        // Model names are machine-specific and long names must fit the caption too.
        foreach(var model in new[]{"LOQ 15IRX9","Legion Pro 7 16IRX10H Long Device Model Name"}) {
            DeviceInfo.Override=model;settings.HiddenTiles=Tiles.Skip(5).Select(t=>t.Id).ToList();settings.ShowWarranty=false;view="main";Render();await geometryReady;await Task.Delay(400);
            if(titleBar.ActualHeight+1<titleBar.DesiredSize.Height)throw new InvalidOperationException("Dynamic model header clipped");
            await SaveImage(Path.Combine(dir,"model-"+(model.StartsWith("LOQ")?"loq":"long")+"-"+name));
        }
        DeviceInfo.Override="Legion 5 15ITH6H";
        settings.ShowWarranty=true;settings.ShowBatteryDetails=true;
        settings.HiddenTiles.Clear();
        // Low and critical battery colours.
        foreach (var level in new[] { 20, 5 })
        {
            view = "main"; current = current with { Percent = level, Plugged = false, Charging = false }; Render(); await geometryReady;
            foreach (var button in tileButtons.Values) button.IsEnabled = true;
            await Task.Delay(1200);
            await SaveImage(Path.Combine(dir, "level" + level + "-" + name));
        }
        var devicePreview=new DeviceDashboardWindow(settings.Theme,current,true);
        dashboard=devicePreview;view="device";Render();await geometryReady;
        await Task.Delay(1500);
        devicePreview.AssertPreviewReady();
        await SaveImage(Path.Combine(dir,"device-"+name));
        await devicePreview.PreviewPositionAsync(false,true);
        await SaveImage(Path.Combine(dir,"device-revealed-"+name));
        await devicePreview.PreviewPositionAsync(true);
        await SaveImage(Path.Combine(dir,"device-bottom-"+name));

        view="main";settings.HiddenTiles=Tiles.Skip(5).Select(t=>t.Id).ToList();
        foreach(bool off in new[]{false,true}) {
            current=current! with { Muted=off,TouchpadLocked=off };Render();await geometryReady;
            foreach(var button in tileButtons.Values)button.IsEnabled=true;
            await SaveImage(Path.Combine(dir,$"icons-{(off ? "off" : "on")}-{name}"));
        }
        foreach(string accent in new[]{"#00A6A6","#0078D4","#744DA9","#D83B01"}) {
            settings.AccentColor=accent;current=current! with { Percent=60 };view="main";Render();await geometryReady;
            if(BatteryColorFor(60)!=Windows.UI.Color.FromArgb(255,156,218,155) || BatteryColorFor(20)!=Windows.UI.Color.FromArgb(255,252,225,0) || BatteryColorFor(5)!=Windows.UI.Color.FromArgb(255,255,153,164))throw new InvalidOperationException("Battery accent or warning thresholds failed");
            var hoverTile=tileButtons["conserve"];hoverTile.IsEnabled=true;hoverTile.IsChecked=true;
            var normal=((SolidColorBrush)hoverTile.Resources["ToggleButtonBackgroundChecked"]).Color;
            var hover=((SolidColorBrush)hoverTile.Resources["ToggleButtonBackgroundCheckedPointerOver"]).Color;
            if(hover.R!=Math.Max(0,normal.R-16) || hover.G!=Math.Max(0,normal.G-13) || hover.B!=Math.Max(0,normal.B-16))throw new InvalidOperationException("Hover must preserve default tint steps for the current accent");
            VisualStateManager.GoToState(hoverTile,"Checked",false);await Task.Delay(100);
            await SaveImage(Path.Combine(dir,$"tile-accent-{accent.Substring(1,accent.Length-1)}-{name}"));
            VisualStateManager.GoToState(hoverTile,"CheckedPointerOver",false);await Task.Delay(100);
            await SaveImage(Path.Combine(dir,$"tile-hover-{accent.Substring(1,accent.Length-1)}-{name}"));
            settings.AccentColor=accent;view="settings";Render();await geometryReady;await Task.Delay(250);
            await SaveImage(Path.Combine(dir,$"accent-{accent.Substring(1,accent.Length-1)}-{name}"));
        }
        if(body.Content is ScrollViewer settingsScroll) {
            settingsScroll.ChangeView(null,settingsScroll.ScrollableHeight,null,true);await Task.Delay(250);
            await SaveImage(Path.Combine(dir,"settings-bottom-"+name));
        }
        settings.AccentColor=AppSettings.DefaultAccentColor;ApplyAccent();
        if(ParseAccent("#12ABEF") is not {} parsed || parsed.R!=0x12 || ParseAccent("invalid")!=null)throw new InvalidOperationException("Accent parsing failed");
        var previewAsset=new UpdateAsset("https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar/releases/download/v9.0.0/FluentVantageToolbar-v9.0.0-installer-win-x64.exe","FluentVantageToolbar-v9.0.0-installer-win-x64.exe",0,null);
        foreach(var toastState in new[]{"offer","downloading","ready","error"}) {
            using(var scope=new UpdateToastCaptureScope(new UpdateToast("9.0.0",previewAsset,"https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar/releases/latest",Dark))) {
                scope.Window.ShowToast();scope.Window.PreviewState(toastState);await Task.Delay(400);
                var expectedAccent=ParseAccent(AppSettings.DefaultAccentColor)!.Value;
                if(toastState!="downloading" && scope.Window.PinnedAccentColor()!=expectedAccent)throw new InvalidOperationException("Toast accent button lost the current accent colour");
                await scope.Window.CaptureAsync(Path.Combine(dir,$"update-toast-{toastState}-"+name));
            }
        }
        if(args.Contains("--update-live")) {
            // Runs the real toast against the published release, so the download actually happens and the ready state is real.
            using var http=new System.Net.Http.HttpClient { Timeout=TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentVantageToolbar-ci");
            using var liveJson=System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/Star-Dust-0/Fluent-Vantage-Toolbar/releases/latest"));
            var liveAsset=UpdateAssetFor(liveJson.RootElement,new Version(0,3,9),true);
            if(liveAsset==null)throw new InvalidOperationException("Live update preview found no published installer");
            var liveTag=(liveJson.RootElement.GetProperty("tag_name").GetString() ?? "v0.0.0").TrimStart('v','V');
            using(var scope=new UpdateToastCaptureScope(new UpdateToast(liveTag,liveAsset,"https://github.com/Star-Dust-0/Fluent-Vantage-Toolbar/releases/latest",Dark))) {
                scope.Window.ShowToast();await Task.Delay(400);
                await scope.Window.CaptureAsync(Path.Combine(dir,"update-toast-live-offer-"+name));
                scope.Window.Begin();
                var wait=0;
                while(!File.Exists(Path.Combine(UpdateInstaller.Dir,liveAsset.Name)) && wait<180000){await Task.Delay(1000);wait+=1000;}
                if(!File.Exists(Path.Combine(UpdateInstaller.Dir,liveAsset.Name)))throw new InvalidOperationException("Live update download did not finish");
                await Task.Delay(600);
                await scope.Window.CaptureAsync(Path.Combine(dir,"update-toast-live-ready-"+name));
            }
        }
        if(args.Contains("--interactive")) {
            settings.HiddenTiles=Tiles.Skip(5).Select(t=>t.Id).ToList();settings.ShowWarranty=true;
            view="main";Render();await geometryReady;
            var phases=Path.Combine(dir,"interactive-phases.txt");
            await VerifyPointerReorderAsync(dir);
            await Task.WhenAll(NavigateAsync("settings"),NavigateAsync("battery"),NavigateAsync("about"));
            await NavigateAsync("main");
            HideFlyout();
            for(int cycle=0;cycle<3;cycle++) {
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} open {cycle}\n");
                ShowFlyout("main",false);while(showingFlyout)await Task.Delay(10);await Task.Delay(500);
                int before=nativeResizeCount;
                for(int refresh=0;refresh<5;refresh++){Apply();await NextFrameAsync();}
                if(nativeResizeCount!=before)throw new InvalidOperationException("Unchanged refresh resized the window");
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} settings {cycle}\n");
                await NavigateAsync("settings");await Task.Delay(350);
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} main {cycle}\n");
                await NavigateAsync("main");await Task.Delay(350);
                foreach(string page in new[]{"battery","device","warranty","about"}) {
                    File.AppendAllText(phases,$"{DateTime.UtcNow:O} {page} {cycle}\n");
                    await NavigateAsync(page);await Task.Delay(350);
                    File.AppendAllText(phases,$"{DateTime.UtcNow:O} main-from-{page} {cycle}\n");
                    await NavigateAsync("main");await Task.Delay(350);
                }
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} hide {cycle}\n");
                HideFlyout();await Task.Delay(250);
            }
            for(int cycle=0;cycle<20;cycle++) {
                File.AppendAllText(phases,$"{DateTime.UtcNow:O} reopen-stress {cycle}\n");
                ShowFlyout("main",false);while(showingFlyout)await Task.Delay(10);
                if(!flyoutVisible)throw new InvalidOperationException("Reopen was cancelled");
                await Task.Delay(180);HideFlyout();await Task.Delay(260);
            }
            File.AppendAllText(phases,$"PASS serialized geometry, three open/settings/main/hide cycles; unchanged refresh native resize count unchanged. Actual DPI={GetDpiForWindow(hwnd)}\n");
        }
        SetCloaked(true);Close();
    }}
