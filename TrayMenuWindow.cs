using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
namespace FluentLegionToolbar;
internal sealed class TrayMenuWindow : Window
{
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr h);
    public TrayMenuWindow(string theme,(int id,string text)[] items,Action<int> choose,int x,int y)
    {
        Title="Fluent Vantage Toolbar";SystemBackdrop=new MicaBackdrop();ExtendsContentIntoTitleBar=true;
        var panel=new StackPanel{Spacing=2,Padding=new Thickness(8)};
        panel.RequestedTheme=theme switch{"light"=>ElementTheme.Light,"dark"=>ElementTheme.Dark,_=>ElementTheme.Default};
        panel.FlowDirection=L.Ar?FlowDirection.RightToLeft:FlowDirection.LeftToRight;
        foreach(var item in items){if(item.id==4)panel.Children.Add(new Border{Height=1,Margin=new Thickness(8,5,8,5),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(70,128,128,128))});var row=new Button{HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,MinHeight=40,Padding=new Thickness(12,8,12,8),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(6)};var content=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};content.Children.Add(new FontIcon{Glyph=item.id switch{1=>"\uE8A7",2=>"\uE713",3=>"\uE946",_=>"\uE7E8"},FontSize=16});content.Children.Add(new TextBlock{Text=item.text,FontSize=14});row.Content=content;int id=item.id;row.Click+=(_,_)=>{Close();choose(id);};panel.Children.Add(row);}
        Content=panel;
        if(AppWindow.Presenter is OverlappedPresenter presenter){presenter.SetBorderAndTitleBar(true,false);presenter.IsResizable=false;presenter.IsMaximizable=false;presenter.IsMinimizable=false;presenter.IsAlwaysOnTop=true;}
        AppWindow.IsShownInSwitchers=false;double scale=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96d;
        AppWindow.ResizeClient(new SizeInt32((int)(256*scale),(int)(199*scale)));
        var area=DisplayArea.GetFromPoint(new PointInt32(x,y),DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(Math.Clamp(x-AppWindow.Size.Width,area.X,Math.Max(area.X,area.X+area.Width-AppWindow.Size.Width)),Math.Clamp(y-AppWindow.Size.Height,area.Y,Math.Max(area.Y,area.Y+area.Height-AppWindow.Size.Height))));
        Activated+=(_,e)=>{if(e.WindowActivationState==WindowActivationState.Deactivated)Close();};
        panel.KeyDown+=(_,e)=>{if(e.Key==Windows.System.VirtualKey.Escape)Close();};
    }
}