using System.Runtime.InteropServices;

namespace FluentLegionToolbar;

/// <summary>Notification-area icon built on Shell_NotifyIcon, hooked to an existing window via a subclass.</summary>
sealed class TrayIcon : IDisposable
{
    const uint WM_TRAY = 0x8001;
    const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MOUSEMOVE = 0x0200;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_GUID = 0x20;
    // Stable identity: Windows remembers placement/promotion per GUID, so the icon keeps its tray position across restarts.
    static readonly Guid TrayGuid = new Guid("3F6E2A41-7C5B-4D2E-9A18-5B0C4D7E9F01");
    /// <summary>Test-only override set before the first TrayIcon is created; production always uses TrayGuid.</summary>
    public static Guid? OverrideGuid;
    Guid IconGuid => OverrideGuid ?? TrayGuid;

    delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);

    public Func<bool>? IsDark;
    /// <summary>Fired on any tray button press; used by capture tests to prove callback routing.</summary>
    public Action? OnTrayButton;
    [DllImport("uxtheme.dll", EntryPoint="#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint="#136")] static extern void FlushMenuThemes();
    public bool NativeTip = false;   // the custom hover card replaces the system tooltip
    int _lastX = int.MinValue, _lastY = int.MinValue;

    /// <summary>Center of the tray icon on screen (physical pixels). Falls back to the cursor position at the last tray event.</summary>
    public bool TryGetAnchor(out int x, out int y)
    {
        try
        {
            var id = new NOTIFYICONIDENTIFIER { hWnd = _hwnd, uID = 1, guidItem = IconGuid };
            id.cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>();
            if (Shell_NotifyIconGetRect(ref id, out var r) == 0 && r.right > r.left && r.bottom > r.top)
            {
                x = (r.left + r.right) / 2; y = (r.top + r.bottom) / 2; return true;
            }
        }
        catch { }
        if (_lastX != int.MinValue) { x = _lastX; y = _lastY; return true; }
        x = 0; y = 0; return false;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA d);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr h, SubclassProc p, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr m, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int r, IntPtr hwnd, IntPtr rc);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string s);

    readonly IntPtr _hwnd;
    readonly Action _onClick;
    readonly Action<int> _onMenu;
    readonly Func<int, int, bool>? _onContext;
    public Action? OnHover;
    public Action? OnSystemThemeChanged;
    (int id, string text)[] _items;
    public void SetItems((int id, string text)[] items) => _items = items;
    readonly SubclassProc _proc;
    readonly uint _taskbarCreated;
    IntPtr _icon;
    string _tip = "";
    bool _added;

    public TrayIcon(IntPtr hwnd, string icoPath, (int id, string text)[] items, Action onClick, Action<int> onMenu, Func<int, int, bool>? onContext = null)
    {
        _hwnd = hwnd; _items = items; _onClick = onClick; _onMenu = onMenu; _onContext = onContext;
        int sz = GetSystemMetrics(49);
        if (sz <= 0) sz = 16;
        _icon = LoadImageW(IntPtr.Zero, icoPath, 1, sz, sz, 0x10);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _proc = WndProc;
        SetWindowSubclass(_hwnd, _proc, new UIntPtr(1), UIntPtr.Zero);
        Add();
    }

    NOTIFYICONDATA Data(uint flags)
    {
        var d = new NOTIFYICONDATA { hWnd = _hwnd, uID = 1, uFlags = flags | NIF_GUID, uCallbackMessage = WM_TRAY, hIcon = _icon, szTip = _tip, szInfo = "", szInfoTitle = "", guidItem = IconGuid };
        d.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
        return d;
    }

    void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
        if (!_added) LastAddError = Marshal.GetLastWin32Error();
        // With a GUID identity the shell can still hold the previous registration after an
        // unclean exit, which makes NIM_ADD fail; NIM_MODIFY re-attaches it to this window.
        if (!_added) { d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP); _added = Shell_NotifyIconW(NIM_MODIFY, ref d); if (!_added) LastModifyError = Marshal.GetLastWin32Error(); }
    }

    /// <summary>Retries registration when the shell was not ready at startup (e.g. explorer restarting).</summary>
    public void EnsureAdded()
    {
        if (_added) return;
        Add();
        // The shell can hold a stale or known-but-unregistered GUID entry that makes both ADD and
        // MODIFY fail; deleting it and adding fresh recovers that state.
        if (!_added) { var d = Data(0); Shell_NotifyIconW(NIM_DELETE, ref d); Add(); }
    }
    public bool IsAdded => _added;
    public bool IconLoaded => _icon != IntPtr.Zero;
    public int LastAddError;
    public int LastModifyError;

    /// <summary>Shows a Windows notification (balloon/toast) from the tray icon.</summary>
    public void Notify(string title, string text)
    {
        if (!_added) return;
        if (title.Length > 60) title = title[..60];
        if (text.Length > 250) text = text[..250];
        var d = Data(NIF_INFO);
        d.szInfoTitle = title; d.szInfo = text; d.dwInfoFlags = 0;
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    /// <summary>Swaps the tray icon image (e.g. dark glyph for a light taskbar) and refreshes the shell icon in place.</summary>
    public void SetIcon(string icoPath)
    {
        try
        {
            int sz = GetSystemMetrics(49);
            if (sz <= 0) sz = 16;
            var next = LoadImageW(IntPtr.Zero, icoPath, 1, sz, sz, 0x10);
            if (next == IntPtr.Zero) return;
            var old = _icon;
            _icon = next;
            if (old != IntPtr.Zero) DestroyIcon(old);
            if (_added) { var d = Data(NIF_ICON); Shell_NotifyIconW(NIM_MODIFY, ref d); }
        }
        catch { }
    }

    public void SetTip(string tip)
    {
        if (!NativeTip) return;
        if (tip.Length > 120) tip = tip[..120];
        if (tip == _tip) return;
        _tip = tip;
        if (!_added) return;
        var d = Data(NIF_TIP);
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_TRAY)
        {
            uint ev = (uint)((long)l & 0xFFFF);
            if (GetCursorPos(out var lp)) { _lastX = lp.x; _lastY = lp.y; }
            if (ev == WM_LBUTTONUP || ev == WM_RBUTTONUP) { try { OnTrayButton?.Invoke(); } catch { } }
            if (ev == WM_MOUSEMOVE) { try { OnHover?.Invoke(); } catch { } }
            else if (ev == WM_LBUTTONUP) _onClick();
            else if (ev == WM_RBUTTONUP)
            {
                bool ok = false;
                if (_onContext != null && GetCursorPos(out var cp)) { try { ok = _onContext(cp.x, cp.y); } catch { } }
                if (!ok) ShowMenu();
            }
            return IntPtr.Zero;
        }
        if (_taskbarCreated != 0 && msg == _taskbarCreated) { Add(); }
        if (msg == 0x001A) { try { OnSystemThemeChanged?.Invoke(); } catch { } } // WM_SETTINGCHANGE: Windows theme may have flipped
        return DefSubclassProc(h, msg, w, l);
    }

    void ShowMenu()
    {
        // Undocumented Windows menu-theme exports, guarded by version and fallback.
        try { if(Environment.OSVersion.Version.Build>=18362){SetPreferredAppMode(IsDark?.Invoke()==true?2:3);FlushMenuThemes();} }catch { }
        GetCursorPos(out var pt);
        var m = CreatePopupMenu();
        foreach (var (id, text) in _items) AppendMenuW(m, 0, new UIntPtr((uint)id), text);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(m, 0x100 | 0x20 | 0x2, pt.x, pt.y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(m);
        if (cmd > 0) _onMenu(cmd);
    }

    public void Dispose()
    {
        if (_added) { var d = Data(0); Shell_NotifyIconW(NIM_DELETE, ref d); _added = false; }
        RemoveWindowSubclass(_hwnd, _proc, new UIntPtr(1));
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
