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

    async Task VerifyPointerReorderAsync(string dir)
    {
        reorderMode=true;await NavigateAsync("settings");Render();await geometryReady;
        if(body.Content is ScrollViewer scroll && reorderRows is {} rows) {
            Activate();SetForegroundWindow(hwnd);Root.UpdateLayout();await Task.Delay(150);
            double offset=rows.TransformToVisual((UIElement)scroll.Content).TransformPoint(new Windows.Foundation.Point()).Y;
            scroll.ChangeView(null,Math.Max(0,offset-80),null,true);await Task.Delay(200);
            for(int pass=0;pass<2;pass++) {
                var from=(FrameworkElement)rows.Children[pass==0?0:2];var to=(FrameworkElement)rows.Children[pass==0?2:0];
                string id=(string)from.Tag;
                var h=(FrameworkElement)((Grid)from).Children[0];
                var start=h.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(h.ActualWidth/2,h.ActualHeight/2));
                var end=to.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(h.ActualWidth/2,to.ActualHeight/2+(pass==0?12:-12)));
                if(!GetWindowRect(hwnd,out var rect))throw new InvalidOperationException("Drag bounds unavailable");
                double scale=GetDpiForWindow(hwnd)/96d;
                var origin=new NativePoint();ClientToScreen(hwnd,ref origin);rect.Left=origin.X;rect.Top=origin.Y;
                if(Root.FlowDirection==FlowDirection.RightToLeft){start.X=Root.ActualWidth-start.X;end.X=Root.ActualWidth-end.X;}
                SetCursorPos(rect.Left+(int)(start.X*scale),rect.Top+(int)(start.Y*scale));mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(120);
                for(int step=1;step<=12;step++) {mouse_event(0x8001,(uint)((rect.Left+(start.X+(end.X-start.X)*step/12)*scale)*65535/(GetSystemMetrics(0)-1)),(uint)((rect.Top+(start.Y+(end.Y-start.Y)*step/12)*scale)*65535/(GetSystemMetrics(1)-1)),0,UIntPtr.Zero);await Task.Delay(25);}
                mouse_event(4,0,0,0,UIntPtr.Zero);await Task.Delay(180);
                await SaveImage(Path.Combine(dir,$"reorder-attempt-{pass}.png"));
                File.AppendAllText(Path.Combine(dir,"reorder-input.txt"),$"pass={pass}; start={start}; end={end}; rect={rect.Left},{rect.Top}; pressed={reorderPresses}; moved={reorderMoves}; released={reorderReleases}; order={string.Join(",",settings.TileOrder)}; trace={reorderTrace}\n");
                if(settings.TileOrder.Count!=Tiles.Length || settings.TileOrder[pass==0?2:0]!=id){Close();throw new InvalidOperationException("Actual pointer reorder did not move expected row");}
                await SaveImage(Path.Combine(dir,$"reorder-pointer-{pass}.png"));
            }
            string saved=System.Text.Json.JsonSerializer.Serialize(settings);var loaded=System.Text.Json.JsonSerializer.Deserialize<AppSettings>(saved)!;
            if(!loaded.TileOrder.SequenceEqual(settings.TileOrder))throw new InvalidOperationException("Order serialization failed");
            File.WriteAllText(Path.Combine(dir,"reorder-pointer.txt"),$"PASS actual mouse press/move/release twice; administrator={Startup.IsAdmin()}; order={string.Join(",",settings.TileOrder)}; serialization roundtrip passed\n");
        } else throw new InvalidOperationException("Reorder rows missing");
        reorderMode=false;await NavigateAsync("main");
    }

    void FinishReorder(bool commit)
    {
        if(!dragging||dragRow==null||dragRows==null)return;
        var row=dragRow;var rows=dragRows;
        reorderTrace+=$" finish={commit},shift={dragShift?.Y};";dragging=false;reordering=false;
        row.RenderTransform=new TranslateTransform();row.Opacity=1;row.BorderThickness=new Thickness(0);
        if(commit) {
            int old=rows.Children.IndexOf(row);double midpoint=dragStartY+(dragShift?.Y??0)+row.ActualHeight/2;
            int target=0;foreach(var child in rows.Children.Cast<FrameworkElement>()) {
                if(ReferenceEquals(child,row))continue;
                double y=child.TransformToVisual(rows).TransformPoint(new Windows.Foundation.Point()).Y;
                if(midpoint>y+child.ActualHeight/2+1)target++;
            }
            if(target!=old){rows.Children.Remove(row);rows.Children.Insert(target,row);}
            settings.TileOrder=rows.Children.Cast<FrameworkElement>().Select(r=>(string)r.Tag).ToList();if(!preview)settings.Save();
        }
        dragRow=null;dragRows=null;dragShift=null;
        Root.ReleasePointerCaptures();
    }

    void AttachReorderPointer(Border handle,Grid row,StackPanel rows)
    {
        if(!reorderHandlersAttached) {
            reorderHandlersAttached=true;
            Root.PointerMoved+=(_,e)=>{reorderMoves++;if(!dragging||dragRow==null||dragRows==null||dragShift==null)return;double y=e.GetCurrentPoint(dragRows).Position.Y;dragShift.Y=Math.Clamp(y-dragStartY-dragRow.ActualHeight/2,-dragStartY,Math.Max(0,dragRows.ActualHeight-dragStartY-dragRow.ActualHeight));e.Handled=true;};
            Root.PointerReleased+=(_,e)=>{reorderReleases++;bool wasDragging=dragging;FinishReorder(true);if(wasDragging)e.Handled=true;};
            Root.PointerCanceled+=(_,_)=>FinishReorder(false);Root.PointerCaptureLost+=(_,_)=>FinishReorder(false);
        }
        handle.PointerPressed+=(_,e)=>{
            reorderPresses++;reorderTrace+=$" press:mode={reorderMode},left={e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed};";if(!reorderMode || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)return;
            dragging=Root.CapturePointer(e.Pointer);reorderTrace+=$" captured={dragging};";if(!dragging)return;reordering=true;
            dragRow=row;dragRows=rows;
            dragStartY=row.TransformToVisual(rows).TransformPoint(new Windows.Foundation.Point()).Y;
            dragShift=new TranslateTransform();row.RenderTransform=dragShift;row.Opacity=.8;
            row.BorderBrush=Primary;row.BorderThickness=new Thickness(1);e.Handled=true;
        };
    }}
