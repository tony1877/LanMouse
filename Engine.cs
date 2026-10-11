using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanMouse;

public enum Phase { Stopped, Starting, Searching, Connected }

[Flags]
enum ClipKinds : byte { None = 0, Text = 1, Image = 2, Files = 4 }

/// <summary>What the other PC has on its clipboard. Nothing is transferred until it is actually pasted.</summary>
sealed record ClipOffer(int Id, ClipKinds Kinds, string Summary);

interface IClipboardSink
{
    /// <summary>The peer copied something: advertise it on our clipboard without fetching it.</summary>
    void ApplyOffer(ClipOffer offer);
    /// <summary>The peer is gone: drop anything we were advertising on its behalf.</summary>
    void ClearOffer();
    /// <summary>The peer is pasting our offer #id: return string (text), byte[] (PNG) or string[] (file paths), or null if it changed.</summary>
    object? ReadForPeer(int id, ClipKinds kind);
}

sealed partial class Engine : IDisposable
{
    public const int TcpPort = 47823, UdpPort = 47824, DataPort = 47825;
    const byte Protocol = 4;
    public static readonly string ReceiveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LAN Mouse");

    readonly Settings settings;
    readonly InputHook hook;
    public IClipboardSink? Clipboard { get; set; }

    readonly object gate = new();
    CancellationTokenSource? cts;
    Socket? listener, dataListener;
    UdpClient? udp;
    byte[] master = Array.Empty<byte>(), discoveryKey = Array.Empty<byte>();
    volatile Link? link;
    int connecting;
    readonly HashSet<Link> transfers = new();

    // -- observable state (read by UI) --
    public Phase Phase { get; private set; }
    public string PeerName { get; private set; } = "";
    /// <summary>The other PC's monitor rectangles in its own coordinates (empty until it tells us).</summary>
    public Screens.Bounds[] PeerMonitors { get; private set; } = Array.Empty<Screens.Bounds>();
    public string PeerAddress => link?.Address ?? "";
    public string? Warning { get; private set; }
    public bool Controlling => controlling;
    public bool Controlled => controlled;
    public event Action? Changed;
    public event Action<string>? Notify;
    public event Action<string?, double>? Progress;

    // -- input state --
    volatile bool controlling, controlled;
    Native.POINT pin, pinHome;
    readonly HashSet<uint> forwardedKeys = new();
    readonly HashSet<int> forwardedButtons = new();
    readonly HashSet<(ushort vk, ushort scan, bool ext)> injectedKeys = new();
    readonly HashSet<int> injectedButtons = new();
    int rx, ry;
    bool peerAllowsEdgeReturn = true;
    // Moves sent while controlling, so the ones still in flight when the peer hands the cursor back aren't lost.
    readonly (short dx, short dy)[] sentMoves = new (short, short)[1024];
    int movesSent, movesApplied;
    // Display scaling: movement is converted so the pointer covers the same visual distance on both PCs.
    double senderScale = 1, carryX, carryY;
    long nextDesktopCheck;
    readonly SecureInput secure = new();
    volatile bool secureActive;   // injecting via the SYSTEM agent (sign-in screen, or an elevated app blocks us)
    volatile bool inputBlocked;   // input can't reach the foreground (secure desktop or elevated app) without the agent

    /// <summary>
    /// Controlled side: decide how to deliver input. Normal apps take direct SendInput. The secure sign-in screen,
    /// and elevated foreground apps like Task Manager (which Windows stops ordinary input from reaching), are routed
    /// through the SYSTEM agent when the user installed sign-in support. Only the unreachable sign-in screen with no
    /// agent forces control back to the other PC; a merely-blocked elevated app does not (that would feel like a
    /// random bounce). Returns true only when control was handed back.
    /// </summary>
    bool SecureDesktopBlocks(Link l)
    {
        long now = Environment.TickCount64;
        if (now < nextDesktopCheck) return false;
        nextDesktopCheck = now + 200;
        bool deskUnreachable = !Native.InputDesktopReachable();
        bool elevated = !deskUnreachable && Native.ForegroundInputBlocked();
        if (!deskUnreachable && !elevated) { secureActive = false; inputBlocked = false; return false; }
        // Need the agent: route through it if available (it runs as SYSTEM and can reach both).
        if (settings.SignInSupport && secure.TryConnect()) { secureActive = true; inputBlocked = false; return false; }
        secureActive = false;
        inputBlocked = true;
        if (deskUnreachable)
        {
            // Sign-in/secure desktop and no agent: hand control back so the cursor isn't stuck here.
            controlled = false;
            ReleaseInjected();
            l.Send(Wire.Build(Msg.Locked));
            ThreadPool.QueueUserWorkItem(_ => Fire());
            return true;
        }
        // An elevated app is swallowing our input and there's no agent: we can't control it, but stay put
        // rather than bouncing back. Ctrl+Alt+<this PC> or Scroll Lock still brings the cursor home.
        return false;
    }
    long cooldownUntil;

    public Engine(Settings settings)
    {
        this.settings = settings;
        EnsureLayout(announce: false);
        hook = new InputHook(OnMouse, OnKey, OnMoveSize);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
    }

    void Fire() => Changed?.Invoke();

    // ======================== arrangement ========================

    static readonly Screens.Bounds[] Placeholder = { new(0, 0, 1920, 1080) };

    /// <summary>The other PC's monitors (its own coordinates): live if connected, else as last seen, else a stand-in.</summary>
    public Screens.Bounds[] PeerLayoutMonitors
    {
        get
        {
            if (PeerMonitors.Length > 0) return PeerMonitors;
            var cache = settings.PeerMonitorCache
                .Where(a => a is { Length: 4 } && a[2] > 0 && a[3] > 0)
                .Select(a => new Screens.Bounds(a[0], a[1], a[2], a[3])).ToArray();
            return cache.Length > 0 ? cache : Placeholder;
        }
    }

    public bool PeerMonitorsKnown => PeerMonitors.Length > 0 || settings.PeerMonitorCache.Length > 0;

    /// <summary>Where the other PC's desktop starts, in this PC's screen coordinates.</summary>
    public (int X, int Y) PeerOffset => (settings.PeerOffsetX, settings.PeerOffsetY);

    /// <summary>The other PC's monitors placed into this PC's coordinates. Read by the hook thread.</summary>
    volatile Screens.Bounds[] peerPlaced = Array.Empty<Screens.Bounds>();

    void UpdatePlaced() => peerPlaced = Screens.Shift(PeerLayoutMonitors, settings.PeerOffsetX, settings.PeerOffsetY);

    void SetOffset(int x, int y, long stamp)
    {
        settings.PeerOffsetX = x;
        settings.PeerOffsetY = y;
        settings.LayoutStamp = stamp;
        settings.HasLayout = true;
        settings.Save();
        UpdatePlaced();
    }

    /// <summary>Make sure there is an arrangement and that it still fits both PCs' current monitors.</summary>
    void EnsureLayout(bool announce)
    {
        var mine = Screens.Monitors;
        var other = PeerLayoutMonitors;
        if (!settings.HasLayout)
        {
            var (x, y) = Screens.Beside(mine, other, settings.Side);
            SetOffset(x, y, 0);
        }
        else if (!Screens.Valid(mine, Screens.Shift(other, settings.PeerOffsetX, settings.PeerOffsetY)))
        {
            var (x, y) = Screens.Snap(mine, other, settings.PeerOffsetX, settings.PeerOffsetY);
            SetOffset(x, y, DateTime.UtcNow.Ticks);
            if (announce) SendLayout();
        }
        else UpdatePlaced();
    }

    void SendLayout()
    {
        long stamp = settings.LayoutStamp; int x = settings.PeerOffsetX, y = settings.PeerOffsetY;
        link?.Send(Wire.Build(Msg.Layout, w => { w.Write(stamp); w.Write(x); w.Write(y); }));
    }

    /// <summary>User dragged the other PC to a new spot (this PC's coordinates). Snaps, saves and tells the peer.</summary>
    public (int X, int Y) MovePeer(int wantX, int wantY)
    {
        var (x, y) = Screens.Snap(Screens.Monitors, PeerLayoutMonitors, wantX, wantY);
        SetOffset(x, y, DateTime.UtcNow.Ticks);
        SendLayout();
        Fire();
        return (x, y);
    }

    /// <summary>The peer's view of the arrangement, if it is newer than ours (ties go to the lower id).</summary>
    void AdoptLayout(long stamp, int peerX, int peerY, string peerId)
    {
        bool theirs = !settings.HasLayout || stamp > settings.LayoutStamp ||
                      (stamp == settings.LayoutStamp && string.CompareOrdinal(peerId, settings.Id) < 0);
        if (theirs) SetOffset(-peerX, -peerY, stamp);
        EnsureLayout(announce: true);
    }

    void OnDisplaysChanged(object? sender, EventArgs e)
    {
        Screens.Refresh();
        link?.Send(Wire.Build(Msg.Screens, WriteMonitors));
        EnsureLayout(announce: true);
        Fire();
    }

    void SetPeerMonitors(Screens.Bounds[] mons)
    {
        PeerMonitors = mons;
        if (mons.Length > 0)
        {
            settings.PeerMonitorCache = mons.Select(m => new[] { m.Left, m.Top, m.Width, m.Height }).ToArray();
            settings.Save();
        }
    }

    static void WriteMonitors(BinaryWriter w)
    {
        var mons = Screens.Monitors;
        w.Write((byte)Math.Min(mons.Length, 16));
        foreach (var m in mons.Take(16)) { w.Write(m.Left); w.Write(m.Top); w.Write(m.Width); w.Write(m.Height); }
    }

    static Screens.Bounds[] ReadMonitors(BinaryReader r)
    {
        if (r.BaseStream.Position >= r.BaseStream.Length) return Array.Empty<Screens.Bounds>();
        int n = r.ReadByte();
        var list = new List<Screens.Bounds>();
        for (int i = 0; i < n && i < 16; i++)
        {
            int x = r.ReadInt32(), y = r.ReadInt32(), w = r.ReadInt32(), h = r.ReadInt32();
            if (w > 0 && h > 0 && w < 100_000 && h < 100_000) list.Add(new Screens.Bounds(x, y, w, h));
        }
        return list.ToArray();
    }

    /// <summary>PC numbers follow the arrangement: 1 = the left (or upper) PC, 2 = the other.</summary>
    public int MyNumber
    {
        get
        {
            var mb = Screens.Box(Screens.Monitors);
            var placed = peerPlaced;
            if (placed.Length == 0) return 1;
            var pb = Screens.Box(placed);
            double dx = pb.Left + pb.Width / 2.0 - (mb.Left + mb.Width / 2.0);
            double dy = pb.Top + pb.Height / 2.0 - (mb.Top + mb.Height / 2.0);
            bool peerFirst = Math.Abs(dx) >= Math.Abs(dy) ? dx < 0 : dy < 0;
            return peerFirst ? 2 : 1;
        }
    }
    public int PeerNumber => 3 - MyNumber;

    // ======================== lifecycle ========================

    public async Task StartAsync()
    {
        if (Phase != Phase.Stopped) return;
        var key = settings.Key;
        if (key.Length < 8) throw new InvalidOperationException("Use a pairing key of at least 8 characters.");
        Phase = Phase.Starting; Warning = null; Fire();

        // Slow on purpose: makes guessing the key from sniffed traffic expensive.
        master = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(key),
            "LanMouse.v3.pairing"u8.ToArray(), 200_000, HashAlgorithmName.SHA256, 32));
        discoveryKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, null, "lanmouse discovery"u8.ToArray());

        cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Any, TcpPort));
            listener.Listen(4);
            dataListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            dataListener.Bind(new IPEndPoint(IPAddress.Any, DataPort));
            dataListener.Listen(8);
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, UdpPort));
        }
        catch (SocketException e)
        {
            Stop();
            Warning = $"Network port busy ({e.SocketErrorCode}). Is another copy of LAN Mouse running?";
            Fire();
            return;
        }

        StartShare();
        Phase = Phase.Searching; Fire();
        _ = Task.Run(() => AcceptLoop(ct));
        _ = Task.Run(() => DataAcceptLoop(ct));
        _ = Task.Run(() => DiscoveryReceiveLoop(ct));
        _ = Task.Run(() => AnnounceLoop(ct));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
        try { listener?.Dispose(); } catch { }
        try { dataListener?.Dispose(); } catch { }
        try { udp?.Dispose(); } catch { }
        listener = null; dataListener = null; udp = null;
        StopShare();
        Phase = Phase.Stopped;
        link?.Dispose();
        link = null;
        PeerMonitors = Array.Empty<Screens.Bounds>();
        AbortTransfers();
        Clipboard?.ClearOffer();
        ResetInputState();
        Fire();
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        Stop();
        hook.Dispose();
        secure.Dispose();
    }

    // ======================== networking ========================

    async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket s;
            try { s = await listener!.AcceptAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Establish(s, initiator: false));
        }
    }

    long lastHeard, searchStarted;

    void Hint(string text)
    {
        if (link != null || Warning == text) return;
        Warning = text;
        Fire();
    }

    static bool IsLocalAddress(IPAddress a)
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(u => u.Address.Equals(a)); }
        catch { return false; }
    }

    async Task AnnounceLoop(CancellationToken ct)
    {
        searchStarted = Environment.TickCount64;
        lastHeard = 0;
        var id = Convert.FromHexString(settings.Id);
        while (!ct.IsCancellationRequested)
        {
            if (link == null)
            {
                var packet = DiscoveryPacket(id, Bucket());
                foreach (var target in BroadcastTargets())
                {
                    try { await udp!.SendAsync(packet, new IPEndPoint(target, UdpPort), ct); } catch { }
                }
                if (lastHeard == 0 && Environment.TickCount64 - searchStarted > 8000 && Warning == null)
                    Hint("Nothing heard from another LAN Mouse yet. Make sure LanMouse.exe is running there with Connect pressed, then click \"Allow through firewall\" on BOTH PCs. If it still fails, type the other PC's IP below.");
                if (!string.IsNullOrWhiteSpace(settings.PeerAddress))
                    _ = Task.Run(() => Connect(settings.PeerAddress.Trim()));
            }
            else
            {
                link.Send(Wire.Build(Msg.Ping));
            }
            try { await Task.Delay(2000, ct); } catch { return; }
        }
    }

    /// <summary>
    /// 255.255.255.255 only leaves through ONE adapter on Windows (often a Hyper-V/VPN one),
    /// so we also send a directed broadcast on every real IPv4 network.
    /// </summary>
    static IEnumerable<IPAddress> BroadcastTargets()
    {
        yield return IPAddress.Broadcast;
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }

    static long Bucket() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

    byte[] DiscoveryPacket(byte[] id, long bucket)
    {
        var p = new byte[4 + 16 + 16];
        "LMD3"u8.CopyTo(p);
        id.CopyTo(p, 4);
        var tagInput = new byte[24];
        id.CopyTo(tagInput, 0);
        BitConverter.GetBytes(bucket).CopyTo(tagInput, 16);
        HMACSHA256.HashData(discoveryKey, tagInput).AsSpan(0, 16).CopyTo(p.AsSpan(20));
        return p;
    }

    async Task DiscoveryReceiveLoop(CancellationToken ct)
    {
        var myId = Convert.FromHexString(settings.Id);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp!.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch { continue; } // ICMP resets etc.

            var p = r.Buffer;
            var from = r.RemoteEndPoint.Address.ToString();
            if (IsLocalAddress(r.RemoteEndPoint.Address)) continue;
            lastHeard = Environment.TickCount64;
            if (p.Length > 0 && p[0] == (byte)'{')
            {
                Hint($"{from} is running the OLD Python LAN Mouse. Close it there and run LanMouse.exe instead.");
                continue;
            }
            if (p.Length != 36 || !p.AsSpan(0, 4).SequenceEqual("LMD3"u8)) continue;
            var peerId = p.AsSpan(4, 16).ToArray();
            int cmp = myId.AsSpan().SequenceCompareTo(peerId);
            if (cmp == 0 || link != null) continue;
            long now = Bucket();
            bool valid = false;
            for (long b = now - 1; b <= now + 1 && !valid; b++)
                valid = CryptographicOperations.FixedTimeEquals(DiscoveryPacket(peerId, b).AsSpan(20), p.AsSpan(20));
            if (!valid)
            {
                Hint($"Found LAN Mouse at {from}, but its pairing key is different (or its clock is off by over a minute).");
                continue;
            }
            // Both sides dial: if one side's firewall blocks incoming connections, the other direction still works.
            _ = Task.Run(() => Connect(from));
        }
    }

    async Task Connect(string host)
    {
        if (link != null || Interlocked.Exchange(ref connecting, 1) == 1) return;
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = new CancellationTokenSource(3000);
            try { await s.ConnectAsync(host, TcpPort, timeout.Token); }
            catch
            {
                s.Dispose();
                Hint($"Found {host} but couldn't connect to it. On that PC, click \"Allow through firewall\".");
                return;
            }
            Establish(s, initiator: true);
        }
        finally { Volatile.Write(ref connecting, 0); }
    }

    void Establish(Socket s, bool initiator)
    {
        if (cts == null) { s.Dispose(); return; }
        var l = Link.Handshake(s, settings.Id, master, initiator);
        if (l == null)
        {
            Hint("Reached the other PC, but the pairing keys don't match.");
            return;
        }

        Link? old;
        lock (gate)
        {
            old = link;
            if (cts == null || (old != null && !(l.InitiatedByLowerId && !old.InitiatedByLowerId)))
            {
                l.Dispose();
                return;
            }
            link = l;
        }
        if (old != null) { old.Closed -= OnClosed; old.Dispose(); }
        ResetInputState();
        l.Message += OnMessage;
        l.Closed += OnClosed;
        l.Start();
        bool hasLayout = settings.HasLayout;
        long stamp = settings.LayoutStamp; int ox = settings.PeerOffsetX, oy = settings.PeerOffsetY;
        l.Send(Wire.Build(Msg.Hello, w =>
        {
            w.Write((byte)settings.Side); // v3 peers read this first
            w.Write(Environment.MachineName);
            WriteMonitors(w);
            w.Write(Protocol);
            w.Write(hasLayout); w.Write(stamp); w.Write(ox); w.Write(oy);
        }));
        Phase = Phase.Connected; Warning = null; PeerName = l.Address;
        Fire();
    }

    void OnClosed(Link l)
    {
        lock (gate)
        {
            if (link != l) return;
            link = null;
        }
        PeerMonitors = Array.Empty<Screens.Bounds>();
        UpdatePlaced();
        ResetInputState();
        AbortTransfers();
        EndAllStreams();
        Clipboard?.ClearOffer();
        if (Phase == Phase.Connected) Phase = Phase.Searching;
        Fire();
    }

    void InjMove(int x, int y) { if (secureActive) secure.Move(x, y); else Inject.MoveTo(x, y); }
    void InjButton(int b, bool down) { if (secureActive) secure.Button(b, down); else Inject.Button(b, down); }
    void InjWheel(short delta, bool horizontal) { if (secureActive) secure.Wheel(delta, horizontal); else Inject.Wheel(delta, horizontal); }
    void InjKey(ushort vk, ushort scan, bool ext, bool down) { if (secureActive) secure.Key(vk, scan, ext, down); else Inject.Key(vk, scan, ext, down); }

    void ResetInputState()
    {
        controlling = false;
        controlled = false;
        secureActive = false;
        inputBlocked = false;
        CursorHider.Set(false);
        lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
        ReleaseInjected();
    }

    void ReleaseInjected()
    {
        lock (injectedKeys)
        {
            foreach (var k in injectedKeys) InjKey(k.vk, k.scan, k.ext, false);
            foreach (var b in injectedButtons) InjButton(b, false);
            injectedKeys.Clear();
            injectedButtons.Clear();
        }
    }

    // ======================== local input (hook thread, keep fast) ========================

    bool OnMouse(int msg, in Native.MSLLHOOKSTRUCT m)
    {
        if (msg == Native.WM_MOUSEMOVE) SampleDrag(m.pt.X, m.pt.Y);
        if (manualDrag != 0) ManualDragStep(msg, m.pt.X, m.pt.Y);
        if (m.extra == Inject.Magic) return false;
        var l = link;

        if (controlling)
        {
            if (l == null) { controlling = false; return false; }
            switch (msg)
            {
                case Native.WM_MOUSEMOVE:
                    // Movement is the offset from where the hidden cursor was last left (centre, or wherever window-
                    // sharing injection moved it). Then snap it back to the centre so it never drifts into a screen
                    // edge (where Windows would clamp it and swallow further movement) and the next delta stays clean.
                    short dx = (short)Math.Clamp(m.pt.X - pin.X, -32000, 32000), dy = (short)Math.Clamp(m.pt.Y - pin.Y, -32000, 32000);
                    if (dx != 0 || dy != 0)
                    {
                        sentMoves[movesSent & 1023] = (dx, dy);
                        movesSent++;
                        l.Send(Wire.Build(Msg.Move, w => { w.Write(dx); w.Write(dy); }));
                    }
                    Native.SetCursorPos(pinHome.X, pinHome.Y);
                    pin = pinHome;
                    return true;
                case Native.WM_MOUSEWHEEL:
                case Native.WM_MOUSEHWHEEL:
                    short delta = unchecked((short)(m.mouseData >> 16));
                    l.Send(Wire.Build(Msg.Wheel, w => { w.Write(delta); w.Write(msg == Native.WM_MOUSEHWHEEL); }));
                    return true;
            }
            int btn = ButtonOf(msg, m.mouseData, out bool down);
            if (btn < 0) return true;
            lock (forwardedKeys)
            {
                if (down) forwardedButtons.Add(btn);
                else if (!forwardedButtons.Remove(btn)) return false; // pressed before switching: let it release here
            }
            l.Send(Wire.Build(Msg.Button, w => { w.Write((byte)btn); w.Write(down); }));
            return true;
        }

        if (msg != Native.WM_MOUSEMOVE || (m.flags & Native.LLMHF_INJECTED) != 0) return false;
        CursorHider.Set(false); // someone is using this PC's own mouse

        if (controlled)
        {
            // You grabbed this PC's own mouse: take the cursor back.
            controlled = false;
            ReleaseInjected();
            l?.Send(Wire.Build(Msg.Yield));
            Fire();
            return false;
        }

        if (l != null && settings.ShareInput && settings.EdgeSwitch && PeerMonitors.Length > 0 && Environment.TickCount64 > cooldownUntil
            && Screens.Crossing(Screens.Monitors, peerPlaced, m.pt.X, m.pt.Y) is (int X, int Y) p)
        {
            var dragged = DraggedWindow();
            if (dragged == 0 && AnyMouseButtonDown()) return false;
            GoToPeer(l, (p.X - settings.PeerOffsetX, p.Y - settings.PeerOffsetY));
            if (dragged != 0)
            {
                // Dragging a window across: it goes along. Keep the held button so letting go drops it over there.
                movingHwnd = 0;
                lock (forwardedKeys) forwardedButtons.Add(0);
                manualDrag = 0;
                if (HandDraggedToPeer(l, dragged, m.pt.X, m.pt.Y)) OnInjector(InjectEscape); // never inject from the hook thread
            }
            return true;
        }
        return false;
    }

    /// <summary>Hand mouse + keyboard to the peer. entry = landing point in the peer's coordinates; null = middle of its main screen (hotkey).</summary>
    void GoToPeer(Link l, (int X, int Y)? entry)
    {
        Native.GetCursorPos(out var cur);
        // Park the (hidden) cursor mid-screen: at the edge, Windows would clip away any motion toward that edge.
        var mon = Screens.At(Screens.Monitors, cur.X, cur.Y) ?? Screens.Nearest(Screens.Monitors, cur.X, cur.Y);
        pin = pinHome = new Native.POINT { X = mon.Left + mon.Width / 2, Y = mon.Top + mon.Height / 2 };
        lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
        movesSent = 0;
        float myScale = (float)Screens.ScaleAt(pin.X, pin.Y);
        controlling = true;
        Native.SetCursorPos(pin.X, pin.Y);
        CursorHider.Set(true);
        bool edgeReturn = settings.EdgeSwitch;
        l.Send(Wire.Build(Msg.Enter, w =>
        {
            w.Write(entry == null);
            w.Write(entry?.X ?? 0); w.Write(entry?.Y ?? 0);
            w.Write(edgeReturn);
            w.Write(myScale);
        }));
        ThreadPool.QueueUserWorkItem(_ => Fire());
    }

    bool hotkeyDigitDown;
    // Tracked here rather than via GetAsyncKeyState: while controlling the other PC we swallow
    // Ctrl/Alt before Windows records them, so GetAsyncKeyState would say they're up and the
    // "come back" hotkey could never fire.
    bool ctrlL, ctrlR, altL, altR;

    void TrackModifier(uint vk, bool down)
    {
        switch (vk)
        {
            case 0xA2: ctrlL = down; break;
            case 0xA3: ctrlR = down; break;
            case 0x11: ctrlL = ctrlR = down; break;
            case 0xA4: altL = down; break;
            case 0xA5: altR = down; break;
            case 0x12: altL = altR = down; break;
        }
    }

    /// <summary>Ctrl+Alt+digit. Returns true if the key was consumed.</summary>
    bool HandleHotkey(Link l, uint vk, bool down)
    {
        int digit = vk is >= 0x31 and <= 0x39 ? (int)vk - 0x30 : vk is >= 0x61 and <= 0x69 ? (int)vk - 0x60 : 0;
        if (digit == 0 || !settings.HotkeySwitch || !settings.ShareInput) return false;
        if (!down) { if (!hotkeyDigitDown) return false; hotkeyDigitDown = false; return true; }
        bool ctrl = ctrlL || ctrlR, alt = altL || altR;
        int me = MyNumber, peer = 3 - me;
        if (!ctrl || !alt || (digit != me && digit != peer)) return false;
        hotkeyDigitDown = true;
        if (digit == peer && !controlling) GoToPeer(l, null);
        else if (digit == me && controlling) ThreadPool.QueueUserWorkItem(_ => ComeHome(null));
        return true;
    }

    static bool AnyMouseButtonDown() =>
        (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x04) & 0x8000) != 0;

    static int ButtonOf(int msg, uint data, out bool down)
    {
        down = msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN;
        return msg switch
        {
            Native.WM_LBUTTONDOWN or Native.WM_LBUTTONUP => 0,
            Native.WM_RBUTTONDOWN or Native.WM_RBUTTONUP => 1,
            Native.WM_MBUTTONDOWN or Native.WM_MBUTTONUP => 2,
            Native.WM_XBUTTONDOWN or Native.WM_XBUTTONUP => (data >> 16) == 1 ? 3 : 4,
            _ => -1,
        };
    }

    const uint VK_SCROLL = 0x91;

    bool OnKey(int msg, in Native.KBDLLHOOKSTRUCT k)
    {
        if (k.extra == Inject.Magic) return false;
        bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        TrackModifier(k.vkCode, down);
        var l = link;
        if (l == null) return false;
        if (!controlled && HandleHotkey(l, k.vkCode, down)) return true;
        if (!controlling) return false;

        if (k.vkCode == VK_SCROLL)
        {
            if (down) ThreadPool.QueueUserWorkItem(_ => ComeHome(null));
            return true;
        }
        lock (forwardedKeys)
        {
            if (down) forwardedKeys.Add(k.vkCode);
            else if (!forwardedKeys.Remove(k.vkCode)) return false;
        }
        ushort vk = (ushort)k.vkCode, scan = (ushort)k.scanCode;
        bool ext = (k.flags & Native.LLKHF_EXTENDED) != 0;
        l.Send(Wire.Build(Msg.Key, w => { w.Write(vk); w.Write(scan); w.Write(ext); w.Write(down); }));
        return true;
    }

    /// <summary>Stop controlling the peer and put the cursor back on this PC. at = where it crossed back (this PC's coordinates).</summary>
    void ComeHome((int X, int Y)? at, int peerApplied = -1)
    {
        if (!controlling) return;
        controlling = false;
        cooldownUntil = Environment.TickCount64 + 40;
        if (at is (int X, int Y) p)
        {
            // Moves we sent after the peer decided to hand back never reached its cursor: apply them here.
            int x = p.X, y = p.Y, sent = movesSent;
            if (peerApplied >= 0 && sent - peerApplied is > 0 and <= 1024)
                for (int i = peerApplied; i < sent; i++) { x += sentMoves[i & 1023].dx; y += sentMoves[i & 1023].dy; }
            (x, y) = Screens.ClampOnto(Screens.Monitors, x, y, 3);
            Native.SetCursorPos(x, y);
        }
        else
        {
            link?.Send(Wire.Build(Msg.Release));
            Native.SetCursorPos(pin.X, pin.Y);
        }
        CursorHider.Set(false);
        lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
        Fire();
    }

    public void ReleaseControl() => ComeHome(null);

    // ======================== remote messages (reader thread) ========================

    void OnMessage(Link l, byte[] msg)
    {
        var type = (Msg)msg[0];
        using var r = Wire.Read(msg);
        switch (type)
        {
            case Msg.Hello:
                r.ReadByte(); // legacy side
                PeerName = r.ReadString();
                SetPeerMonitors(ReadMonitors(r));
                bool modern = r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() >= Protocol;
                if (modern)
                {
                    bool peerHas = r.ReadBoolean();
                    long stamp = r.ReadInt64(); int px = r.ReadInt32(), py = r.ReadInt32();
                    if (peerHas) AdoptLayout(stamp, px, py, l.PeerId);
                    else EnsureLayout(announce: true);
                    if (!peerHas) SendLayout();
                    Warning = null;
                }
                else
                {
                    EnsureLayout(announce: false);
                    Warning = $"{PeerName} runs an older LAN Mouse. Install this version there too so screen arrangement and clipboard work.";
                }
                Fire();
                break;

            case Msg.Screens:
                SetPeerMonitors(ReadMonitors(r));
                EnsureLayout(announce: true);
                Fire();
                break;

            case Msg.Layout:
            {
                long stamp = r.ReadInt64(); int px = r.ReadInt32(), py = r.ReadInt32();
                if (stamp >= settings.LayoutStamp) SetOffset(-px, -py, stamp);
                EnsureLayout(announce: true);
                Fire();
                break;
            }

            case Msg.Enter:
                if (!settings.ShareInput) break;
                nextDesktopCheck = 0;
                bool center = r.ReadBoolean();
                int ex = r.ReadInt32(), ey = r.ReadInt32();
                peerAllowsEdgeReturn = r.ReadBoolean();
                senderScale = r.BaseStream.Position + 4 <= r.BaseStream.Length ? Math.Clamp(r.ReadSingle(), 0.5f, 5f) : 1;
                carryX = carryY = 0;
                if (center)
                {
                    // Hotkey jump: land in the middle of the main screen.
                    var pb = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                    (rx, ry) = (pb.Left + pb.Width / 2, pb.Top + pb.Height / 2);
                }
                else (rx, ry) = Screens.ClampOnto(Screens.Monitors, ex, ey, 1);
                movesApplied = 0;
                inputBlocked = false;
                controlled = true;
                if (SecureDesktopBlocks(l)) break;
                InjMove(rx, ry);
                CursorHider.Set(false);
                Fire();
                break;

            case Msg.Move:
                if (!controlled || SecureDesktopBlocks(l)) break;
                double f = Screens.ScaleAt(rx, ry) / senderScale;
                double mx = r.ReadInt16() * f + carryX, my = r.ReadInt16() * f + carryY;
                int ix = (int)Math.Round(mx), iy = (int)Math.Round(my);
                carryX = mx - ix; carryY = my - iy;
                int nx = rx + ix, ny = ry + iy;
                movesApplied++;
                if (Screens.At(Screens.Monitors, nx, ny) == null)
                {
                    // Off our screens. If that spot is one of the controller's screens, hand the cursor back there.
                    // Not while input is blocked with no agent: the real cursor is frozen (e.g. on Task Manager) and
                    // our dead-reckoned position would march off the edge and bounce back for no reason.
                    if ((secureActive || !inputBlocked) && peerAllowsEdgeReturn && Screens.At(peerPlaced, nx, ny) != null)
                    {
                        var dragged = DraggedWindow();
                        controlled = false;
                        int bx = nx - settings.PeerOffsetX, by = ny - settings.PeerOffsetY, applied = movesApplied;
                        l.Send(Wire.Build(Msg.Return, w => { w.Write(bx); w.Write(by); w.Write(applied); }));
                        if (dragged != 0)
                        {
                            movingHwnd = 0;
                            manualDrag = 0;
                            if (HandDraggedToPeer(l, dragged, rx, ry)) InjectEscape();
                        }
                        ReleaseInjected();
                        CursorHider.Set(true); // cursor left this PC
                        ThreadPool.QueueUserWorkItem(_ => Fire());
                        break;
                    }
                    (nx, ny) = Screens.Clamp(nx, ny);
                }
                (rx, ry) = (nx, ny);
                InjMove(rx, ry);
                break;

            case Msg.Button:
                if (!controlled) break;
                nextDesktopCheck = 0; // a click is what usually opens the sign-in prompt: check right away next time
                int b = r.ReadByte(); bool bd = r.ReadBoolean();
                lock (injectedKeys) { if (bd) injectedButtons.Add(b); else injectedButtons.Remove(b); }
                InjButton(b, bd);
                break;

            case Msg.Wheel:
                if (!controlled) break;
                short delta = r.ReadInt16();
                InjWheel(delta, r.ReadBoolean());
                break;

            case Msg.Key:
                if (!controlled || SecureDesktopBlocks(l)) break;
                ushort vk = r.ReadUInt16(), scan = r.ReadUInt16(); bool ext = r.ReadBoolean(), kd = r.ReadBoolean();
                lock (injectedKeys) { if (kd) injectedKeys.Add((vk, scan, ext)); else injectedKeys.Remove((vk, scan, ext)); }
                InjKey(vk, scan, ext, kd);
                break;

            case Msg.Return:
                int hx = r.ReadInt32(), hy = r.ReadInt32();
                int peerApplied = r.BaseStream.Position < r.BaseStream.Length ? r.ReadInt32() : -1;
                ComeHome((hx, hy), peerApplied);
                break;

            case Msg.Locked:
                if (controlling)
                {
                    controlling = false;
                    cooldownUntil = Environment.TickCount64 + 1500; // don't slide straight back onto the locked screen
                    lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
                    Native.SetCursorPos(pinHome.X, pinHome.Y);
                    CursorHider.Set(false);
                    Fire();
                }
                Notify?.Invoke($"{PeerName} is showing its sign-in screen. Windows only lets you type the PIN with that PC's own keyboard.");
                break;

            case Msg.Yield:
                if (controlling) { controlling = false; lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); } CursorHider.Set(false); Fire(); }
                break;

            case Msg.Release:
                if (controlled) CursorHider.Set(true); // controller pulled the cursor back home
                controlled = false;
                ReleaseInjected();
                Fire();
                break;

            default:
                OnShareMessage(l, type, r);
                break;

            case Msg.Offer:
            {
                int id = r.ReadInt32();
                var kinds = (ClipKinds)r.ReadByte() & Allowed();
                string summary = r.ReadString();
                if (kinds != ClipKinds.None) Clipboard?.ApplyOffer(new ClipOffer(id, kinds, summary));
                break;
            }
        }
    }

    ClipKinds Allowed() =>
        (settings.ShareText ? ClipKinds.Text : 0) | (settings.ShareImages ? ClipKinds.Image : 0) | (settings.ShareFiles ? ClipKinds.Files : 0);

    // ======================== clipboard: offers (control link) ========================

    /// <summary>Tell the peer what we just copied. Only a description goes over the network now; data moves on paste.</summary>
    public void SendOffer(int id, ClipKinds kinds, string summary)
    {
        kinds &= Allowed();
        if (kinds == ClipKinds.None) return;
        link?.Send(Wire.Build(Msg.Offer, w => { w.Write(id); w.Write((byte)kinds); w.Write(summary); }));
    }

    // ======================== clipboard: transfers (separate connections) ========================
    // Every paste opens its own encrypted connection, so a big file never queues in front of mouse/keyboard input
    // and the receiving side writes to disk on a thread that has nothing to do with input.

    void Track(Link l) { lock (transfers) transfers.Add(l); }
    void Untrack(Link l) { lock (transfers) transfers.Remove(l); }

    void AbortTransfers()
    {
        Link[] all;
        lock (transfers) { all = transfers.ToArray(); transfers.Clear(); }
        foreach (var t in all) t.Dispose();
    }

    async Task DataAcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket s;
            try { s = await dataListener!.AcceptAsync(ct); }
            catch { return; }
            _ = Task.Run(() => ServeTransfer(s));
        }
    }

    /// <summary>The peer is pasting something we offered: stream it over this dedicated connection.</summary>
    async Task ServeTransfer(Socket s)
    {
        if (cts == null || link == null) { s.Dispose(); return; }
        var l = Link.Handshake(s, settings.Id, master, initiator: false, dataLink: true);
        if (l == null) return;
        Track(l);
        try
        {
            var first = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            l.Message += (_, m) => first.TrySetResult(m);
            l.Closed += _ => { first.TrySetCanceled(); closed.TrySetResult(); };
            l.Start();
            var req = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if ((Msg)req[0] != Msg.Pull) return;
            int id; ClipKinds kind;
            using (var r = Wire.Read(req)) { id = r.ReadInt32(); kind = (ClipKinds)r.ReadByte(); }

            var data = (kind & Allowed()) == 0 ? null : Clipboard?.ReadForPeer(id, kind);
            switch (data)
            {
                case string text when kind == ClipKinds.Text && text.Length <= 4_000_000:
                    l.Send(Wire.Build(Msg.Text, w => w.Write(text)));
                    break;
                case byte[] png when kind == ClipKinds.Image:
                    await SendBlob(l, Msg.ImageStart, png.LongLength, w => w.Write(png.LongLength), new[] { (Stream)new MemoryStream(png) }, "picture");
                    break;
                case string[] roots when kind == ClipKinds.Files:
                    await SendFiles(l, roots);
                    break;
                default:
                    l.Send(Wire.Build(Msg.Gone));
                    break;
            }
            l.FinishSending();
            await l.Drained;
            // Let the receiver hang up first so nothing still in flight gets cut off.
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception e) when (e is not OperationCanceledException and not TimeoutException)
        {
            Notify?.Invoke($"Couldn't send to {PeerName}: {e.Message}");
            Progress?.Invoke(null, -1);
        }
        catch { }
        finally { Untrack(l); l.Dispose(); }
    }

    async Task SendFiles(Link l, string[] roots)
    {
        var entries = new List<(string rel, string full, long size)>();
        foreach (var root in roots)
        {
            if (File.Exists(root)) entries.Add((Path.GetFileName(root), root, new FileInfo(root).Length));
            else if (Directory.Exists(root))
            {
                var baseDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) ?? root;
                entries.Add((Path.GetRelativePath(baseDir, root), root, -1));
                foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                    entries.Add((Path.GetRelativePath(baseDir, d), d, -1));
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    entries.Add((Path.GetRelativePath(baseDir, f), f, new FileInfo(f).Length));
            }
        }
        if (entries.Count == 0) { l.Send(Wire.Build(Msg.Gone)); return; }
        long total = entries.Where(e => e.size > 0).Sum(e => e.size);
        var meta = JsonSerializer.Serialize(entries.Select(e => new FileEntry(e.rel.Replace('\\', '/'), e.size)).ToArray());
        var streams = entries.Where(e => e.size >= 0).Select(e => (Stream)new FileStream(e.full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan));
        int fileCount = entries.Count(e => e.size >= 0);
        await SendBlob(l, Msg.FilesStart, total, w => { w.Write(total); w.Write(meta); }, streams,
            fileCount == 1 ? Path.GetFileName(entries.First(e => e.size >= 0).full) : $"{fileCount} files");
    }

    async Task SendBlob(Link l, Msg kind, long total, Action<BinaryWriter> header, IEnumerable<Stream> streams, string label)
    {
        var ct = CancellationToken.None;
        await l.SendBulkAsync(Wire.Build(kind, header), ct);
        long sent = 0, lastReport = 0;
        var buf = new byte[256 * 1024];
        foreach (var s in streams)
        {
            using (s)
            {
                long remaining = s.Length;
                while (remaining > 0)
                {
                    int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct);
                    if (n <= 0) throw new IOException("File changed while sending");
                    remaining -= n;
                    sent += n;
                    var chunk = Wire.Build(Msg.Chunk, w => w.Write(buf, 0, n));
                    if (l.IsClosed) throw new IOException("Disconnected");
                    await l.SendBulkAsync(chunk, ct);
                    if (total > 4_000_000 && sent - lastReport > total / 100) { lastReport = sent; Progress?.Invoke($"Sending {label}", sent / (double)total); }
                }
            }
        }
        await l.SendBulkAsync(Wire.Build(Msg.End), ct);
        Progress?.Invoke(null, -1);
    }

    /// <summary>
    /// Fetch one kind of the peer's offer, right now (called when something on this PC pastes it).
    /// Returns string (text), byte[] (PNG) or string[] (paths of the received files), or null.
    /// </summary>
    public async Task<object?> PullAsync(ClipOffer offer, ClipKinds kind, CancellationToken ct)
    {
        var main = link;
        if (main == null || cts == null) return null;
        Link? l = null;
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectTimeout.CancelAfter(4000);
                try { await s.ConnectAsync(main.Address, DataPort, connectTimeout.Token).ConfigureAwait(false); }
                catch { s.Dispose(); Notify?.Invoke($"Couldn't reach {PeerName} to paste. Click \"Allow through firewall\" on that PC."); return null; }
            }
            l = await Task.Run(() => Link.Handshake(s, settings.Id, master, initiator: true, dataLink: true), ct).ConfigureAwait(false);
            if (l == null) return null;
            Track(l);
            var download = new Download(this, offer.Summary);
            l.Message += (_, m) => download.Handle(m);
            l.Closed += _ => download.Fail();
            l.Start();
            l.Send(Wire.Build(Msg.Pull, w => { w.Write(offer.Id); w.Write((byte)kind); }));
            using var reg = ct.Register(() => l.Dispose());
            return await download.Result.ConfigureAwait(false);
        }
        catch { return null; }
        finally
        {
            if (l != null) { Untrack(l); l.Dispose(); }
        }
    }

    record FileEntry(string Path, long Size);

    void ReportProgress(string? text, double frac) => Progress?.Invoke(text, frac);
    void ReportNotify(string text) => Notify?.Invoke(text);

    /// <summary>Receives one transfer (text, picture or files) on its own connection.</summary>
    sealed class Download
    {
        readonly Engine e;
        readonly string label;
        readonly TaskCompletionSource<object?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object?> Result => done.Task;

        long total, got, lastReport;
        MemoryStream? image;
        FileEntry[] files = Array.Empty<FileEntry>();
        string folder = "";
        int index = -1;
        FileStream? current;
        long remaining;

        public Download(Engine e, string label) { this.e = e; this.label = label; }

        public void Fail()
        {
            current?.Dispose();
            current = null;
            if (done.TrySetResult(null) && total > 4_000_000) e.ReportProgress(null, -1);
        }

        public void Handle(byte[] msg)
        {
            if (done.Task.IsCompleted) return;
            try
            {
                using var r = Wire.Read(msg);
                switch ((Msg)msg[0])
                {
                    case Msg.Gone:
                        done.TrySetResult(null);
                        break;

                    case Msg.Text:
                        done.TrySetResult(r.ReadString());
                        break;

                    case Msg.ImageStart:
                        total = r.ReadInt64();
                        if (total > 256_000_000) throw new InvalidDataException("Picture too large");
                        image = new MemoryStream((int)total);
                        break;

                    case Msg.FilesStart:
                        total = r.ReadInt64();
                        files = JsonSerializer.Deserialize<FileEntry[]>(r.ReadString()) ?? Array.Empty<FileEntry>();
                        if (files.Length == 0) { done.TrySetResult(null); return; }
                        var stamp = Path.Combine(ReceiveDir, DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
                        folder = stamp;
                        for (int n = 2; Directory.Exists(folder); n++) folder = $"{stamp} ({n})";
                        Directory.CreateDirectory(folder);
                        if (total > 20_000_000) e.ReportNotify($"Pasting {label} from {e.PeerName}...");
                        NextFile();
                        break;

                    case Msg.Chunk:
                        var data = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
                        got += data.Length;
                        if (image != null) image.Write(data);
                        else
                        {
                            if (current == null || data.Length > remaining) throw new InvalidDataException("Transfer out of sync");
                            current.Write(data);
                            remaining -= data.Length;
                            if (remaining == 0) NextFile();
                        }
                        if (total > 4_000_000 && got - lastReport > total / 100)
                        {
                            lastReport = got;
                            e.ReportProgress(image != null ? "Pasting picture" : $"Pasting {label}", got / (double)total);
                        }
                        break;

                    case Msg.End:
                        if (total > 4_000_000) e.ReportProgress(null, -1);
                        if (image != null) { done.TrySetResult(image.ToArray()); return; }
                        current?.Dispose();
                        current = null;
                        var roots = files.Select(f => f.Path.Split('/')[0]).Distinct()
                            .Select(name => Path.Combine(folder, Sanitize(name))).ToArray();
                        done.TrySetResult(roots);
                        break;
                }
            }
            catch (Exception ex)
            {
                e.ReportNotify($"Paste failed: {ex.Message}");
                Fail();
            }
        }

        void NextFile()
        {
            current?.Dispose();
            current = null;
            while (++index < files.Length)
            {
                var f = files[index];
                var path = SafePath(folder, f.Path);
                if (f.Size < 0) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                current = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                remaining = f.Size;
                if (f.Size > 0) return;
                current.Dispose();
                current = null;
            }
        }
    }

    static string Sanitize(string segment)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) segment = segment.Replace(c, '_');
        segment = segment.Trim().TrimEnd('.');
        return segment is "" or "." or ".." ? "_" : segment;
    }

    /// <summary>Never let a remote path escape the receive folder.</summary>
    static string SafePath(string folder, string rel)
    {
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Sanitize).ToArray();
        var full = Path.GetFullPath(Path.Combine(new[] { folder }.Concat(parts).ToArray()));
        if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe path");
        return full;
    }
}
