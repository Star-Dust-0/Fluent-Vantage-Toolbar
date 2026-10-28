# Implementation evidence

- https://github.com/sahidhh/lenovo-battery-tray : MIT, explicitly excludes Legion / LOQ. Concept reference only.
- https://github.com/BartoszCichecki/LenovoLegionToolkit : GPL-3.0; protocol/behavior evidence, no implementation code copied. 82JH mapped to 15ITH6H; touchpad support/query/set WMI names; microphone uses Core Audio; charge mode uses EnergyDrv.
- https://github.com/dantmnf/OpenLenovoSettings : MIT; independent EnergyDrv charging protocol evidence, conservation bit 0x20 and express bit 0x04, commands 3/5 and 7/8. Conservation support bits vary by generation, so successful state reads do not prove write support. Writes are read back and failure is surfaced.
- https://psref.lenovo.com/syspool/Sys/PDF/Legion/Lenovo_Legion_5_15ITH6H/Lenovo_Legion_5_15ITH6H_Spec.html : physical e-camera shutter switch.
- https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops : native WinUI MicaBackdrop.
- https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font : Windows 11 glyph font.
- https://github.com/StarsDust01/fluent-prayer-times : MIT, same owner's tray shell infrastructure reused with license retained. No prayer/location data included.

- LenovoLegionToolkit (GPL-3.0), protocol evidence only, no code copied, for: Fn Lock (EnergyDrv IOCTL 0x831020E8, query 2, bit 10, set 0xE/0xF), Lenovo battery record (IOCTL 0x83102138, manufacture date field), warranty lookup (pcsupport.lenovo.com getIbaseInfo endpoint, serial number and machine type from Win32_ComputerSystemProduct).
- Battery capacity and cycle count come from Windows WMI (root\WMI BatteryStaticData, BatteryFullChargedCapacity, BatteryCycleCount).

No claim of target-device runtime verification is made.



## 0.2.1 control-path audit
Reference source reviewed at LLT commit d0c57bfc7ecc26d4ccf15bb3835fed7ac28a259f. GPL source was used to check public interface facts, not copied into this MIT implementation.

- Fn Lock: EnergyDrv IOCTL 0x831020E8, query 2, bit 10, commands 0xE / 0xF. Legacy 82JH keeps the hardware polarity; newer mapped Legion series invert it. Driver requests are serialized with a 20ms cooldown. https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/FnLockFeature.cs
- Conservation / Rapid charge: IOCTL 0x831020F8, query 0xFF. Bit 0x20 means Conservation; bit 0x04 means Rapid; neither means Normal. Command sequences: Conservation 8,3; Rapid 5,7; Normal 5,8. The old additional Normal-mode support-bit check was wrong. https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/BatteryFeature.cs
- Always-on USB: settings query 2, raw bit 7 is enabled, raw bit 15 is always mode. Commands Off B,12; Always A,13. Removed invented support-bit gates. https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/AlwaysOnUsbFeature.cs
- Touchpad: prefer LENOVO_UTILITY_DATA.GetIfSupportOrVersion(datatype=0x12), version >=0x18. Read HKCU PrecisionTouchPad/Status Enabled; use Ctrl+Win+F24 via SendInput to toggle only when needed. Fallback: LENOVO_GAMEZONE_DATA IsSupportDisableTP/GetTPStatus/SetTPStatus. Never write the registry directly. https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/PrecisionTouchpadLockFeature.cs
- Microphone: Core Audio, enumerate all active capture endpoints, mute every endpoint, verify each, log name/id/before/requested/after. This was already the same API family as LLT; no claim that an untested new driver path solves the reported live failure. https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/MicrophoneFeature.cs
- Refresh rate: WindowsDisplayAPI, active internal/eDP path only, same resolution/depth/interlace, verify after SetSettings. No external monitor is changed. https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/Features/RefreshRateFeature.cs
- Touchpad glyph: native Segoe Fluent Icons EFA5 (Touchpad), replaces unrelated E7C9. https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font

Live hardware effects still need owner retesting. CI fixtures prove layout only. Diagnostic log: %LOCALAPPDATA%/FluentLegionToolbar/diagnostic.log (rotated at 2MB), readable from Settings. It contains hardware/provider errors and local audio endpoint identifiers, no credentials or network uploads.
