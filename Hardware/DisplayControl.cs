using WindowsDisplayAPI;
using WindowsDisplayAPI.DisplayConfig;
using WindowsDisplayAPI.Native.DisplayConfig;
using WindowsDisplayAPI.Native.DeviceContext;
namespace FluentLegionToolbar.Hardware;
static class DisplayControl
{
    static string SourceName(string monitorName) {
        int suffix = monitorName.IndexOf('\\', 4);
        return suffix < 0 ? monitorName : monitorName[..suffix];
    }
    static Display Internal()
    {
        var displays = Display.GetDisplays().ToArray();
        foreach (var display in displays) DiagnosticLog.Write($"Display device={display.DeviceName} source={SourceName(display.DeviceName)} path={display.DevicePath}");
        var paths = PathInfo.GetActivePaths();
        var names = paths.Where(p => p.TargetsInfo.Any(t => t.OutputTechnology is DisplayConfigVideoOutputTechnology.Internal or DisplayConfigVideoOutputTechnology.DisplayPortEmbedded))
            .Select(p => p.DisplaySource.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in paths) DiagnosticLog.Write($"Display path source={path.DisplaySource.DisplayName} output={string.Join(",", path.TargetsInfo.Select(t => t.OutputTechnology))}");
        var candidates = displays.Where(d => names.Contains(SourceName(d.DeviceName), StringComparer.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 1) return candidates[0];
        // No arbitrary primary-screen fallback: that could change an external monitor.
        throw new DeviceMessageException("The laptop screen could not be identified safely, so nothing was changed. External displays are never changed.", "تعذّر تحديد شاشة الحاسوب المحمول بأمان، لذلك لم يتم تغيير شيء. لا يتم تغيير الشاشات الخارجية أبدًا.");
    }
    public static (int,int[]) Read()
    {
        var d=Internal();var c=d.CurrentSetting;
        var rates=d.GetPossibleSettings().Where(m=>m.Resolution==c.Resolution&&m.ColorDepth==c.ColorDepth&&m.IsInterlaced==c.IsInterlaced).Select(m=>m.Frequency).Distinct().OrderBy(x=>x).ToArray();
        DiagnosticLog.Write($"Refresh current={c.Frequency} supported={string.Join(",", rates)}");
        return(c.Frequency,rates);
    }
    public static void Set(int hz)
    {
        var d=Internal();var c=d.CurrentSetting;
        var match=d.GetPossibleSettings().FirstOrDefault(m=>m.Resolution==c.Resolution&&m.ColorDepth==c.ColorDepth&&m.IsInterlaced==c.IsInterlaced&&m.Frequency==hz)??throw new NotSupportedException("Requested refresh rate is not available at the current resolution.");
        d.SetSettings(new DisplaySetting(match,c.Position,c.Orientation,DisplayFixedOutput.Default),true);
        // The mode change can take a moment to show up; poll briefly before calling it unconfirmed.
        for(int i=0;i<10;i++){if(Internal().CurrentSetting.Frequency==hz)return;Thread.Sleep(100);}
        if(Internal().CurrentSetting.Frequency!=hz)throw new InvalidOperationException("Refresh rate change was not confirmed.");
    }
}



