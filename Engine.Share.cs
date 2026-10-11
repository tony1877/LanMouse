using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LanMouse;

/// <summary>Win32 calls used for window sharing.</summary>
static class Win
{
    public const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
    public const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_POPUP = 0x80000000L;
    public const uint LWA_ALPHA = 0x2;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    public static readonly nint HWND_TOP = 0, HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;
    public const uint WM_NCHITTEST = 0x84, WM_SYSCOMMAND = 0x112, WM_NCLBUTTONDOWN = 0xA1;
    public const int SC_DRAGMOVE = 0xF012;
    public const int HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTLEFT = 10, HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint h, out Native.RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attr, out Native.RECT r, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint h, int attr, out int v, int size);
    [DllImport("user32.dll")] public static extern bool PrintWindow(nint h, nint dc, uint flags);
    [DllImport("user32.dll")] public static extern nint GetDC(nint h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern long GetWindowLong(nint h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern long SetWindowLong(nint h, int idx, long v);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(nint h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(nint h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(nint h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint h, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern nint SendMessageTimeout(nint h, uint msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint h, char[] buf, int max);
    public delegate bool EnumProc(nint h, nint l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, nint l);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint h, uint flags);

    public static Native.RECT Frame(nint h)
    {
        // The visible frame, without the invisible resize borders Windows 10/11 add around windows.
        if (DwmGetWindowAttribute(h, 9, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) == 0 && r.Right > r.Left) return r;
        GetWindowRect(h, out r);
        return r;
    }

    public static string Title(nint h)
    {
        var buf = new char[256];
        int n = GetWindowText(h, buf, buf.Length);
        return new string(buf, 0, Math.Max(0, n));
    }

    public static bool Cloaked(nint h) => DwmGetWindowAttribute(h, 14, out int v, 4) == 0 && v != 0;

    /// <summary>Make a window of another app the foreground window (works around the foreground lock).</summary>
    public static void ForceForeground(nint h)
    {
        var fg = GetForegroundWindow();
        if (fg == h) return;
        uint me = Native.GetCurrentThreadId(), other = GetWindowThreadProcessId(fg, out _);
        bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, other, false);
    }
}

sealed partial class Engine
{
    public const int VideoPort = 47826;
    const int Tile = 128;
    UdpClient? video;
    readonly ConcurrentDictionary<int, OutStream> outStreams = new();
    readonly ConcurrentDictionary<int, StreamViewer> viewers = new();
    int nextStreamId = RandomNumberGenerator.GetInt32(1, int.MaxValue / 2);

    // Synthesized input must never come from the hook thread: Windows runs our own hook for every injected event
    // and would wait on that same thread, stalling the cursor. Everything injected for sharing goes through here.
    readonly BlockingCollection<Action> injectQueue = new();
    Thread? injectThread;

    void OnInjector(Action a)
    {
        if (injectThread == null)
        {
            lock (injectQueue)
            {
                if (injectThread == null)
                {
                    injectThread = new Thread(() => { foreach (var job in injectQueue.GetConsumingEnumerable()) { try { job(); } catch { } } })
                        { IsBackground = true, Name = "Injector", Priority = ThreadPriority.AboveNormal };
                    injectThread.Start();
                }
            }
        }
        injectQueue.Add(a);
    }

    // ======================== moving a window ourselves ========================
    // Windows' own move loop snaps windows that touch a screen edge (and offers Snap Layouts), which is exactly where
    // a window arrives from the other PC. So shared windows are dragged by us instead: they simply follow the cursor.

    volatile nint manualDrag;
    int manualGrabX, manualGrabY;

    internal bool IsManualDragging(nint hwnd) => manualDrag == hwnd;

    internal void BeginManualDrag(nint hwnd, int grabX, int grabY)
    {
        manualGrabX = grabX; manualGrabY = grabY;
        Win.GetWindowRect(hwnd, out lastDragRect);
        manualDrag = hwnd;
        movingHwnd = hwnd;
        dragIsMove = true;
    }

    /// <summary>Hook thread: drag the window with the cursor until the left button comes up.</summary>
    void ManualDragStep(int msg, int x, int y)
    {
        var h = manualDrag;
        if (h == 0) return;
        if (msg == Native.WM_MOUSEMOVE)
            Win.SetWindowPos(h, 0, x - manualGrabX, y - manualGrabY, 0, 0,
                Win.SWP_NOSIZE | Win.SWP_NOZORDER | Win.SWP_NOACTIVATE | 0x4000 /* ASYNCWINDOWPOS: never block the hook */);
        else if (msg == Native.WM_LBUTTONUP)
        {
            manualDrag = 0;
            if (movingHwnd == h) movingHwnd = 0;
        }
    }

    /// <summary>Windows we made invisible, with their original style, so they can be restored even after a crash.</summary>
    static readonly ConcurrentDictionary<nint, (long ex, bool layered, byte alpha, uint lwaFlags, uint key)> hiddenWindows = new();

    // ======================== lifecycle ========================

    void StartShare()
    {
        try
        {
            video = new UdpClient(new IPEndPoint(IPAddress.Any, VideoPort));
            video.Client.ReceiveBufferSize = 8 << 20;
            video.Client.SendBufferSize = 8 << 20;
            try { video.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[4], null); } catch { }
            var v = video;
            _ = Task.Run(() => VideoReceiveLoop(v));
        }
        catch { video = null; }
    }

    void StopShare()
    {
        EndAllStreams();
        try { video?.Dispose(); } catch { }
        video = null;
    }

    void EndAllStreams()
    {
        foreach (var s in outStreams.Values) s.Stop(restore: true);
        outStreams.Clear();
        foreach (var v in viewers.Values) v.Close();
        viewers.Clear();
    }

    /// <summary>Bring back every window we hid. Used on exit and from the crash handler.</summary>
    public static void EmergencyRestoreWindows()
    {
        foreach (var (h, st) in hiddenWindows) RestoreStyle(h, st);
        hiddenWindows.Clear();
    }

    static void RestoreStyle(nint h, (long ex, bool layered, byte alpha, uint lwaFlags, uint key) st)
    {
        if (!Win.IsWindow(h)) return;
        if (st.layered) Win.SetLayeredWindowAttributes(h, st.key, st.alpha, st.lwaFlags);
        Win.SetWindowLong(h, Win.GWL_EXSTYLE, st.ex);
        Win.SetWindowPos(h, (st.ex & Win.WS_EX_TOPMOST) != 0 ? Win.HWND_TOPMOST : Win.HWND_NOTOPMOST, 0, 0, 0, 0,
            Win.SWP_NOMOVE | Win.SWP_NOSIZE | Win.SWP_NOACTIVATE | Win.SWP_FRAMECHANGED);
    }

    // ======================== detecting a window being dragged ========================

    volatile nint movingHwnd;
    Native.RECT lastDragRect;
    int lastDragOffX, lastDragOffY;
    volatile bool dragIsMove;

    void OnMoveSize(nint hwnd, bool start)
    {
        if (start)
        {
            movingHwnd = hwnd;
            dragIsMove = false;
            Win.GetWindowRect(hwnd, out lastDragRect);
            Native.GetCursorPos(out var c);
            lastDragOffX = c.X - lastDragRect.Left; lastDragOffY = c.Y - lastDragRect.Top;
        }
        else if (hwnd == movingHwnd) movingHwnd = 0;
    }

    /// <summary>Called for every cursor move (physical or injected) while a window move/size loop runs.</summary>
    void SampleDrag(int x, int y)
    {
        var h = movingHwnd;
        if (h == 0) return;
        if (!Win.GetWindowRect(h, out var r)) { movingHwnd = 0; return; }
        bool sameSize = r.Right - r.Left == lastDragRect.Right - lastDragRect.Left && r.Bottom - r.Top == lastDragRect.Bottom - lastDragRect.Top;
        bool moved = r.Left != lastDragRect.Left || r.Top != lastDragRect.Top;
        if (!sameSize) dragIsMove = false;   // an edge resize (or a maximized window un-maximizing; the next moves decide)
        else if (moved) dragIsMove = true;   // same size, new place: the window is being moved
        lastDragRect = r;
    }

    /// <summary>The window currently being dragged by its title bar on this PC, if any.</summary>
    nint DraggedWindow() =>
        settings.ShareWindows && movingHwnd != 0 && dragIsMove && (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 ? movingHwnd : 0;

    static void InjectEscape()
    {
        Inject.Key(0x1B, 0, false, true);
        Inject.Key(0x1B, 0, false, false);
    }

    /// <summary>
    /// A dragged window just crossed to the peer at cursor (cx,cy). The cursor message (Enter or Return) has
    /// already been sent, so the peer's cursor is in place: send the window after it.
    /// </summary>
    /// <returns>true if it was a real window here (whose local drag should now be cancelled).</returns>
    bool HandDraggedToPeer(Link l, nint hwnd, int cx, int cy)
    {
        var viewer = viewers.Values.FirstOrDefault(v => v.Handle == hwnd);
        if (viewer != null)
        {
            // A streamed window goes home: the peer shows the real one again, under the cursor, still dragging.
            var (gx, gy) = viewer.ToStream(cx, cy);
            int id = viewer.Id;
            l.Send(Wire.Build(Msg.StreamBack, w => { w.Write(id); w.Write(gx); w.Write(gy); w.Write(true); }));
            viewers.TryRemove(id, out _);
            viewer.Close();
            return false;
        }
        var fr = Win.Frame(hwnd);
        StartStream(l, hwnd, cx - fr.Left, cy - fr.Top, drag: true);
        return true;
    }

    // ======================== streaming a window out ========================

    void StartStream(Link l, nint hwnd, int grabX, int grabY, bool drag)
    {
        var root = Win.GetAncestor(hwnd, 2); // GA_ROOT
        if (root != 0) hwnd = root;
        if (outStreams.Values.Any(s => s.Hwnd == hwnd)) return;
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, "lanmouse video"u8.ToArray());
        int id = Interlocked.Increment(ref nextStreamId);
        var fr = Win.Frame(hwnd);
        int w = Math.Max(1, fr.Right - fr.Left), h = Math.Max(1, fr.Bottom - fr.Top);
        float scale = (float)Screens.ScaleAt((fr.Left + fr.Right) / 2, (fr.Top + fr.Bottom) / 2);
        string title = Win.Title(hwnd);
        var s = new OutStream(this, id, hwnd, key);
        outStreams[id] = s;
        l.Send(Wire.Build(Msg.StreamStart, wr =>
        {
            wr.Write(id); wr.Write(w); wr.Write(h); wr.Write(scale);
            wr.Write(grabX); wr.Write(grabY); wr.Write(title); wr.Write(salt); wr.Write(drag);
        }));
        s.Start();
    }

    /// <summary>The viewer handed the window back: show it again, under the cursor, and keep dragging it if asked.</summary>
    void BringBack(OutStream s, int grabX, int grabY, bool drag)
    {
        outStreams.TryRemove(s.Id, out _);
        s.Stop(restore: true);
        Native.GetCursorPos(out var c);
        var fr = Win.Frame(s.Hwnd);
        Win.GetWindowRect(s.Hwnd, out var wr);
        // Window rect includes invisible borders; place so the visible frame sits at cursor - grab.
        int x = c.X - grabX - (fr.Left - wr.Left), y = c.Y - grabY - (fr.Top - wr.Top);
        Win.SetWindowPos(s.Hwnd, Win.HWND_TOP, x, y, 0, 0, Win.SWP_NOSIZE);
        if (!drag) { Win.ForceForeground(s.Hwnd); return; }
        if (controlled)
        {
            // The peer's mouse is driving this PC: hold the button down on its behalf until it lets go.
            lock (injectedKeys) injectedButtons.Add(0);
            Inject.Button(0, true);
        }
        Win.ForceForeground(s.Hwnd);
        BeginManualDrag(s.Hwnd, c.X - x, c.Y - y);
    }

    sealed class OutStream
    {
        readonly Engine e;
        public readonly int Id;
        public readonly nint Hwnd;
        readonly AesGcm aes;
        long counter;
        volatile bool running;
        Thread? thread;
        public long LastInput;
        public int ButtonsDown;
        bool interactive;
        public readonly bool[] SuppressUp = new bool[5];

        public OutStream(Engine e, int id, nint hwnd, byte[] key) { this.e = e; Id = id; Hwnd = hwnd; aes = new AesGcm(key, 16); }

        public void Start()
        {
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "StreamOut", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void Stop(bool restore)
        {
            running = false;
            if (restore && hiddenWindows.TryRemove(Hwnd, out var st)) RestoreStyle(Hwnd, st);
        }

        void Hide()
        {
            long ex = Win.GetWindowLong(Hwnd, Win.GWL_EXSTYLE);
            bool layered = (ex & Win.WS_EX_LAYERED) != 0;
            uint key = 0, flags = 0; byte alpha = 255;
            if (layered && !Win.GetLayeredWindowAttributes(Hwnd, out key, out alpha, out flags)) { key = 0; alpha = 255; flags = 0; }
            hiddenWindows[Hwnd] = (ex, layered, alpha, flags, key);
            // Nearly transparent and click-through: it keeps rendering (so capture works) but you can't see or hit it.
            Win.SetWindowLong(Hwnd, Win.GWL_EXSTYLE, ex | Win.WS_EX_LAYERED | Win.WS_EX_TRANSPARENT);
            Win.SetLayeredWindowAttributes(Hwnd, 0, 1, Win.LWA_ALPHA);
        }

        /// <summary>Let injected clicks reach the window: on top of everything and no longer click-through.</summary>
        public void MakeInteractive()
        {
            LastInput = Environment.TickCount64;
            if (interactive || !hiddenWindows.TryGetValue(Hwnd, out var st)) return;
            interactive = true;
            Win.SetWindowLong(Hwnd, Win.GWL_EXSTYLE, (st.ex | Win.WS_EX_LAYERED) & ~Win.WS_EX_TRANSPARENT);
            Win.SetWindowPos(Hwnd, Win.HWND_TOPMOST, 0, 0, 0, 0, Win.SWP_NOMOVE | Win.SWP_NOSIZE | Win.SWP_NOACTIVATE);
        }

        void MakePassive()
        {
            if (!interactive || !hiddenWindows.TryGetValue(Hwnd, out var st)) return;
            interactive = false;
            Win.SetWindowLong(Hwnd, Win.GWL_EXSTYLE, st.ex | Win.WS_EX_LAYERED | Win.WS_EX_TRANSPARENT);
            Win.SetWindowPos(Hwnd, Win.HWND_NOTOPMOST, 0, 0, 0, 0, Win.SWP_NOMOVE | Win.SWP_NOSIZE | Win.SWP_NOACTIVATE);
        }

        void Loop()
        {
            Thread.Sleep(80); // let the cancelled drag snap the window back first
            if (!running) return;
            if (Win.IsIconic(Hwnd)) Win.ShowWindow(Hwnd, 9); // SW_RESTORE
            Hide();
            var cap = new Capturer();
            byte[]? prev = null;
            int prevW = 0, prevH = 0;
            uint frame = 0;
            using var encoders = new ThreadLocal<TileEncoder>(() => new TileEncoder(), trackAllValues: true);
            try
            {
                while (running)
                {
                    long t0 = Environment.TickCount64;
                    if (!Win.IsWindow(Hwnd))
                    {
                        var l = e.link;
                        int id = Id;
                        l?.Send(Wire.Build(Msg.StreamEnd, w => w.Write(id)));
                        e.outStreams.TryRemove(Id, out _);
                        hiddenWindows.TryRemove(Hwnd, out _);
                        return;
                    }
                    if (interactive && ButtonsDown == 0 && Environment.TickCount64 - LastInput > 1500) MakePassive();

                    var (cur, w, h) = cap.Grab(Hwnd);
                    if (cur != null && e.video is UdpClient udp && e.link is Link link)
                    {
                        var to = new IPEndPoint(IPAddress.Parse(link.Address), VideoPort);
                        bool full = prev == null || w != prevW || h != prevH;
                        int cols = (w + Tile - 1) / Tile, rows = (h + Tile - 1) / Tile;
                        var todo = new List<(int x0, int y0, int tw, int th)>();
                        for (int ty = 0; ty < rows; ty++)
                            for (int tx = 0; tx < cols; tx++)
                            {
                                int x0 = tx * Tile, y0 = ty * Tile, tw = Math.Min(Tile, w - x0), th = Math.Min(Tile, h - y0);
                                // Changed tiles, plus a rotating slice of unchanged ones so a lost packet heals within a second.
                                bool refresh = (ty * cols + tx) % 30 == frame % 30;
                                if (full || refresh || !Same(prev!, cur, w, x0, y0, tw, th)) todo.Add((x0, y0, tw, th));
                            }
                        uint f = frame;
                        // Encode on all cores: a full-window update (scrolling, first frame) is many tiles at once.
                        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, t =>
                        {
                            var data = encoders.Value!.Encode(cur, w, t.x0, t.y0, t.tw, t.th);
                            SendTile(udp, to, f, w, h, t.x0, t.y0, t.tw, t.th, data);
                        });
                        prev = cur; prevW = w; prevH = h;
                        frame++;
                    }
                    int wait = 33 - (int)(Environment.TickCount64 - t0);
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            catch { }
            finally
            {
                cap.Dispose();
                foreach (var enc in encoders.Values) enc.Dispose();
            }
        }

        static bool Same(byte[] a, byte[] b, int w, int x0, int y0, int tw, int th)
        {
            for (int y = y0; y < y0 + th; y++)
                if (!a.AsSpan((y * w + x0) * 4, tw * 4).SequenceEqual(b.AsSpan((y * w + x0) * 4, tw * 4))) return false;
            return true;
        }

        void SendTile(UdpClient udp, IPEndPoint to, uint frame, int w, int h, int x, int y, int tw, int th, byte[] jpg)
        {
            var plain = new byte[16 + jpg.Length];
            BitConverter.TryWriteBytes(plain.AsSpan(0), frame);
            BitConverter.TryWriteBytes(plain.AsSpan(4), (ushort)w);
            BitConverter.TryWriteBytes(plain.AsSpan(6), (ushort)h);
            BitConverter.TryWriteBytes(plain.AsSpan(8), (ushort)x);
            BitConverter.TryWriteBytes(plain.AsSpan(10), (ushort)y);
            BitConverter.TryWriteBytes(plain.AsSpan(12), (ushort)tw);
            BitConverter.TryWriteBytes(plain.AsSpan(14), (ushort)th);
            jpg.CopyTo(plain, 16);
            var packet = new byte[16 + plain.Length + 16];
            "LMV1"u8.CopyTo(packet);
            BitConverter.TryWriteBytes(packet.AsSpan(4), Id);
            BitConverter.TryWriteBytes(packet.AsSpan(8), Interlocked.Increment(ref counter));
            aes.Encrypt(packet.AsSpan(4, 12), plain, packet.AsSpan(16, plain.Length), packet.AsSpan(16 + plain.Length, 16));
            try { udp.Send(packet, packet.Length, to); } catch { }
        }
    }

    /// <summary>Grabs a window (plus its menus/popups) as top-down BGRA pixels of its visible frame.</summary>
    sealed class Capturer : IDisposable
    {
        nint dc, bmp, bits;
        int bw, bh;

        bool Ensure(int w, int h)
        {
            if (w == bw && h == bh && bmp != 0) return true;
            Free();
            var screen = Win.GetDC(0);
            dc = Win.CreateCompatibleDC(screen);
            Win.ReleaseDC(0, screen);
            var bi = new Win.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            bmp = Win.CreateDIBSection(dc, ref bi, 0, out bits, 0, 0);
            if (bmp == 0) return false;
            Win.SelectObject(dc, bmp);
            bw = w; bh = h;
            return true;
        }

        /// <summary>Render one window into the DIB and return it with its window rect.</summary>
        bool Render(nint h, out Native.RECT wr)
        {
            Win.GetWindowRect(h, out wr);
            int w = wr.Right - wr.Left, hh = wr.Bottom - wr.Top;
            if (w <= 0 || hh <= 0 || w > 16384 || hh > 16384 || !Ensure(w, hh)) return false;
            return Win.PrintWindow(h, dc, 2); // PW_RENDERFULLCONTENT: works for GPU-drawn apps (browsers) too
        }

        public (byte[]? px, int w, int h) Grab(nint hwnd)
        {
            var fr = Win.Frame(hwnd);
            int w = fr.Right - fr.Left, h = fr.Bottom - fr.Top;
            if (w <= 0 || h <= 0 || !Render(hwnd, out var wr)) return (null, 0, 0);
            var px = new byte[w * h * 4];
            Blit(px, w, h, wr, fr);

            // Menus, dropdowns and tooltips are separate windows of the same app: paint them on top.
            Win.GetWindowThreadProcessId(hwnd, out uint pid);
            var popups = new List<nint>();
            Win.EnumWindows((p, _) =>
            {
                if (p != hwnd && Win.IsWindowVisible(p) && !Win.Cloaked(p)
                    && (Win.GetWindowLong(p, Win.GWL_STYLE) & Win.WS_POPUP) != 0
                    && Win.GetWindowThreadProcessId(p, out uint ppid) != 0 && ppid == pid
                    && Win.GetWindowRect(p, out var r) && r.Left < fr.Right && r.Right > fr.Left && r.Top < fr.Bottom && r.Bottom > fr.Top)
                    popups.Add(p);
                return popups.Count < 8;
            }, 0);
            popups.Reverse(); // EnumWindows is top-first; paint bottom-first
            foreach (var p in popups)
                if (Render(p, out var pr)) Blit(px, w, h, pr, fr, Win.Frame(p));
            return (px, w, h);
        }

        /// <summary>Copy the rendered DIB (window rect wr) into px (frame rect fr), clipped to src (defaults to the frame).</summary>
        void Blit(byte[] px, int w, int h, Native.RECT wr, Native.RECT fr, Native.RECT? src = null)
        {
            var s = src ?? fr;
            int x0 = Math.Max(s.Left, fr.Left), x1 = Math.Min(s.Right, fr.Right);
            int y0 = Math.Max(s.Top, fr.Top), y1 = Math.Min(s.Bottom, fr.Bottom);
            x0 = Math.Max(x0, wr.Left); x1 = Math.Min(x1, wr.Right);
            y0 = Math.Max(y0, wr.Top); y1 = Math.Min(y1, wr.Bottom);
            if (x1 <= x0 || y1 <= y0) return;
            int rowBytes = (x1 - x0) * 4;
            for (int y = y0; y < y1; y++)
                Marshal.Copy(bits + ((y - wr.Top) * bw + (x0 - wr.Left)) * 4, px, ((y - fr.Top) * w + (x0 - fr.Left)) * 4, rowBytes);
        }

        void Free()
        {
            if (bmp != 0) Win.DeleteObject(bmp);
            if (dc != 0) Win.DeleteDC(dc);
            bmp = dc = 0; bw = bh = 0;
        }

        public void Dispose() => Free();
    }

    sealed class TileEncoder : IDisposable
    {
        readonly ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        readonly EncoderParameters good = Params(75), rough = Params(40);
        readonly Dictionary<(int, int), Bitmap> bitmaps = new();
        readonly MemoryStream ms = new();

        static EncoderParameters Params(long q) { var p = new EncoderParameters(1); p.Param[0] = new EncoderParameter(Encoder.Quality, q); return p; }

        public byte[] Encode(byte[] px, int w, int x0, int y0, int tw, int th)
        {
            if (!bitmaps.TryGetValue((tw, th), out var bmp)) bitmaps[(tw, th)] = bmp = new Bitmap(tw, th, PixelFormat.Format32bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            for (int y = 0; y < th; y++) Marshal.Copy(px, ((y0 + y) * w + x0) * 4, bd.Scan0 + y * bd.Stride, tw * 4);
            bmp.UnlockBits(bd);
            ms.SetLength(0);
            bmp.Save(ms, codec, good);
            if (ms.Length > 60_000) { ms.SetLength(0); bmp.Save(ms, codec, rough); } // keep one tile per datagram
            return ms.ToArray();
        }

        public void Dispose() { foreach (var b in bitmaps.Values) b.Dispose(); }
    }

    // ======================== receiving video ========================

    async Task VideoReceiveLoop(UdpClient udp)
    {
        var plain = new byte[70_000];
        while (true)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(); }
            catch (ObjectDisposedException) { return; }
            catch { if (video != udp) return; continue; }
            var p = r.Buffer;
            if (p.Length < 16 + 16 + 16 || !p.AsSpan(0, 4).SequenceEqual("LMV1"u8)) continue;
            if (link is not Link l || r.RemoteEndPoint.Address.MapToIPv4().ToString() != l.Address) continue;
            int id = BitConverter.ToInt32(p, 4);
            if (!viewers.TryGetValue(id, out var v)) continue;
            int n = p.Length - 32;
            try { v.Aes.Decrypt(p.AsSpan(4, 12), p.AsSpan(16, n), p.AsSpan(16 + n, 16), plain.AsSpan(0, n)); }
            catch { continue; }
            int w = BitConverter.ToUInt16(plain, 4), h = BitConverter.ToUInt16(plain, 6);
            int x = BitConverter.ToUInt16(plain, 8), y = BitConverter.ToUInt16(plain, 10);
            int tw = BitConverter.ToUInt16(plain, 12), th = BitConverter.ToUInt16(plain, 14);
            try
            {
                using var bmp = (Bitmap)Image.FromStream(new MemoryStream(plain, 16, n - 16));
                v.PutTile(w, h, x, y, tw, th, bmp);
            }
            catch { }
        }
    }

    // ======================== control messages ========================

    /// <summary>Handles window-sharing messages from the peer. Returns false for anything else.</summary>
    bool OnShareMessage(Link l, Msg type, BinaryReader r)
    {
        switch (type)
        {
            case Msg.StreamStart:
            {
                int id = r.ReadInt32(), w = r.ReadInt32(), h = r.ReadInt32();
                float scale = r.ReadSingle();
                int gx = r.ReadInt32(), gy = r.ReadInt32();
                string title = r.ReadString();
                var salt = r.ReadBytes(16);
                bool drag = r.ReadBoolean();
                if (!settings.ShareWindows)
                {
                    l.Send(Wire.Build(Msg.StreamStop, wr => wr.Write(id)));
                    break;
                }
                var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, "lanmouse video"u8.ToArray());
                if (drag && controlled)
                {
                    // The peer's held mouse button now belongs to this PC: keep it down until it's released.
                    lock (injectedKeys) injectedButtons.Add(0);
                    Inject.Button(0, true);
                }
                Native.GetCursorPos(out var c);
                var v = new StreamViewer(this, id, key, w, h, scale, gx, gy, title, c.X, c.Y, drag);
                viewers[id] = v;
                break;
            }

            case Msg.StreamEnd:
            {
                int id = r.ReadInt32();
                if (viewers.TryRemove(id, out var v)) v.Close();
                break;
            }

            case Msg.StreamStop:
            {
                int id = r.ReadInt32();
                if (outStreams.TryRemove(id, out var s)) s.Stop(restore: true);
                break;
            }

            case Msg.StreamBack:
            {
                int id = r.ReadInt32(), gx = r.ReadInt32(), gy = r.ReadInt32();
                bool drag = r.ReadBoolean();
                if (outStreams.TryGetValue(id, out var s)) OnInjector(() => BringBack(s, gx, gy, drag));
                break;
            }

            case Msg.StreamMouse:
            {
                int id = r.ReadInt32();
                byte kind = r.ReadByte(), button = r.ReadByte();
                int x = r.ReadInt32(), y = r.ReadInt32();
                short delta = r.ReadInt16();
                if (outStreams.TryGetValue(id, out var s)) OnInjector(() => InjectStreamMouse(l, s, kind, button, x, y, delta));
                break;
            }

            case Msg.StreamKey:
            {
                int id = r.ReadInt32();
                ushort vk = r.ReadUInt16(), scan = r.ReadUInt16();
                bool ext = r.ReadBoolean(), down = r.ReadBoolean();
                if (outStreams.TryGetValue(id, out var s))
                    OnInjector(() =>
                    {
                        s.MakeInteractive();
                        Win.ForceForeground(s.Hwnd);
                        Inject.Key(vk, scan, ext, down);
                    });
                break;
            }

            case Msg.StreamFocus:
            {
                int id = r.ReadInt32();
                if (outStreams.TryGetValue(id, out var s)) OnInjector(() => Win.ForceForeground(s.Hwnd));
                break;
            }

            case Msg.StreamResize:
            {
                int id = r.ReadInt32(), w = r.ReadInt32(), h = r.ReadInt32();
                if (outStreams.TryGetValue(id, out var s) && w > 50 && h > 50 && w < 16384 && h < 16384)
                {
                    var fr = Win.Frame(s.Hwnd);
                    Win.GetWindowRect(s.Hwnd, out var wr);
                    // Requested size is the visible frame; add back the invisible borders.
                    int bw = (wr.Right - wr.Left) - (fr.Right - fr.Left), bh = (wr.Bottom - wr.Top) - (fr.Bottom - fr.Top);
                    Win.ShowWindow(s.Hwnd, 9);
                    Win.SetWindowPos(s.Hwnd, 0, 0, 0, w + bw, h + bh, Win.SWP_NOMOVE | Win.SWP_NOZORDER | Win.SWP_NOACTIVATE);
                }
                break;
            }

            case Msg.StreamHit:
            {
                int id = r.ReadInt32(), code = r.ReadInt32();
                if (viewers.TryGetValue(id, out var v)) v.OnHit(code);
                break;
            }

            default:
                return false;
        }
        return true;
    }

    /// <summary>Mouse input from the viewer, delivered to the real (hidden) window. Runs on the injector thread.</summary>
    void InjectStreamMouse(Link l, OutStream s, byte kind, byte button, int x, int y, short delta)
    {
        if (!Win.IsWindow(s.Hwnd)) return;
        var fr = Win.Frame(s.Hwnd);
        int sx = fr.Left + x, sy = fr.Top + y;

        if (kind == 1 && button < 5)
        {
            // Title bar, borders and min/max buttons are handled by the viewer window itself.
            Win.SendMessageTimeout(s.Hwnd, Win.WM_NCHITTEST, 0, (nint)((sy << 16) | (sx & 0xFFFF)), 0x2 /* ABORTIFHUNG */, 100, out var hit);
            int code = (int)hit;
            if (button == 0 && (code == Win.HTCAPTION || code == Win.HTMINBUTTON || code == Win.HTMAXBUTTON || (code >= Win.HTLEFT && code <= Win.HTBOTTOMRIGHT)))
            {
                s.SuppressUp[button] = true;
                int id = s.Id;
                l.Send(Wire.Build(Msg.StreamHit, w => { w.Write(id); w.Write(code); }));
                return;
            }
        }
        if (kind == 2 && button < 5 && s.SuppressUp[button]) { s.SuppressUp[button] = false; return; }

        s.MakeInteractive();
        Inject.MoveTo(sx, sy);
        // This moved our (hidden) cursor off centre. Tell the capture handler where it now is, so the next real
        // mouse move is measured as a small delta from here instead of a huge jump from centre.
        if (controlling) pin = new Native.POINT { X = sx, Y = sy };
        switch (kind)
        {
            case 1: s.ButtonsDown++; Inject.Button(button, true); break;
            case 2: s.ButtonsDown = Math.Max(0, s.ButtonsDown - 1); Inject.Button(button, false); break;
            case 3: Inject.Wheel(delta, false); break;
            case 4: Inject.Wheel(delta, true); break;
        }
    }

    // ======================== viewer -> streamer (called from viewer threads) ========================

    internal void ViewerMouse(int id, byte kind, byte button, int x, int y, short delta) =>
        link?.Send(Wire.Build(Msg.StreamMouse, w => { w.Write(id); w.Write(kind); w.Write(button); w.Write(x); w.Write(y); w.Write(delta); }));

    internal void ViewerKey(int id, ushort vk, ushort scan, bool ext, bool down) =>
        link?.Send(Wire.Build(Msg.StreamKey, w => { w.Write(id); w.Write(vk); w.Write(scan); w.Write(ext); w.Write(down); }));

    internal void ViewerFocus(int id) => link?.Send(Wire.Build(Msg.StreamFocus, w => w.Write(id)));

    internal void ViewerResize(int id, int w, int h) => link?.Send(Wire.Build(Msg.StreamResize, wr => { wr.Write(id); wr.Write(w); wr.Write(h); }));

    internal void ViewerClosed(int id)
    {
        if (viewers.TryRemove(id, out _)) link?.Send(Wire.Build(Msg.StreamStop, w => w.Write(id)));
    }
}
