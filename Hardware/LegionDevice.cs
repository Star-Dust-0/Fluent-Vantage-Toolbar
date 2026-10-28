using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using Microsoft.Win32;
namespace FluentLegionToolbar.Hardware;
// Protocol facts are documented in SOURCES.md. No Lenovo services are stopped or replaced.
public enum ThermalMode { Quiet=1, Balanced=2, Performance=3 }
public enum ChargeMode { Normal, Conservation, Rapid }
public sealed record DeviceState(int? Percent, bool Plugged, bool Charging, ChargeMode? Mode, bool? Muted, bool? TouchpadLocked, string Model, string[] Problems, bool? FnLocked = null, int? UsbMode = null, int? RefreshHz = null, int[]? AvailableHz = null, ThermalMode? Thermal = null);
public sealed class LegionDevice
{
    [StructLayout(LayoutKind.Sequential)] struct PowerStatus { public byte AC, Flags, Percent, Reserved; public uint Life, FullLife; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref uint input, uint inputLength, out uint output, uint outputLength, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref uint input, uint inputLength, byte[] output, uint outputLength, out uint returned, IntPtr overlapped);
    readonly SemaphoreSlim gate = new(1);
    readonly object driverQueue = new();
    /// <summary>Reads one Lenovo battery record (EnergyDrv IOCTL 0x83102138). Offsets: temperature 14, manufacture date 16, first use 18.</summary>
    public static (ushort Temperature, ushort Manufacture, ushort FirstUse)? ReadLenovoBatteryRecord(uint index)
    {
        using var h = CreateFile(@"\\.\EnergyDrv", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new byte[84];
        if (!DeviceIoControl(h, 0x83102138, ref index, 4, buffer, (uint)buffer.Length, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ushort Read(int offset) => BitConverter.ToUInt16(buffer, offset);
        return (Read(14), Read(16), Read(18));
    }
    bool targetModel;
    bool fnDisplayState = true; // Owner-requested initial UI assumption, not a firmware write.
    bool FnInverted => DeviceInfo.FnInverted;
    uint Exchange(uint command, uint ioctl = 0x831020F8)
    {
        lock (driverQueue) {
        if (!targetModel) throw new InvalidOperationException("This device is not the expected Lenovo 82JH / 15ITH6H.");
        using var h = CreateFile(@"\\.\EnergyDrv", 3, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        bool success = DeviceIoControl(h, ioctl, ref command, 4, out uint response, 4, out uint returned, IntPtr.Zero);
        int error = success ? 0 : Marshal.GetLastWin32Error();
        DiagnosticLog.Write($"EnergyDrv ioctl=0x{ioctl:X} command=0x{command:X} success={success} bytes={returned} error={error}");
        if (!success) throw new Win32Exception(error);
        bool query = (ioctl == 0x831020F8 && command == 255) || (ioctl == 0x831020E8 && command == 2);
        if (query && returned < 4) throw new InvalidOperationException($"EnergyDrv query returned {returned} bytes instead of a state value.");
        // Setter success has no output-payload requirement. Separate state queries verify the effect.
        DiagnosticLog.Write($"EnergyDrv ioctl=0x{ioctl:X} in=0x{command:X} out=0x{response:X}");
        Thread.Sleep(20); return response;
        }
    }
    ChargeMode ReadMode()
    {
        uint raw = Exchange(255);
        if ((raw & 0x20) != 0) return ChargeMode.Conservation;
        if ((raw & 0x04) != 0) return ChargeMode.Rapid;
        // Toolkit interprets bit 17 after a byte swap as charge mode enabled.
        // A successful query with neither mode bit is Normal, as documented by LLT.
        return ChargeMode.Normal;
    }
    int Touchpad(string method, int? input = null)
    {
        if (!targetModel) throw new InvalidOperationException("Not the expected Legion model.");
        using var search = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_GAMEZONE_DATA");
        using var rows = search.Get();
        var instance = rows.Cast<ManagementObject>().FirstOrDefault() ?? throw new InvalidOperationException("Lenovo WMI provider is unavailable.");
        using (instance)
        {
            using var parameters = instance.GetMethodParameters(method);
            if (input.HasValue) parameters["Data"] = input.Value;
            using var result = instance.InvokeMethod(method, parameters, null);
            return input.HasValue ? input.Value : Convert.ToInt32(result["Data"]);
        }
    }
    bool ReadMicrophone()
    {
        using var audio = new MMDeviceEnumerator();
        var endpoints = audio.EnumerateAudioEndPoints(DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active);
        if (endpoints.Count == 0) throw new InvalidOperationException("No active microphone endpoint.");
        bool muted = true;
        foreach (var device in endpoints) { using (device) muted &= device.AudioEndpointVolume.Mute; }
        return muted;
    }
    public async Task<DeviceState> ReadAsync() => await Task.Run(() =>
    {
        var errors = new List<string>(); string model = "Unknown device";
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"); using var rows = q.Get();
            foreach (ManagementObject row in rows) { using(row) { model = Convert.ToString(row["Model"]) ?? model; targetModel = DeviceInfo.IsSupported; } }
        } catch (Exception e) { errors.Add(e.Message); targetModel = false; }
        int? percent=null; bool plugged=false, charging=false;
        if (GetSystemPowerStatus(out var power)) { percent = power.Percent <= 100 ? power.Percent : null; plugged = power.AC == 1; charging = power.Flags != 255 && (power.Flags & 8) != 0; }
        ChargeMode? mode=null; bool? muted=null, locked=null;
        try { mode=ReadMode(); } catch(Exception e) { errors.Add("Battery mode: "+e.Message); }
        try { locked = ReadTouchpad(); } catch(Exception e) { errors.Add("Touchpad: "+e.Message); }
        try { muted=ReadMicrophone(); } catch(Exception e) { errors.Add("Microphone: "+e.Message); }
        bool? fn=null;int? usb=null,hz=null;int[] rates=[];
        try { uint settings=Exchange(2,0x831020E8); fn=((settings&1024)!=0) ^ FnInverted; } catch(Exception e) { errors.Add("Fn Lock: "+e.Message); }
        try { uint settings=Exchange(2,0x831020E8); usb=(settings&128)==0?0:(settings&32768)!=0?2:1; } catch(Exception e) { errors.Add("USB: "+e.Message); }
        try {(hz,rates)=DisplayControl.Read();}catch(Exception e){errors.Add("Display: "+e.Message);}
        ThermalMode? thermal=null;
        try { if(Touchpad("IsSupportSmartFan")>0)thermal=DecodeThermal(Touchpad("GetSmartFanMode")); }catch(Exception e){errors.Add("Thermal mode: "+e.Message);}
        foreach (var error in errors) DiagnosticLog.Write(error);
        DiagnosticLog.Write($"State model={model} type={DeviceInfo.MachineType} supported={targetModel} fn={fn} micMuted={muted} touchpadLocked={locked} usb={usb} hz={hz} mode={mode}");
        return new DeviceState(percent, plugged, charging, mode, muted, locked, model, errors.ToArray(), fn.HasValue ? fnDisplayState : null,usb,hz,rates,thermal);
    });
    // Same control path as the reference toolkit: EnergyDrv settings IOCTL, query 2, bit 10 = Fn Lock, set 0xE = on, 0xF = off.
    // No support bit is required first; the query itself succeeding is the capability check. The state is read back with retries.
    public Task SetFnAsync(bool locked) => Task.Run(async () => {
        Exchange(2,0x831020E8);
        bool hardwareLocked = locked ^ FnInverted;
        Exchange(hardwareLocked?14u:15u,0x831020E8);
        for(int i=0;i<10;i++) {
            await Task.Delay(100);
            if(((Exchange(2,0x831020E8)&1024)!=0)==hardwareLocked) { fnDisplayState = locked; return; }
        }
        throw new InvalidOperationException("Fn Lock state was not confirmed.");
    });
    public Task SetUsbAsync(bool enabled) => Task.Run(() => {
        Exchange(2,0x831020E8);
        Exchange(enabled?10u:11u,0x831020E8);Exchange(enabled?19u:18u,0x831020E8);
        uint raw=Exchange(2,0x831020E8);int mode=(raw&128)==0?0:(raw&32768)!=0?2:1;
        if(mode!=(enabled?2:0))throw new InvalidOperationException("Always-on USB setting was not confirmed.");
    });
    public static ThermalMode DecodeThermal(int raw) => raw switch {
        1=>ThermalMode.Quiet,2=>ThermalMode.Balanced,3=>ThermalMode.Performance,
        _=>throw new InvalidOperationException($"Unknown thermal mode value: {raw}")
    };
    public static ThermalMode NextThermal(ThermalMode current) => current switch {
        ThermalMode.Balanced=>ThermalMode.Performance,ThermalMode.Performance=>ThermalMode.Quiet,ThermalMode.Quiet=>ThermalMode.Balanced,
        _=>throw new InvalidOperationException("Unknown thermal mode cannot be changed")
    };
    public Task CycleThermalAsync() => Task.Run(async ()=> {
        await gate.WaitAsync();
        try {
            if(Touchpad("IsSupportSmartFan")<=0)throw new InvalidOperationException("Thermal mode is not supported by the provider");
            var previous=DecodeThermal(Touchpad("GetSmartFanMode"));var next=NextThermal(previous);
            if(next==ThermalMode.Performance && (!GetSystemPowerStatus(out var power) || power.AC!=1))throw new InvalidOperationException("Performance mode requires AC power");
            // Match Toolkit's documented firmware workaround for affected models.
            if(previous==ThermalMode.Quiet && next==ThermalMode.Performance){Touchpad("SetSmartFanMode",2);await Task.Delay(500);}
            Touchpad("SetSmartFanMode",(int)next);
            for(int attempt=0;attempt<10;attempt++) {
                await Task.Delay(100);
                if(DecodeThermal(Touchpad("GetSmartFanMode"))==next){DiagnosticLog.Write($"Thermal mode verified: {previous} -> {next}");return;}
            }
            throw new InvalidOperationException("Thermal mode change was not confirmed by the firmware");
        } finally {gate.Release();}
    });
    public Task SetRefreshAsync(int hz) => Task.Run(()=>DisplayControl.Set(hz));
    public async Task SetModeAsync(ChargeMode next)
    {
        await gate.WaitAsync();
        try { await Task.Run(async () => {
            var before=ReadMode();
            if (before == next) return;
            uint[] commands = next switch { ChargeMode.Conservation => [8u, 3u], ChargeMode.Rapid => [5u, 7u], _ => [5u, 8u] };
            foreach (var command in commands) Exchange(command);
            for(int i=0;i<10;i++) { await Task.Delay(100); if(ReadMode()==next) return; }
            throw new InvalidOperationException("Firmware did not confirm the requested mode. Lenovo Vantage may be overriding it.");
        }); } finally { gate.Release(); }
    }
    bool PrecisionTouchpadSupported()
    {
        try {
            using var search = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM LENOVO_UTILITY_DATA");
            using var rows = search.Get();
            foreach (ManagementObject row in rows) using (row) {
                using var input = row.GetMethodParameters("GetIfSupportOrVersion"); input["datatype"] = 0x12;
                using var result = row.InvokeMethod("GetIfSupportOrVersion", input, null);
                uint value = Convert.ToUInt32(result["Data"]); DiagnosticLog.Write($"PrecisionTouchpad version=0x{value:X}"); return value >= 0x18;
            }
        } catch (Exception e) { DiagnosticLog.Write("PrecisionTouchpad probe: " + e.Message); }
        return false;
    }
    bool ReadTouchpad()
    {
        if (PrecisionTouchpadSupported()) {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status");
            if (key?.GetValue("Enabled") is int enabled) return enabled == 0;
            throw new InvalidOperationException("Precision touchpad status is absent for the current Windows user.");
        }
        if (Touchpad("IsSupportDisableTP") <= 0) throw new NotSupportedException("No supported touchpad control path.");
        int value = Touchpad("GetTPStatus");
        if (value is not (0 or 1)) throw new InvalidOperationException("Unknown touchpad state: " + value);
        return value == 1;
    }
    [StructLayout(LayoutKind.Sequential)] struct KeyInput { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct Input {
        [FieldOffset(0)] public uint type; [FieldOffset(8)] public KeyInput keyboard;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] input, int size);
    public Task SetTouchpadAsync(bool locked) => Task.Run(async () => {
        if (ReadTouchpad() == locked) return;
        if (PrecisionTouchpadSupported()) {
            ushort[] keys = [0x11, 0x5B, 0x87, 0x87, 0x5B, 0x11];
            var inputs = keys.Select((key, i) => new Input { type = 1, keyboard = new KeyInput { key = key, flags = i >= 3 ? 2u : 0u } }).ToArray();
            uint sent = SendInput(6, inputs, Marshal.SizeOf<Input>());
            DiagnosticLog.Write($"Touchpad Ctrl+Win+F24 sent={sent}/6 error={Marshal.GetLastWin32Error()}");
            if (sent != 6) throw new Win32Exception(Marshal.GetLastWin32Error());
        } else Touchpad("SetTPStatus", locked ? 1 : 0);
        for (int i = 0; i < 10; i++) { await Task.Delay(100); if (ReadTouchpad() == locked) return; }
        throw new InvalidOperationException("Touchpad state was not confirmed. Check diagnostic.log.");
    });
    public Task SetMicrophoneAsync(bool muted) => Task.Run(() => {
        using var audio=new MMDeviceEnumerator(); var endpoints=audio.EnumerateAudioEndPoints(DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active);
        if(endpoints.Count==0) throw new InvalidOperationException("No active microphone.");
        var failures = new List<string>();
        foreach(var device in endpoints) { using(device) { try {
            bool before = device.AudioEndpointVolume.Mute;
            device.AudioEndpointVolume.Mute=muted; bool after = device.AudioEndpointVolume.Mute;
            DiagnosticLog.Write($"Microphone {device.FriendlyName} id={device.ID} before={before} requested={muted} after={after}");
            if(after!=muted) failures.Add(device.FriendlyName + ": mute readback mismatch");
        } catch (Exception e) { failures.Add(device.FriendlyName + ": " + e.Message); } } }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
    });
}
