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


    void ResizeForContent() => geometryReady=FitGeometrySafeAsync(++geometryGeneration);
    async Task FitGeometrySafeAsync(int generation) { try { await FitGeometryAsync(generation); } catch (Exception ex) { DiagnosticLog.Write("FitGeometry failed: " + ex.Message); } }

    static Task NextFrameAsync()
    {
        var done=new TaskCompletionSource<bool>();
        EventHandler<object>? handler=null;
        handler=(_,_)=>{CompositionTarget.Rendering-=handler;done.TrySetResult(true);};
        CompositionTarget.Rendering+=handler;
        return WaitFrameAsync(done.Task,()=>CompositionTarget.Rendering-=handler);
    }
    static async Task WaitFrameAsync(Task frame,Action cleanup)
    {
        // Hidden windows can stop compositor ticks. Do not leave geometry blocked.
        await Task.WhenAny(frame,Task.Delay(34));cleanup();
    }

    async Task FitGeometryAsync(int generation)
    {
        await geometryLock.WaitAsync();
        try {
            if(generation!=geometryGeneration)return;
            // Finish templates and arrange natural content before touching the HWND.
            await NextFrameAsync();
            if(generation!=geometryGeneration)return;
            Root.UpdateLayout();
            double scale=Math.Max(1,GetDpiForWindow(hwnd)/96d);
            {
                // Window chrome is outer size minus client size. Never derive it from Root.ActualWidth:
                // at logon the cloaked window may not be laid out yet (ActualWidth 0), which made the "frame"
                // equal to the whole window and doubled the size to full screen.
                var outer=AppWindow.Size;var client=AppWindow.ClientSize;
                double fw=outer.Width-client.Width,fh=outer.Height-client.Height;
                double limit=48*scale;
                frameWidthPixels=(fw>=0&&fw<=limit)?fw:0;
                frameHeightPixels=(fh>=0&&fh<=limit)?fh:0;
            }
            canvas.Height=double.NaN;
            canvas.RowDefinitions[1].Height=view=="main"?GridLength.Auto:new GridLength(1,GridUnitType.Star);
            canvas.Measure(new Windows.Foundation.Size(472,double.PositiveInfinity));
            double naturalHeight=view=="main"?canvas.DesiredSize.Height:520+Math.Max(36,titleBar.DesiredSize.Height);
            canvas.Arrange(new Windows.Foundation.Rect(24,15,472,naturalHeight));
            double wanted=naturalHeight+Root.Padding.Top+Root.Padding.Bottom;
            if(view=="main" && body.Content is StackPanel main) {
                var last=(FrameworkElement)main.Children.Last();
                double ink=await PaintedHeightAsync(last);
                wanted=last.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0,ink)).Y-canvas.Margin.Top+24;
            }
            if(generation!=geometryGeneration)return;
            // At logon the cloaked window may not be laid out yet, so the painted-ink measure can come back near zero.
            // Never let that produce a tiny (invisible) window: fall back to the measured natural height, with a floor.
            if(!preview) {
                double natural=naturalHeight+Root.Padding.Top+Root.Padding.Bottom;
                if(double.IsNaN(wanted)||double.IsInfinity(wanted)||wanted<natural*0.6)wanted=natural;
                wanted=Math.Max(wanted,200);
            }
            var work=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest).WorkArea;
            wanted=Math.Min(wanted,work.Height/scale-16);
            var desired=new SizeInt32((int)Math.Round(520*scale+frameWidthPixels),(int)Math.Round(wanted*scale+frameHeightPixels));
            desired=new SizeInt32(Math.Min(desired.Width,(int)Math.Round(520*scale+48*scale)),Math.Min(desired.Height,work.Height));
            {
                if(AppWindow.Size.Width!=desired.Width || AppWindow.Size.Height!=desired.Height) {
                    AppWindow.Resize(desired);nativeResizeCount++;
                }
                Root.UpdateLayout();await NextFrameAsync();Root.UpdateLayout();await NextFrameAsync();
                if(!preview && tray!=null && tray.TryGetAnchor(out int x,out int y)) {
                    var area=DisplayArea.GetFromPoint(new PointInt32(x,y),DisplayAreaFallback.Nearest).WorkArea;
                    int left=Math.Clamp(x-AppWindow.Size.Width/2,area.X,Math.Max(area.X,area.X+area.Width-AppWindow.Size.Width));
                    int top=Math.Clamp(y-AppWindow.Size.Height-16,area.Y,Math.Max(area.Y,area.Y+area.Height-AppWindow.Size.Height-12));
                    if(AppWindow.Position.X!=left || AppWindow.Position.Y!=top)AppWindow.Move(new PointInt32(left,top));
                }
            }
            if(preview && args.Contains("--interactive")) {
                if(!GetWindowRect(hwnd,out var actual) || actual.Right-actual.Left!=desired.Width || actual.Bottom-actual.Top!=desired.Height)throw new InvalidOperationException("Native bounds differ from settled content size");
                var region=CreateRectRgn(0,0,0,0);int kind=GetWindowRgn(hwnd,region);DeleteObject(region);
                if(kind!=0 || canvas.Margin.Top!=0)throw new InvalidOperationException("Retained region or canvas offset");
                var dir=Path.GetDirectoryName(args[Array.IndexOf(args,"--capture")+1])!;
                File.AppendAllText(Path.Combine(dir,"navigation-native-bounds.csv"),$"{view},{actual.Left},{actual.Top},{actual.Right},{actual.Bottom},region={kind},offset={canvas.Margin.Top},dpi={GetDpiForWindow(hwnd)}\n");
            }
        } finally {geometryLock.Release();}
    }

    async Task<double> PaintedHeightAsync(FrameworkElement element)
    {
        var bitmap=new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer=await bitmap.GetPixelsAsync();
        using var reader=Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        var pixels=new byte[buffer.Length];reader.ReadBytes(pixels);
        for(int y=bitmap.PixelHeight-1;y>=0;y--)for(int x=0;x<bitmap.PixelWidth;x++)
            if(pixels[(y*bitmap.PixelWidth+x)*4+3]>16)
                return (y+1)*element.ActualHeight/bitmap.PixelHeight;
        return element.ActualHeight;
    }}
