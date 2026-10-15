using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfClipboard = System.Windows.Clipboard;

namespace LanMouse;

/// <summary>
/// Clipboard sharing that only moves data when you paste.
///
/// Copy on this PC -> the peer gets a tiny "offer" describing it. The peer puts placeholders on its clipboard
/// (Windows "delayed rendering"); the first time an app there actually pastes, Windows asks us for the data and we
/// download it right then over a dedicated connection.
///
/// Runs on its own thread: while a paste is downloading, the app that is pasting waits (that is how Windows
/// clipboard works), but LAN Mouse's window and the cursor stay responsive.
/// </summary>
sealed class ClipboardSync : IClipboardSink, IDisposable
{
    readonly Engine engine;
    readonly Settings settings;
    readonly Thread thread;
    Dispatcher ui = null!;
    HwndSource window = null!;
    DispatcherTimer debounce = null!;
    static readonly uint CF_PNG = Native.RegisterClipboardFormat("PNG");
    static readonly uint CF_DROPEFFECT = Native.RegisterClipboardFormat("Preferred DropEffect");

    // -- what we offered the peer --
    int myOfferId;
    ClipKinds myKinds;
    string[]? myFiles;

    // -- what the peer offered us (and what we already fetched of it) --
    ClipOffer? theirs;
    string? gotText;
    byte[]? gotPng;
    string[]? gotFiles;
    CancellationTokenSource? pasteCts;

    public ClipboardSync(Engine engine, Settings settings)
    {
        this.engine = engine;
        this.settings = settings;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            ui = Dispatcher.CurrentDispatcher;
            window = new HwndSource(new HwndSourceParameters("LanMouseClipboard") { ParentWindow = new IntPtr(-3) }); // message-only
            window.AddHook(WndProc);
            Native.AddClipboardFormatListener(window.Handle);
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            debounce.Tick += (_, _) => { debounce.Stop(); Capture(); };
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }

    nint WndProc(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_CLIPBOARDUPDATE:
                // Ignore our own placeholders; anything else is a fresh copy on this PC.
                if (Native.GetClipboardOwner() != window.Handle) { debounce.Stop(); debounce.Start(); }
                break;
            case Native.WM_RENDERFORMAT:
                Render((uint)w);
                handled = true;
                break;
            case Native.WM_RENDERALLFORMATS:
                // We're exiting while still holding the peer's offer: it can't be pasted later, so just let it go.
                handled = true;
                break;
            case Native.WM_DESTROYCLIPBOARD:
                // Something else took over the clipboard: the peer's offer is no longer on it.
                theirs = null;
                gotText = null; gotPng = null; gotFiles = null;
                break;
        }
        return 0;
    }

    static T? Retry<T>(Func<T> f)
    {
        for (int i = 0; i < 6; i++)
        {
            try { return f(); }
            catch (COMException) { Thread.Sleep(40); } // clipboard busy
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return default;
    }

    // ==================== this PC copied something -> offer it ====================

    void Capture()
    {
        if (engine.Phase != Phase.Connected) return;
        try
        {
            ClipKinds kinds = ClipKinds.None;
            string summary = "";
            string[]? files = null;
            if (Retry(() => WpfClipboard.ContainsFileDropList()))
            {
                files = Retry(() => WpfClipboard.GetFileDropList())?.Cast<string>().ToArray();
                if (files is { Length: > 0 })
                {
                    kinds = ClipKinds.Files;
                    summary = files.Length == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(files[0])) : $"{files.Length} items";
                }
            }
            else
            {
                if (Retry(() => WpfClipboard.ContainsImage() || WpfClipboard.ContainsData("PNG"))) kinds |= ClipKinds.Image;
                if (Retry(() => WpfClipboard.ContainsText())) kinds |= ClipKinds.Text;
                summary = kinds.HasFlag(ClipKinds.Image) ? "picture" : "text";
            }
            if (kinds == ClipKinds.None) return;
            myOfferId++;
            myKinds = kinds;
            myFiles = files;
            engine.SendOffer(myOfferId, kinds, summary);
        }
        catch { }
    }

    /// <summary>Peer is pasting our offer: read it now (on this STA thread, which owns clipboard access).</summary>
    public object? ReadForPeer(int id, ClipKinds kind)
    {
        try
        {
            return ui.Invoke(() =>
            {
                if (id != myOfferId || !myKinds.HasFlag(kind)) return (object?)null;
                return kind switch
                {
                    ClipKinds.Files => myFiles,
                    ClipKinds.Image => Retry(ReadPng),
                    ClipKinds.Text => Retry(() => WpfClipboard.ContainsText() ? WpfClipboard.GetText() : null),
                    _ => null,
                };
            }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(8));
        }
        catch { return null; }
    }

    static byte[]? ReadPng()
    {
        // Browsers and modern apps put a "PNG" stream on the clipboard with proper transparency, so prefer it.
        if (WpfClipboard.GetData("PNG") is MemoryStream ms) return ms.ToArray();
        var img = WpfClipboard.GetImage();
        if (img == null) return null;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var outStream = new MemoryStream();
        enc.Save(outStream);
        return outStream.ToArray();
    }

    // ==================== the peer copied something -> placeholders here ====================

    public void ApplyOffer(ClipOffer offer) => ui.BeginInvoke(() =>
    {
        pasteCts?.Cancel();
        theirs = null;
        gotText = null; gotPng = null; gotFiles = null;
        for (int i = 0; i < 10 && !Native.OpenClipboard(window.Handle); i++) Thread.Sleep(30);
        try
        {
            Native.EmptyClipboard(); // sends WM_DESTROYCLIPBOARD to the old owner (possibly us), so set theirs afterwards
            theirs = offer;
            if (offer.Kinds.HasFlag(ClipKinds.Files))
            {
                Native.SetClipboardData(Native.CF_HDROP, 0);
                Native.SetClipboardData(CF_DROPEFFECT, Global(BitConverter.GetBytes(1))); // DROPEFFECT_COPY
            }
            if (offer.Kinds.HasFlag(ClipKinds.Image))
            {
                Native.SetClipboardData(CF_PNG, 0);
                Native.SetClipboardData(Native.CF_DIB, 0);
            }
            if (offer.Kinds.HasFlag(ClipKinds.Text)) Native.SetClipboardData(Native.CF_UNICODETEXT, 0);
        }
        finally { Native.CloseClipboard(); }
    });

    public void ClearOffer(string peerId) => ui.BeginInvoke(() =>
    {
        if (theirs != null && theirs.PeerId != peerId) return; // the clipboard holds another PC's copy: keep it
        pasteCts?.Cancel();
        if (theirs == null || Native.GetClipboardOwner() != window.Handle) { theirs = null; return; }
        theirs = null;
        if (Native.OpenClipboard(window.Handle))
        {
            Native.EmptyClipboard();
            Native.CloseClipboard();
        }
    });

    /// <summary>An app is pasting one of the peer's formats: download it now and hand it to Windows.</summary>
    void Render(uint format)
    {
        var offer = theirs;
        if (offer == null) return;
        ClipKinds kind = format == Native.CF_HDROP ? ClipKinds.Files
                       : format == Native.CF_UNICODETEXT ? ClipKinds.Text
                       : format == Native.CF_DIB || format == CF_PNG ? ClipKinds.Image
                       : ClipKinds.None;
        if (kind == ClipKinds.None) return;

        object? data = kind switch { ClipKinds.Text => gotText, ClipKinds.Image => gotPng, _ => gotFiles };
        if (data == null)
        {
            pasteCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try { data = engine.PullAsync(offer, kind, pasteCts.Token).GetAwaiter().GetResult(); }
            catch { data = null; }
            if (theirs != offer) return; // replaced while we waited
            switch (data)
            {
                case string t: gotText = t; break;
                case byte[] p: gotPng = p; break;
                case string[] f: gotFiles = f; break;
            }
        }
        nint h = data switch
        {
            string text when format == Native.CF_UNICODETEXT => Global(Encoding.Unicode.GetBytes(text + "\0")),
            byte[] png when format == CF_PNG => Global(png),
            byte[] png when format == Native.CF_DIB => Global(PngToDib(png)),
            string[] paths when format == Native.CF_HDROP => Global(DropFiles(paths)),
            _ => 0,
        };
        if (h != 0 && Native.SetClipboardData(format, h) == 0) Native.GlobalFree(h);
    }

    static nint Global(byte[] bytes)
    {
        var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)Math.Max(1, bytes.Length));
        if (h == 0) return 0;
        var p = Native.GlobalLock(h);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Native.GlobalUnlock(h);
        return h;
    }

    /// <summary>CF_HDROP: DROPFILES header + double-null-terminated UTF-16 path list.</summary>
    static byte[] DropFiles(string[] paths)
    {
        var names = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var buf = new byte[20 + names.Length];
        BitConverter.GetBytes(20).CopyTo(buf, 0);  // pFiles
        BitConverter.GetBytes(1).CopyTo(buf, 16);  // fWide
        names.CopyTo(buf, 20);
        return buf;
    }

    /// <summary>CF_DIB: BITMAPINFOHEADER + bottom-up 32-bit pixels (Windows synthesizes CF_BITMAP from this).</summary>
    static byte[] PngToDib(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var src = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        src.CopyPixels(pixels, stride, 0);
        var dib = new byte[40 + pixels.Length];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);   // positive = bottom-up
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(pixels.Length).CopyTo(dib, 20);
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(pixels, y * stride, dib, 40 + (h - 1 - y) * stride, stride);
        return dib;
    }

    public void Dispose()
    {
        try
        {
            ui.Invoke(() =>
            {
                Native.RemoveClipboardFormatListener(window.Handle);
                window.Dispose();
            });
            ui.InvokeShutdown();
        }
        catch { }
    }
}
