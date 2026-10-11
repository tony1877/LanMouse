using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace LanMouse;

/// <summary>
/// Shows a window streamed from the other PC and sends everything you do in it back.
/// Each viewer runs on its own UI thread so a busy stream never slows LAN Mouse's own window.
/// </summary>
sealed class StreamViewer
{
    public readonly int Id;
    public readonly AesGcm Aes;
    readonly Engine engine;
    readonly float sourceScale;
    readonly object canvasLock = new();
    Bitmap? canvas;
    int streamW, streamH;
    volatile bool dirty;
    Rectangle dirtyRect;
    ViewerForm? form;
    public nint Handle { get; private set; }
    readonly ManualResetEventSlim ready = new();

    public StreamViewer(Engine engine, int id, byte[] key, int w, int h, float sourceScale, int grabX, int grabY, string title, int cursorX, int cursorY, bool drag)
    {
        this.engine = engine;
        Id = id;
        Aes = new AesGcm(key, 16);
        this.sourceScale = sourceScale;
        streamW = w; streamH = h;
        canvas = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(canvas)) g.Clear(Color.FromArgb(24, 27, 34));

        var t = new Thread(() =>
        {
            double f = Factor(cursorX, cursorY);
            form = new ViewerForm(this)
            {
                Text = string.IsNullOrEmpty(title) ? "LAN Mouse window" : title,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(cursorX - (int)Math.Round(grabX * f), cursorY - (int)Math.Round(grabY * f)),
                ClientSize = new Size((int)Math.Round(w * f), (int)Math.Round(h * f)),
            };
            Handle = form.Handle;
            ready.Set();
            if (drag) form.Shown += (_, _) => form.FollowCursor();
            Application.Run(form);
        }) { IsBackground = true, Name = "StreamViewer" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(3000);
    }

    /// <summary>Viewer pixels per streamed pixel, so the window keeps its visual size across display scaling.</summary>
    double Factor(int x, int y) => Screens.ScaleAt(x, y) / Math.Max(0.5, sourceScale);

    /// <summary>A point on this PC's screen, as a position inside the streamed window.</summary>
    public (int X, int Y) ToStream(int screenX, int screenY)
    {
        var f = form;
        if (f == null || f.IsDisposed) return (0, 0);
        var b = f.Bounds;
        return ((int)((screenX - b.Left) * (double)streamW / Math.Max(1, b.Width)),
                (int)((screenY - b.Top) * (double)streamH / Math.Max(1, b.Height)));
    }

    public void PutTile(int w, int h, int x, int y, int tw, int th, Bitmap tile)
    {
        lock (canvasLock)
        {
            if (w != streamW || h != streamH || canvas == null)
            {
                // The real window changed size: start a new canvas, keeping what we can.
                var next = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(next))
                {
                    g.Clear(Color.FromArgb(24, 27, 34));
                    if (canvas != null) g.DrawImageUnscaled(canvas, 0, 0);
                }
                canvas?.Dispose();
                canvas = next;
                streamW = w; streamH = h;
                form?.BeginInvoke(() => form.FitToStream());
            }
            using (var g = Graphics.FromImage(canvas))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(tile, new Rectangle(x, y, tw, th), new Rectangle(0, 0, tw, th), GraphicsUnit.Pixel);
            }
            var r = new Rectangle(x, y, tw, th);
            dirtyRect = dirtyRect.IsEmpty ? r : Rectangle.Union(dirtyRect, r);
        }
        dirty = true;
    }

    /// <summary>The streamer says the mouse went down on its title bar, a border or a caption button.</summary>
    public void OnHit(int code) => form?.BeginInvoke(() => form.OnHit(code));

    public void Close()
    {
        var f = form;
        if (f == null || f.IsDisposed) return;
        try { f.BeginInvoke(() => { f.ClosingFromEngine = true; f.Close(); }); } catch { }
    }

    sealed class ViewerForm : Form
    {
        readonly StreamViewer v;
        readonly System.Windows.Forms.Timer repaint = new() { Interval = 8 };
        public bool ClosingFromEngine;
        bool userSized;
        long lastMove;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern nint SendMessage(nint h, int msg, nint w, nint l);

        public ViewerForm(StreamViewer v)
        {
            this.v = v;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(24, 27, 34);
            DoubleBuffered = true;
            ShowInTaskbar = true;
            KeyPreview = false;
            repaint.Tick += (_, _) =>
            {
                if (!v.dirty) return;
                v.dirty = false;
                Rectangle d;
                lock (v.canvasLock) { d = v.dirtyRect; v.dirtyRect = Rectangle.Empty; }
                if (d.IsEmpty) return;
                // Only repaint what changed (in viewer pixels, padded for the scaling filter).
                double sx = ClientSize.Width / (double)Math.Max(1, v.streamW), sy = ClientSize.Height / (double)Math.Max(1, v.streamH);
                Invalidate(Rectangle.FromLTRB((int)(d.Left * sx) - 2, (int)(d.Top * sy) - 2, (int)Math.Ceiling(d.Right * sx) + 2, (int)Math.Ceiling(d.Bottom * sy) + 2));
            };
            repaint.Start();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00040000 | 0x00020000 | 0x00010000; // WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX: native resize, snap, taskbar min
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            lock (v.canvasLock)
            {
                if (v.canvas == null) return;
                bool exact = ClientSize.Width == v.streamW && ClientSize.Height == v.streamH;
                e.Graphics.InterpolationMode = exact ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.CompositingMode = CompositingMode.SourceCopy;
                // Draw just the invalidated part: scaling the whole window every frame is what made it sluggish.
                var c = e.ClipRectangle;
                double sx = v.streamW / (double)Math.Max(1, ClientSize.Width), sy = v.streamH / (double)Math.Max(1, ClientSize.Height);
                var src = new RectangleF((float)(c.Left * sx), (float)(c.Top * sy), (float)(c.Width * sx), (float)(c.Height * sy));
                e.Graphics.DrawImage(v.canvas, c, src, GraphicsUnit.Pixel);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        /// <summary>Real window resized on the other PC: match it unless the user is sizing this one.</summary>
        public void FitToStream()
        {
            if (WindowState != FormWindowState.Normal || userSized) { userSized = false; return; }
            double f = v.Factor(Left + Width / 2, Top + Height / 2);
            ClientSize = new Size((int)Math.Round(v.streamW * f), (int)Math.Round(v.streamH * f));
        }

        /// <summary>Move with the cursor until the button is released (no Windows snapping).</summary>
        public void FollowCursor()
        {
            Activate();
            var c = Cursor.Position;
            v.engine.BeginManualDrag(Handle, c.X - Left, c.Y - Top);
        }

        public void BeginDrag(int ht)
        {
            Activate();
            ReleaseCapture();
            SendMessage(Handle, (int)Win.WM_NCLBUTTONDOWN, ht, 0);
        }

        public void OnHit(int code)
        {
            if (code == Win.HTMINBUTTON) { WindowState = FormWindowState.Minimized; return; }
            if (code == Win.HTMAXBUTTON)
            {
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                SendSize();
                return;
            }
            if ((MouseButtons & MouseButtons.Left) == 0) return; // already let go
            if (code == Win.HTCAPTION) FollowCursor();
            else BeginDrag(code); // resize edges: Windows' native sizing is fine
        }

        void SendSize()
        {
            double f = v.Factor(Left + Width / 2, Top + Height / 2);
            v.engine.ViewerResize(v.Id, (int)Math.Round(ClientSize.Width / f), (int)Math.Round(ClientSize.Height / f));
        }

        (int, int) Map(nint lParam)
        {
            int x = unchecked((short)(long)lParam), y = unchecked((short)((long)lParam >> 16));
            return ((int)(x * (double)v.streamW / Math.Max(1, ClientSize.Width)), (int)(y * (double)v.streamH / Math.Max(1, ClientSize.Height)));
        }

        const int WM_NCCALCSIZE = 0x83, WM_ACTIVATE = 0x6, WM_MOUSEMOVE = 0x200, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E,
            WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_CHAR = 0x102, WM_SYSCHAR = 0x106,
            WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232, WM_SIZING = 0x214, WM_NCHITTEST = 0x84;

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCCALCSIZE when m.WParam != 0:
                    m.Result = 0; // no visible frame: the streamed window draws its own
                    return;
                case WM_NCHITTEST:
                {
                    // A thin resize border of our own (the real window's resize zone sits outside what's streamed);
                    // everything else is content, and the real window decides what its title bar is.
                    if (WindowState != FormWindowState.Normal) { m.Result = 1; return; }
                    var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                    int b = Math.Max(4, (int)Math.Round(6 * DeviceDpi / 96.0));
                    bool l = p.X < b, r = p.X >= ClientSize.Width - b, t = p.Y < b, btm = p.Y >= ClientSize.Height - b;
                    m.Result = (t, btm, l, r) switch
                    {
                        (true, _, true, _) => 13, (true, _, _, true) => 14, (_, true, true, _) => 16, (_, true, _, true) => 17,
                        (true, _, _, _) => 12, (_, true, _, _) => 15, (_, _, true, _) => 10, (_, _, _, true) => 11,
                        _ => 1,
                    };
                    return;
                }
                case WM_ACTIVATE when ((long)m.WParam & 0xFFFF) != 0:
                    v.engine.ViewerFocus(v.Id);
                    break;
                case WM_MOUSEMOVE:
                    if (v.engine.IsManualDragging(Handle)) return; // being dragged: nothing to tell the real window
                    long now = Environment.TickCount64;
                    if (now - lastMove >= 8)
                    {
                        lastMove = now;
                        var (x, y) = Map(m.LParam);
                        v.engine.ViewerMouse(v.Id, 0, 0, x, y, 0);
                    }
                    return;
                case 0x201: case 0x203: Button(ref m, 0, true); return;   // L down / dblclk
                case 0x202: Button(ref m, 0, false); return;
                case 0x204: case 0x206: Button(ref m, 1, true); return;   // R
                case 0x205: Button(ref m, 1, false); return;
                case 0x207: case 0x209: Button(ref m, 2, true); return;   // M
                case 0x208: Button(ref m, 2, false); return;
                case 0x20B: case 0x20D: Button(ref m, ((long)m.WParam >> 16) == 1 ? (byte)3 : (byte)4, true); return;
                case 0x20C: Button(ref m, ((long)m.WParam >> 16) == 1 ? (byte)3 : (byte)4, false); return;
                case WM_MOUSEWHEEL:
                case WM_MOUSEHWHEEL:
                {
                    var p = PointToClient(Cursor.Position);
                    var (x, y) = ((int)(p.X * (double)v.streamW / Math.Max(1, ClientSize.Width)), (int)(p.Y * (double)v.streamH / Math.Max(1, ClientSize.Height)));
                    short delta = unchecked((short)((long)m.WParam >> 16));
                    v.engine.ViewerMouse(v.Id, m.Msg == WM_MOUSEWHEEL ? (byte)3 : (byte)4, 0, x, y, delta);
                    return;
                }
                case WM_KEYDOWN: case WM_SYSKEYDOWN: Key(ref m, true); return;
                case WM_KEYUP: case WM_SYSKEYUP: Key(ref m, false); return;
                case WM_CHAR: case WM_SYSCHAR: return; // the real window makes its own characters from the keys
                case WM_SIZING:
                    userSized = true;
                    break;
                case WM_EXITSIZEMOVE:
                    if (userSized) SendSize();
                    break;
            }
            base.WndProc(ref m);
        }

        readonly bool[] downSent = new bool[5];

        void Button(ref Message m, byte button, bool down)
        {
            if (down) Capture = true; else Capture = false;
            // Only pass on a release if its press went over too (e.g. not the end of dragging this viewer).
            if (button < 5) { if (!down && !downSent[button]) return; downSent[button] = down; }
            var (x, y) = Map(m.LParam);
            v.engine.ViewerMouse(v.Id, down ? (byte)1 : (byte)2, button, x, y, 0);
        }

        void Key(ref Message m, bool down)
        {
            long lp = (long)m.LParam;
            ushort vk = (ushort)(long)m.WParam, scan = (ushort)((lp >> 16) & 0xFF);
            bool ext = (lp & (1 << 24)) != 0;
            v.engine.ViewerKey(v.Id, vk, scan, ext, down);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            repaint.Stop();
            if (!ClosingFromEngine) v.engine.ViewerClosed(v.Id);
            base.OnFormClosed(e);
            Application.ExitThread();
        }
    }
}
