using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace LanMouse;

static class Native
{
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204,
        WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A,
        WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_MOUSEHWHEEL = 0x20E;
    public const int WM_QUIT = 0x12, WM_CLIPBOARDUPDATE = 0x31D;
    public const uint LLMHF_INJECTED = 0x1, LLKHF_EXTENDED = 0x1, LLKHF_INJECTED = 0x10;
    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nint extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    public delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int id, HookProc proc, nint hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desk);

    /// <summary>
    /// False while Windows shows a secure desktop (sign-in/PIN prompt, UAC, Ctrl+Alt+Del). Normal programs can't
    /// open it or send input to it, by design.
    /// </summary>
    public static bool InputDesktopReachable()
    {
        var d = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
        if (d == 0) return false;
        CloseDesktop(d);
        return true;
    }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint proc, uint access, out nint tok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(nint tok, int cls, nint info, int len, out int ret);

    /// <summary>
    /// True when the foreground window belongs to a higher-integrity process than ours (e.g. Task Manager or any
    /// app run as administrator). Windows (UIPI) then silently drops our simulated input, so it must be routed
    /// through the SYSTEM agent instead.
    /// </summary>
    public static bool ForegroundInputBlocked()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == 0) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0) return false;
            int mine = ProcessIntegrity(GetCurrentProcessId(), 0x2000 /* assume medium for us */);
            int theirs = ProcessIntegrity(pid, 0x3000 /* if we can't open it, it's above us */);
            return theirs > mine;
        }
        catch { return false; }
    }

    static int ProcessIntegrity(uint pid, int fallback)
    {
        nint h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return fallback;
        nint tok = 0;
        try
        {
            if (!OpenProcessToken(h, 0x0008 /* TOKEN_QUERY */, out tok)) return fallback;
            GetTokenInformation(tok, 25 /* TokenIntegrityLevel */, 0, 0, out int len);
            if (len <= 0) return fallback;
            nint buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(tok, 25, buf, len, out _)) return fallback;
                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label { PSID Sid; ... } }: Sid is first pointer.
                nint sid = System.Runtime.InteropServices.Marshal.ReadIntPtr(buf);
                byte count = System.Runtime.InteropServices.Marshal.ReadByte(sid, 1);     // SubAuthorityCount
                int rid = System.Runtime.InteropServices.Marshal.ReadInt32(sid, 8 + (count - 1) * 4); // last sub-authority
                return rid;
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
        }
        finally
        {
            if (tok != 0) CloseHandle(tok);
            CloseHandle(h);
        }
    }
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] public static extern nint GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] public static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] public static extern nint GlobalLock(nint mem);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(nint mem);
    [DllImport("kernel32.dll")] public static extern nint GlobalFree(nint mem);
    public const int WM_RENDERFORMAT = 0x305, WM_RENDERALLFORMATS = 0x306, WM_DESTROYCLIPBOARD = 0x307;
    public const uint CF_UNICODETEXT = 13, CF_DIB = 8, CF_HDROP = 15, GMEM_MOVEABLE = 0x2;
}

/// <summary>Geometry of this PC's monitors and of the combined two-PC arrangement, in physical pixels.</summary>
static class Screens
{
    public readonly record struct Bounds(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width;
        public int Bottom => Top + Height;
        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        public Bounds Shift(int dx, int dy) => new(Left + dx, Top + dy, Width, Height);
    }

    public static Bounds Virtual => new(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77),
                                        Math.Max(1, Native.GetSystemMetrics(78)), Math.Max(1, Native.GetSystemMetrics(79)));

    static Bounds[]? monitors;

    /// <summary>Every monitor's rectangle (cached; call <see cref="Refresh"/> when the display setup changes).</summary>
    public static Bounds[] Monitors => monitors ??= Load();

    public static void Refresh() { monitors = null; lock (scales) scales.Clear(); }

    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    static readonly System.Collections.Generic.Dictionary<Bounds, double> scales = new();

    /// <summary>Windows display scaling of the monitor under (x,y): 1.0 = 100%, 1.5 = 150%.</summary>
    public static double ScaleAt(int x, int y)
    {
        var m = At(Monitors, x, y) ?? Nearest(Monitors, x, y);
        lock (scales)
        {
            if (scales.TryGetValue(m, out var s)) return s;
            s = 1.0;
            try
            {
                var h = Native.MonitorFromPoint(new Native.POINT { X = m.Left + m.Width / 2, Y = m.Top + m.Height / 2 }, Native.MONITOR_DEFAULTTONEAREST);
                if (h != 0 && GetDpiForMonitor(h, 0, out var dx, out _) == 0 && dx > 0) s = dx / 96.0;
            }
            catch { }
            scales[m] = s;
            return s;
        }
    }

    static Bounds[] Load()
    {
        try
        {
            var all = System.Windows.Forms.Screen.AllScreens
                .OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top)
                .Select(s => new Bounds(s.Bounds.Left, s.Bounds.Top, Math.Max(1, s.Bounds.Width), Math.Max(1, s.Bounds.Height)))
                .ToArray();
            if (all.Length > 0) return all;
        }
        catch { }
        return new[] { Virtual };
    }

    public static Bounds? At(Bounds[] ms, int x, int y)
    {
        foreach (var m in ms) if (m.Contains(x, y)) return m;
        return null;
    }

    public static Bounds Nearest(Bounds[] ms, int x, int y) => ms.MinBy(m =>
        (long)Math.Max(0, Math.Max(m.Left - x, x - m.Right + 1)) + Math.Max(0, Math.Max(m.Top - y, y - m.Bottom + 1)));

    public static Bounds Box(Bounds[] ms)
    {
        int l = ms.Min(m => m.Left), t = ms.Min(m => m.Top);
        return new(l, t, ms.Max(m => m.Right) - l, ms.Max(m => m.Bottom) - t);
    }

    public static Bounds[] Shift(Bounds[] ms, int dx, int dy) => ms.Select(m => m.Shift(dx, dy)).ToArray();

    /// <summary>Pull a point onto the nearest real monitor (handles gaps and mismatched monitor sizes).</summary>
    public static (int X, int Y) Clamp(int x, int y) => ClampOnto(Monitors, x, y, 0);

    /// <summary>Pull a point onto the nearest of the given monitors, at least <paramref name="inset"/> px from its border.</summary>
    public static (int X, int Y) ClampOnto(Bounds[] ms, int x, int y, int inset)
    {
        var m = At(ms, x, y) ?? Nearest(ms, x, y);
        int ix = Math.Min(inset, (m.Width - 1) / 2), iy = Math.Min(inset, (m.Height - 1) / 2);
        return (Math.Clamp(x, m.Left + ix, m.Right - 1 - ix), Math.Clamp(y, m.Top + iy, m.Bottom - 1 - iy));
    }

    static readonly (int dx, int dy)[] Directions = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>
    /// The cursor is at (x,y) on one of <paramref name="mine"/>. If it is pressed against a border that touches one of
    /// <paramref name="other"/> (already shifted into the same coordinates), returns the point just across that border.
    /// </summary>
    public static (int X, int Y)? Crossing(Bounds[] mine, Bounds[] other, int x, int y)
    {
        if (other.Length == 0) return null;
        var m = At(mine, x, y) ?? Nearest(mine, x, y);
        foreach (var (dx, dy) in Directions)
        {
            bool atBorder = dx > 0 ? x >= m.Right - 1 : dx < 0 ? x <= m.Left : dy > 0 ? y >= m.Bottom - 1 : y <= m.Top;
            if (!atBorder) continue;
            int px = Math.Clamp(x, m.Left, m.Right - 1) + dx, py = Math.Clamp(y, m.Top, m.Bottom - 1) + dy;
            if (At(mine, px, py) != null) continue;          // just a seam between two of this PC's own screens
            if (At(other, px, py) is Bounds o) return ClampOnto(new[] { o }, px, py, 2);
        }
        return null;
    }

    // -- arrangement rules --

    static bool Overlap(Bounds a, Bounds b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    public static bool Overlaps(Bounds[] a, Bounds[] b) => a.Any(m => b.Any(o => Overlap(m, o)));

    /// <summary>Length of the border two monitors share (0 if they don't sit edge to edge).</summary>
    public static int Shared(Bounds a, Bounds b)
    {
        if (a.Right == b.Left || b.Right == a.Left) return Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        if (a.Bottom == b.Top || b.Bottom == a.Top) return Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        return 0;
    }

    static int MinShared(Bounds a, Bounds b) => Math.Max(1, Math.Min(200, Math.Min(Math.Min(a.Width, a.Height), Math.Min(b.Width, b.Height)) / 3));

    /// <summary>The other PC's screens don't overlap ours and at least one pair shares a usable stretch of border.</summary>
    public static bool Valid(Bounds[] mine, Bounds[] otherShifted) =>
        !mine.Any(m => otherShifted.Any(o => Overlap(m, o))) &&
        mine.Any(m => otherShifted.Any(o => Shared(m, o) >= MinShared(m, o)));

    /// <summary>
    /// Closest valid placement of the other PC (its monitors in its own coordinates) to the requested offset:
    /// it snaps flush against one of our screens, like Windows display settings, and lines up edges when close.
    /// </summary>
    public static (int X, int Y) Snap(Bounds[] mine, Bounds[] other, int wantX, int wantY)
    {
        (int, int)? best = null;
        long bestDist = long.MaxValue;
        void Consider(int ox, int oy)
        {
            if (!Valid(mine, Shift(other, ox, oy))) return;
            long d = (long)(ox - wantX) * (ox - wantX) + (long)(oy - wantY) * (oy - wantY);
            if (d < bestDist) { bestDist = d; best = (ox, oy); }
        }
        foreach (var m in mine)
            foreach (var p in other)
            {
                int ms = MinShared(m, p);
                // side by side: x is fixed, y slides along the shared border
                foreach (int ox in new[] { m.Right - p.Left, m.Left - p.Right })
                {
                    int lo = m.Top - p.Bottom + ms, hi = m.Bottom - p.Top - ms;
                    if (lo > hi) continue;
                    int oy = Math.Clamp(wantY, lo, hi);
                    foreach (int a in new[] { m.Top - p.Top, m.Bottom - p.Bottom, m.Top + m.Height / 2 - (p.Top + p.Height / 2) })
                        if (a >= lo && a <= hi && Math.Abs(a - oy) <= Math.Max(40, m.Height / 12)) { oy = a; break; }
                    Consider(ox, oy);
                    Consider(ox, lo);
                    Consider(ox, hi);
                }
                // stacked: y is fixed, x slides along the shared border
                foreach (int oy in new[] { m.Bottom - p.Top, m.Top - p.Bottom })
                {
                    int lo = m.Left - p.Right + ms, hi = m.Right - p.Left - ms;
                    if (lo > hi) continue;
                    int ox = Math.Clamp(wantX, lo, hi);
                    foreach (int a in new[] { m.Left - p.Left, m.Right - p.Right, m.Left + m.Width / 2 - (p.Left + p.Width / 2) })
                        if (a >= lo && a <= hi && Math.Abs(a - ox) <= Math.Max(40, m.Width / 12)) { ox = a; break; }
                    Consider(ox, oy);
                    Consider(lo, oy);
                    Consider(hi, oy);
                }
            }
        if (best is (int, int) b) return b;
        var mb = Box(mine); var ob = Box(other);
        return (mb.Right - ob.Left, mb.Top - ob.Top);
    }

    /// <summary>A starting arrangement: the other PC's desktop beside ours on the given side, tops/lefts aligned.</summary>
    public static (int X, int Y) Beside(Bounds[] mine, Bounds[] other, Side side)
    {
        var mb = Box(mine); var ob = Box(other);
        var (x, y) = side switch
        {
            Side.Left => (mb.Left - ob.Right, mb.Top - ob.Top),
            Side.Top => (mb.Left - ob.Left, mb.Top - ob.Bottom),
            Side.Bottom => (mb.Left - ob.Left, mb.Bottom - ob.Top),
            _ => (mb.Right - ob.Left, mb.Top - ob.Top),
        };
        return Snap(mine, other, x, y);
    }
}

/// <summary>Synthesizes input locally. Tagged with Magic so our own hooks can ignore it.</summary>
static class Inject
{
    public const nint Magic = 0x4C4D4F55;
    const uint MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, RDOWN = 0x8, RUP = 0x10, MDOWN = 0x20, MUP = 0x40,
        XDOWN = 0x80, XUP = 0x100, WHEEL = 0x800, HWHEEL = 0x1000, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
    const uint KEYUP = 0x2, EXTENDED = 0x1, SCANCODE = 0x8;
    static readonly int Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>();

    static void Mouse(int dx, int dy, uint data, uint flags)
    {
        var i = new Native.INPUT { type = 0 };
        i.u.mi = new Native.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Magic };
        Native.SendInput(1, new[] { i }, Size);
    }

    static (int, int) Abs(int x, int y)
    {
        var v = Screens.Virtual;
        return ((int)(((long)(x - v.Left) * 65535 + (v.Width - 1) / 2) / Math.Max(1, v.Width - 1)),
                (int)(((long)(y - v.Top) * 65535 + (v.Height - 1) / 2) / Math.Max(1, v.Height - 1)));
    }

    public static void MoveTo(int x, int y)
    {
        var (ax, ay) = Abs(x, y);
        Mouse(ax, ay, 0, MOVE | ABSOLUTE | VIRTUALDESK);
    }

    /// <param name="button">0 left, 1 right, 2 middle, 3 X1, 4 X2</param>
    /// <param name="at">If set, move there in the same input event, so nothing can move the cursor in between.</param>
    public static void Button(int button, bool down, (int X, int Y)? at = null)
    {
        uint flags, data = 0;
        switch (button)
        {
            case 0: flags = down ? LDOWN : LUP; break;
            case 1: flags = down ? RDOWN : RUP; break;
            case 2: flags = down ? MDOWN : MUP; break;
            case 3: case 4: flags = down ? XDOWN : XUP; data = (uint)(button - 2); break;
            default: return;
        }
        Send(data, flags, at);
    }

    public static void Wheel(int delta, bool horizontal, (int X, int Y)? at = null) =>
        Send(unchecked((uint)delta), horizontal ? HWHEEL : WHEEL, at);

    static void Send(uint data, uint flags, (int X, int Y)? at)
    {
        if (at is not { } p) { Mouse(0, 0, data, flags); return; }
        var (ax, ay) = Abs(p.X, p.Y);
        Mouse(ax, ay, data, flags | MOVE | ABSOLUTE | VIRTUALDESK);
    }

    public static void Key(ushort vk, ushort scan, bool extended, bool down)
    {
        // Right Shift arrives from the hook flagged as extended (it isn't), and NumLock/Pause/PrintScreen have odd
        // scan codes: sent by scan code they come out as the wrong key or nothing. Send those by virtual key.
        if (vk is 0xA1 or 0x90 or 0x13 or 0x2C) { scan = 0; extended = vk is 0x90 or 0x2C; }
        uint flags = (down ? 0 : KEYUP) | (extended ? EXTENDED : 0);
        if (scan != 0) flags |= SCANCODE;
        var i = new Native.INPUT { type = 1 };
        i.u.ki = new Native.KEYBDINPUT { wVk = scan != 0 ? (ushort)0 : vk, wScan = scan, dwFlags = flags, dwExtraInfo = Magic };
        Native.SendInput(1, new[] { i }, Size);
    }
}
