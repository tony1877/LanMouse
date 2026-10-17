using System;
using System.Collections.Concurrent;
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

/// <summary>What another PC has on its clipboard. Nothing is transferred until it is actually pasted.</summary>
sealed record ClipOffer(string PeerId, int Id, ClipKinds Kinds, string Summary);

interface IClipboardSink
{
    /// <summary>A PC copied something: advertise it on our clipboard without fetching it.</summary>
    void ApplyOffer(ClipOffer offer);
    /// <summary>That PC is gone: drop anything we were advertising on its behalf.</summary>
    void ClearOffer(string peerId);
    /// <summary>A PC is pasting our offer #id: return string (text), byte[] (PNG) or string[] (file paths), or null if it changed.</summary>
    object? ReadForPeer(int id, ClipKinds kind);
}

/// <summary>One connected PC.</summary>
sealed class Peer
{
    public Peer(Link link) { Link = link; }
    public Link Link { get; }
    public string Id => Link.PeerId;
    public string Name { get; set; } = "";
    public string Address => Link.Address;
    public Screens.Bounds[] Monitors { get; set; } = Array.Empty<Screens.Bounds>();
    /// <summary>Runs a version that understands multi-PC arrangements.</summary>
    public bool Modern { get; set; }
    public void Send(byte[] msg) => Link.Send(msg);
}

/// <summary>A PC as the UI draws it.</summary>
sealed record PcView(string Id, string Name, int Number, bool IsSelf, bool Online, bool Known, Screens.Bounds[] Monitors, (int X, int Y) Pos);

sealed partial class Engine : IDisposable
{
    public const int TcpPort = 47823, UdpPort = 47824, DataPort = 47825;
    const byte Protocol = 5; // 5 = any number of PCs
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
    readonly ConcurrentDictionary<string, Peer> peers = new();
    readonly HashSet<string> dialing = new();
    readonly HashSet<Link> transfers = new();

    // -- observable state (read by UI) --
    public Phase Phase { get; private set; }
    public string? Warning { get; private set; }
    public bool Controlling => target != null;
    public bool Controlled => controller != null;
    public string ControllingName => target?.Name ?? "";
    public string ControlledByName => controller?.Name ?? "";
    public string? ControllingId => target?.Id;
    public IReadOnlyList<Peer> Peers => peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public event Action? Changed;
    public event Action<string>? Notify;
    public event Action<string?, double>? Progress;

    // -- input state --
    volatile Peer? target;       // the PC our mouse and keyboard currently drive (null = this PC)
    volatile Peer? controller;   // the PC driving us right now (null = nobody)
    Native.POINT pin, pinHome;
    readonly HashSet<uint> forwardedKeys = new();
    readonly HashSet<int> forwardedButtons = new();
    readonly HashSet<(ushort vk, ushort scan, bool ext)> injectedKeys = new();
    readonly HashSet<int> injectedButtons = new();
    int rx, ry;
    bool controllerAllowsEdge = true;
    // Moves sent while controlling, so the ones still in flight when a PC hands the cursor on aren't lost.
    readonly (short dx, short dy)[] sentMoves = new (short, short)[1024];
    int movesSent, movesApplied;
    // Display scaling: movement is converted so the pointer covers the same visual distance on every PC.
    double senderScale = 1, carryX, carryY;
    long nextDesktopCheck;
    readonly SecureInput secure = new();
    volatile bool secureActive;   // injecting via the SYSTEM agent (sign-in screen, or an elevated app blocks us)
    volatile bool inputBlocked;   // input can't reach the foreground (secure desktop or elevated app) without the agent
    long cooldownUntil;

    /// <summary>
    /// Controlled side: decide how to deliver input. Normal apps take direct SendInput. The secure sign-in screen,
    /// and elevated foreground apps like Task Manager (which Windows stops ordinary input from reaching), are routed
    /// through the SYSTEM agent when the user installed sign-in support. Only the unreachable sign-in screen with no
    /// agent forces control back; a merely-blocked elevated app does not (that would feel like a random bounce).
    /// Returns true only when control was handed back.
    /// </summary>
    bool SecureDesktopBlocks(Peer from)
    {
        long now = Environment.TickCount64;
        if (now < nextDesktopCheck) return false;
        nextDesktopCheck = now + 200;
        bool deskUnreachable = !Native.InputDesktopReachable();
        bool elevated = !deskUnreachable && Native.ForegroundInputBlocked();
        onSecureDesktop = deskUnreachable;
        if (!deskUnreachable && !elevated) { secureActive = false; inputBlocked = false; return false; }
        if (settings.SignInSupport && !agentBroken && secure.TryConnect())
        {
            if (!secureActive) SecureInput.ClientLog($"routing input through the agent ({(deskUnreachable ? "sign-in screen" : "elevated app")})");
            secureActive = true; inputBlocked = false; return false;
        }
        var why = $"cannot route through the agent: support={settings.SignInSupport} broken={agentBroken}; {(deskUnreachable ? "sign-in screen unreachable, handing control back" : "elevated app blocks input")}";
        if (why != lastRouteNote) SecureInput.ClientLog(lastRouteNote = why);
        secureActive = false;
        inputBlocked = true;
        if (deskUnreachable)
        {
            controller = null;
            ReleaseInjected();
            from.Send(Wire.Build(Msg.Locked));
            ThreadPool.QueueUserWorkItem(_ => Fire());
            return true;
        }
        return false;
    }

    public Engine(Settings settings)
    {
        this.settings = settings;
        LoadWorld();
        FixLayout(announce: false);
        hook = new InputHook(OnMouse, OnKey);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
    }

    void Fire() => Changed?.Invoke();

    // ======================== arrangement ========================

    World world = new();
    readonly object worldGate = new();

    string MyId => settings.Id;

    void LoadWorld()
    {
        lock (worldGate)
        {
            world = new World { Stamp = settings.WorldStamp };
            foreach (var (id, p) in settings.WorldPositions)
                if (p is { Length: 2 }) world.Pos[id] = (p[0], p[1]);
        }
    }

    void SaveWorld()
    {
        settings.WorldPositions = world.Pos.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.X, kv.Value.Y });
        settings.WorldStamp = world.Stamp;
        settings.Save();
    }

    /// <summary>Monitors of any PC in its own coordinates: live if connected, else as last seen, else a stand-in.</summary>
    Screens.Bounds[] MonitorsOf(string id)
    {
        if (id == MyId) return Screens.Reported;
        if (peers.TryGetValue(id, out var p) && p.Monitors.Length > 0) return p.Monitors;
        if (settings.KnownPcs.TryGetValue(id, out var k))
        {
            var cache = k.Monitors.Where(a => a is { Length: 4 } && a[2] > 0 && a[3] > 0)
                .Select(a => new Screens.Bounds(a[0], a[1], a[2], a[3])).ToArray();
            if (cache.Length > 0) return cache;
        }
        return World.Placeholder;
    }

    bool MonitorsKnown(string id) =>
        id == MyId || (peers.TryGetValue(id, out var p) && p.Monitors.Length > 0) ||
        (settings.KnownPcs.TryGetValue(id, out var k) && k.Monitors.Length > 0);

    string NameOf(string id) =>
        id == MyId ? Environment.MachineName :
        peers.TryGetValue(id, out var p) && p.Name != "" ? p.Name :
        settings.KnownPcs.TryGetValue(id, out var k) && k.Name != "" ? k.Name : "Other PC";

    /// <summary>Place PCs that have no spot yet and untangle overlaps. Tells everyone if it changed anything.</summary>
    void FixLayout(bool announce)
    {
        bool changed;
        lock (worldGate)
        {
            var ids = new List<string> { MyId };
            ids.AddRange(peers.Values.Where(p => p.Modern).Select(p => p.Id));
            changed = world.Fix(ids, MonitorsOf);
            if (changed) { world.Stamp = DateTime.UtcNow.Ticks; SaveWorld(); }
        }
        RebuildRoutes();
        if (changed && announce) BroadcastLayout();
    }

    byte[] LayoutMessage()
    {
        World w;
        lock (worldGate) w = world.Clone();
        return Wire.Build(Msg.Layout, wr =>
        {
            wr.Write(w.Stamp);
            wr.Write(w.Pos.Count);
            foreach (var (id, p) in w.Pos) { wr.Write(id); wr.Write(p.X); wr.Write(p.Y); }
        });
    }

    void BroadcastLayout()
    {
        var msg = LayoutMessage();
        foreach (var p in peers.Values) if (p.Modern) p.Send(msg);
    }

    /// <summary>Another PC's view of the arrangement. The newer one wins (ties go to the lower id); PCs it doesn't know about are kept.</summary>
    void AdoptLayout(Peer from, BinaryReader r)
    {
        long stamp = r.ReadInt64();
        int n = r.ReadInt32();
        var theirs = new Dictionary<string, (int, int)>();
        for (int i = 0; i < n && i < 64; i++) { var id = r.ReadString(); theirs[id] = (r.ReadInt32(), r.ReadInt32()); }
        lock (worldGate)
        {
            bool newer = stamp > world.Stamp || (stamp == world.Stamp && string.CompareOrdinal(from.Id, MyId) < 0);
            if (newer && theirs.Count > 0)
            {
                var keep = world.Pos.Where(kv => !theirs.ContainsKey(kv.Key)).ToList();
                world.Pos.Clear();
                foreach (var (id, p) in theirs) world.Pos[id] = p;
                // PCs only we know about: re-place them next to the adopted arrangement if they now collide.
                foreach (var (id, p) in keep) world.Pos[id] = p;
                world.Stamp = stamp;
                SaveWorld();
            }
        }
        FixLayout(announce: true);
        Fire();
    }

    /// <summary>User dragged a PC to a new spot (world coordinates of its desktop origin). Snaps, saves and tells everyone.</summary>
    public void MovePc(string id, int wantX, int wantY)
    {
        lock (worldGate)
        {
            if (!world.Pos.ContainsKey(id)) return;
            world.Pos[id] = world.SnapPosition(id, wantX, wantY, MonitorsOf);
            world.Stamp = DateTime.UtcNow.Ticks;
            SaveWorld();
        }
        RebuildRoutes();
        BroadcastLayout();
        Fire();
    }

    /// <summary>Drop PCs that aren't connected from the arrangement (and forget them).</summary>
    public void ForgetOffline()
    {
        lock (worldGate)
        {
            foreach (var id in world.Pos.Keys.ToList())
                if (id != MyId && !peers.ContainsKey(id)) { world.Pos.Remove(id); settings.KnownPcs.Remove(id); }
            world.Stamp = DateTime.UtcNow.Ticks;
            SaveWorld();
        }
        FixLayout(announce: false);
        BroadcastLayout();
        Fire();
    }

    /// <summary>Every PC in the arrangement, numbered, for drawing.</summary>
    public List<PcView> LayoutView()
    {
        lock (worldGate)
        {
            var order = world.Numbered(MonitorsOf);
            return order.Select((id, i) => new PcView(id, NameOf(id), i + 1, id == MyId, id == MyId || peers.ContainsKey(id),
                MonitorsKnown(id), world.Placed(id, MonitorsOf), world.Pos[id])).ToList();
        }
    }

    public int MyNumber => LayoutView().FirstOrDefault(v => v.IsSelf)?.Number ?? 1;

    /// <summary>What the hook thread needs, precomputed: our monitors and every reachable PC's monitors in OUR coordinates.</summary>
    sealed record Route(Peer Peer, Screens.Bounds[] Mons, int Dx, int Dy);
    volatile Route[] routes = Array.Empty<Route>();
    volatile string[] numbered = Array.Empty<string>();

    void RebuildRoutes()
    {
        lock (worldGate)
        {
            if (!world.Pos.TryGetValue(MyId, out var me)) me = (0, 0);
            var list = new List<Route>();
            foreach (var p in peers.Values)
            {
                if (!p.Modern || p.Monitors.Length == 0 || !world.Pos.TryGetValue(p.Id, out var pos)) continue;
                int dx = pos.X - me.X, dy = pos.Y - me.Y;
                list.Add(new Route(p, Screens.Shift(p.Monitors, dx, dy), dx, dy));
            }
            routes = list.ToArray();
            numbered = world.Numbered(MonitorsOf).ToArray();
        }
    }

    void OnDisplaysChanged(object? sender, EventArgs e)
    {
        Screens.Refresh();
        var msg = Wire.Build(Msg.Screens, WriteMonitors);
        foreach (var p in peers.Values) p.Send(msg);
        FixLayout(announce: true);
        Fire();
    }

    void SetPeerMonitors(Peer p, Screens.Bounds[] mons)
    {
        p.Monitors = mons;
        if (!settings.KnownPcs.TryGetValue(p.Id, out var k)) settings.KnownPcs[p.Id] = k = new KnownPc();
        if (p.Name != "") k.Name = p.Name;
        if (mons.Length > 0) k.Monitors = mons.Select(m => new[] { m.Left, m.Top, m.Width, m.Height }).ToArray();
        settings.Save();
    }

    static void WriteMonitors(BinaryWriter w)
    {
        var mons = Screens.Reported;
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
            listener.Listen(16);
            dataListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            dataListener.Bind(new IPEndPoint(IPAddress.Any, DataPort));
            dataListener.Listen(16);
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
        Phase = Phase.Stopped;
        foreach (var p in peers.Values) { p.Link.Closed -= OnClosed; p.Link.Dispose(); Clipboard?.ClearOffer(p.Id); }
        peers.Clear();
        RebuildRoutes();
        AbortTransfers();
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
        if (!peers.IsEmpty || Warning == text) return;
        Warning = text;
        Fire();
    }

    static bool IsLocalAddress(IPAddress a)
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(u => u.Address.Equals(a)); }
        catch { return false; }
    }

    IEnumerable<string> ManualAddresses() =>
        settings.PeerAddress.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    async Task AnnounceLoop(CancellationToken ct)
    {
        searchStarted = Environment.TickCount64;
        lastHeard = 0;
        var id = Convert.FromHexString(settings.Id);
        while (!ct.IsCancellationRequested)
        {
            // Keep announcing even when connected: a third PC may join at any time.
            var packet = DiscoveryPacket(id, Bucket());
            foreach (var t in BroadcastTargets())
            {
                try { await udp!.SendAsync(packet, new IPEndPoint(t, UdpPort), ct); } catch { }
            }
            if (peers.IsEmpty && lastHeard == 0 && Environment.TickCount64 - searchStarted > 8000 && Warning == null)
                Hint("Nothing heard from another LAN Mouse yet. Make sure LanMouse.exe is running there with Connect pressed, then click \"Allow through firewall\" on EVERY PC. If it still fails, type the other PC's IP in Settings.");
            var connectedAddrs = peers.Values.Select(p => p.Address).ToHashSet();
            foreach (var host in ManualAddresses())
                if (!connectedAddrs.Contains(host)) _ = Task.Run(() => Connect(host));
            var ping = Wire.Build(Msg.Ping);
            foreach (var p in peers.Values) p.Send(ping);
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
            if (myId.AsSpan().SequenceEqual(peerId)) continue;
            if (peers.ContainsKey(Convert.ToHexString(peerId).ToLowerInvariant())) continue;
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
        lock (dialing) if (!dialing.Add(host)) return;
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
        finally { lock (dialing) dialing.Remove(host); }
    }

    void Establish(Socket s, bool initiator)
    {
        if (cts == null) { s.Dispose(); return; }
        var l = Link.Handshake(s, settings.Id, master, initiator);
        if (l == null)
        {
            Hint("Reached another PC, but the pairing keys don't match.");
            return;
        }

        var peer = new Peer(l);
        Peer? old;
        lock (gate)
        {
            peers.TryGetValue(l.PeerId, out old);
            // Two connections to the same PC (both dialed): both sides keep the one the lower id opened.
            if (cts == null || (old != null && !(l.InitiatedByLowerId && !old.Link.InitiatedByLowerId)))
            {
                l.Dispose();
                return;
            }
            peers[l.PeerId] = peer;
        }
        if (old != null) { old.Link.Closed -= OnClosed; old.Link.Dispose(); PeerGone(old); }
        l.Message += OnMessage;
        l.Closed += OnClosed;
        l.Start();
        l.Send(Wire.Build(Msg.Hello, w =>
        {
            w.Write((byte)settings.Side); // v3 peers read this first
            w.Write(Environment.MachineName);
            WriteMonitors(w);
            w.Write(Protocol);
            w.Write(false); w.Write(0L); w.Write(0); w.Write(0); // v4 two-PC layout fields (unused)
        }));
        l.Send(LayoutMessage());
        Phase = Phase.Connected; Warning = null;
        peer.Name = l.Address;
        Fire();
    }

    Peer? PeerOf(Link l) => peers.TryGetValue(l.PeerId, out var p) && p.Link == l ? p : null;

    void OnClosed(Link l)
    {
        Peer? p;
        lock (gate)
        {
            if (!peers.TryGetValue(l.PeerId, out p) || p.Link != l) return;
            peers.TryRemove(l.PeerId, out _);
        }
        PeerGone(p);
        if (peers.IsEmpty && Phase == Phase.Connected) Phase = Phase.Searching;
        Fire();
    }

    /// <summary>A PC disconnected (or its connection was replaced): make sure nothing is left pointing at it.</summary>
    void PeerGone(Peer p)
    {
        RebuildRoutes();
        Clipboard?.ClearOffer(p.Id);
        if (target == p)
        {
            target = null;
            Native.SetCursorPos(pinHome.X, pinHome.Y);
            CursorHider.Set(false);
            lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
        }
        if (controller == p)
        {
            controller = null;
            ReleaseInjected();
        }
        if (peers.IsEmpty) AbortTransfers();
    }

    void InjMove(int x, int y)
    {
        if (!secureActive) { Inject.MoveTo(x, y); return; }
        VerifyAgent();
        secure.Move(x, y);
        if (!onSecureDesktop) agentExpect = (x, y);
    }

    // -- safety net for sign-in support --
    // If the agent accepts our input but nothing happens (stale install, wrong token, broken worker), this PC would
    // be impossible to control. While the normal desktop is up we can see whether the cursor really moved; if it
    // keeps not moving, stop using the agent for this session and go back to normal input.
    volatile bool onSecureDesktop;
    string? lastRouteNote;
    bool agentBroken;
    int agentMisses;
    (int X, int Y)? agentExpect;

    void VerifyAgent()
    {
        if (agentExpect is not (int X, int Y) want || onSecureDesktop) return;
        agentExpect = null;
        Native.GetCursorPos(out var cur);
        if (Math.Abs(cur.X - want.X) <= 2 && Math.Abs(cur.Y - want.Y) <= 2) { agentMisses = 0; return; }
        if (++agentMisses < 15) return;
        agentBroken = true;
        secureActive = false;
        SecureInput.ClientLog("agent is not delivering input; falling back to normal input for this session");
        Warning = "Sign-in support on this PC isn't working, so it was switched off until LAN Mouse restarts. Remove and reinstall it in Settings.";
        Notify?.Invoke(Warning);
        ThreadPool.QueueUserWorkItem(_ => Fire());
    }
    void InjButton(int b, bool down) { if (secureActive) secure.Button(b, down); else Inject.Button(b, down); }
    void InjWheel(short delta, bool horizontal) { if (secureActive) secure.Wheel(delta, horizontal); else Inject.Wheel(delta, horizontal); }
    void InjKey(ushort vk, ushort scan, bool ext, bool down) { if (secureActive) secure.Key(vk, scan, ext, down); else Inject.Key(vk, scan, ext, down); }

    void ResetInputState()
    {
        target = null;
        controller = null;
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

    bool lockSwallowedL;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool LockWorkStation();

    bool OnMouse(int msg, in Native.MSLLHOOKSTRUCT m)
    {
        if (m.extra == Inject.Magic) return false;
        var t = target;

        if (t != null)
        {
            switch (msg)
            {
                case Native.WM_MOUSEMOVE:
                    // Movement is the offset from the hidden cursor's parking spot. Then snap it back there so it never
                    // drifts into a screen edge (where Windows would clamp it and swallow further movement).
                    short dx = (short)Math.Clamp(m.pt.X - pin.X, -32000, 32000), dy = (short)Math.Clamp(m.pt.Y - pin.Y, -32000, 32000);
                    if (dx != 0 || dy != 0)
                    {
                        sentMoves[movesSent & 1023] = (dx, dy);
                        movesSent++;
                        t.Send(Wire.Build(Msg.Move, w => { w.Write(dx); w.Write(dy); }));
                    }
                    Native.SetCursorPos(pinHome.X, pinHome.Y);
                    pin = pinHome;
                    return true;
                case Native.WM_MOUSEWHEEL:
                case Native.WM_MOUSEHWHEEL:
                    short delta = unchecked((short)(m.mouseData >> 16));
                    t.Send(Wire.Build(Msg.Wheel, w => { w.Write(delta); w.Write(msg == Native.WM_MOUSEHWHEEL); }));
                    return true;
            }
            int btn = ButtonOf(msg, m.mouseData, out bool down);
            if (btn < 0) return true;
            lock (forwardedKeys)
            {
                if (down) forwardedButtons.Add(btn);
                else if (!forwardedButtons.Remove(btn)) return false; // pressed before switching: let it release here
            }
            t.Send(Wire.Build(Msg.Button, w => { w.Write((byte)btn); w.Write(down); }));
            return true;
        }

        if (msg != Native.WM_MOUSEMOVE || (m.flags & Native.LLMHF_INJECTED) != 0) return false;
        CursorHider.Set(false); // someone is using this PC's own mouse

        var c = controller;
        if (c != null)
        {
            // You grabbed this PC's own mouse: take the cursor back.
            controller = null;
            ReleaseInjected();
            c.Send(Wire.Build(Msg.Yield));
            ThreadPool.QueueUserWorkItem(_ => Fire());
            return false;
        }

        if (settings.ShareInput && settings.EdgeSwitch && Environment.TickCount64 > cooldownUntil)
        {
            var mine = Screens.Monitors;
            foreach (var r in routes)
            {
                if (Screens.Crossing(mine, r.Mons, m.pt.X, m.pt.Y) is not (int X, int Y) p) continue;
                if (AnyMouseButtonDown()) return false;
                GoTo(r.Peer, (p.X - r.Dx, p.Y - r.Dy));
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Drive PC <paramref name="q"/> with our mouse and keyboard. entry = landing point in q's coordinates;
    /// null = middle of its main screen (hotkey). If we were driving another PC, it is released first.
    /// </summary>
    void GoTo(Peer q, (int X, int Y)? entry)
    {
        var old = target;
        if (old != null && old != q) old.Send(Wire.Build(Msg.Release));
        if (old == null)
        {
            Native.GetCursorPos(out var cur);
            // Park the (hidden) cursor mid-screen: at the edge, Windows would clip away any motion toward that edge.
            var mon = Screens.At(Screens.Monitors, cur.X, cur.Y) ?? Screens.Nearest(Screens.Monitors, cur.X, cur.Y);
            pin = pinHome = new Native.POINT { X = mon.Left + mon.Width / 2, Y = mon.Top + mon.Height / 2 };
            Native.SetCursorPos(pin.X, pin.Y);
            CursorHider.Set(true);
        }
        lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
        movesSent = 0;
        float myScale = (float)Screens.ScaleAt(pinHome.X, pinHome.Y);
        target = q;
        bool edgeReturn = settings.EdgeSwitch;
        q.Send(Wire.Build(Msg.Enter, w =>
        {
            w.Write(entry == null);
            w.Write(entry?.X ?? 0); w.Write(entry?.Y ?? 0);
            w.Write(edgeReturn);
            w.Write(myScale);
        }));
        ThreadPool.QueueUserWorkItem(_ => Fire());
    }

    bool hotkeyDigitDown;
    // Tracked here rather than via GetAsyncKeyState: while controlling another PC we swallow
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

    /// <summary>Ctrl+Alt+digit jumps to the PC with that number. Returns true if the key was consumed.</summary>
    bool HandleHotkey(uint vk, bool down)
    {
        int digit = vk is >= 0x31 and <= 0x39 ? (int)vk - 0x30 : vk is >= 0x61 and <= 0x69 ? (int)vk - 0x60 : 0;
        if (digit == 0 || !settings.HotkeySwitch || !settings.ShareInput) return false;
        if (!down) { if (!hotkeyDigitDown) return false; hotkeyDigitDown = false; return true; }
        if (!(ctrlL || ctrlR) || !(altL || altR)) return false;
        var order = numbered;
        if (digit > order.Length) return false;
        var id = order[digit - 1];
        if (id == MyId)
        {
            hotkeyDigitDown = true;
            if (target != null) ThreadPool.QueueUserWorkItem(_ => ComeHome(null));
            return true;
        }
        if (!peers.TryGetValue(id, out var q) || !q.Modern) return false; // that PC is offline: let the key through
        hotkeyDigitDown = true;
        if (target != q) GoTo(q, null);
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
        if (peers.IsEmpty) return false;
        if (controller == null && HandleHotkey(k.vkCode, down)) return true;
        var t = target;
        if (t == null) return false;

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
        t.Send(Wire.Build(Msg.Key, w => { w.Write(vk); w.Write(scan); w.Write(ext); w.Write(down); }));
        return true;
    }

    /// <summary>Stop driving other PCs and put the cursor back on this one. at = where it arrives (this PC's coordinates).</summary>
    void ComeHome((int X, int Y)? at)
    {
        var t = target;
        if (t == null) return;
        target = null;
        cooldownUntil = Environment.TickCount64 + 40;
        if (at is (int X, int Y) p)
        {
            var (x, y) = Screens.ClampOnto(Screens.Monitors, p.X, p.Y, 3);
            Native.SetCursorPos(x, y);
        }
        else
        {
            t.Send(Wire.Build(Msg.Release));
            Native.SetCursorPos(pin.X, pin.Y);
        }
        CursorHider.Set(false);
        lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
        Fire();
    }

    public void ReleaseControl() => ComeHome(null);

    /// <summary>
    /// The PC we were driving says the cursor left its screens at (lx,ly) in its coordinates. Work out whose screen that
    /// is in the shared arrangement and continue there: this PC, a third PC, or (if nobody is there) back where it was.
    /// </summary>
    void OnReturn(Peer from, int lx, int ly, int fromApplied)
    {
        if (target != from) return;
        (int X, int Y) fromPos, myPos;
        lock (worldGate)
        {
            if (!world.Pos.TryGetValue(from.Id, out fromPos) || !world.Pos.TryGetValue(MyId, out myPos)) { ComeHome(null); return; }
        }
        int wx = lx + fromPos.X, wy = ly + fromPos.Y, sent = movesSent;
        // Moves we sent after that PC decided to hand on never reached its cursor: apply them here.
        if (fromApplied >= 0 && sent - fromApplied is > 0 and <= 1024)
            for (int i = fromApplied; i < sent; i++) { wx += sentMoves[i & 1023].dx; wy += sentMoves[i & 1023].dy; }

        string? owner;
        lock (worldGate) owner = world.Owner(wx, wy, MonitorsOf);
        if (owner == MyId) { ComeHome((wx - myPos.X, wy - myPos.Y)); return; }
        if (owner != null && owner != from.Id && peers.TryGetValue(owner, out var q) && q.Modern)
        {
            (int X, int Y) qPos;
            lock (worldGate) qPos = world.Pos[owner];
            var entry = Screens.ClampOnto(q.Monitors.Length > 0 ? q.Monitors : World.Placeholder, wx - qPos.X, wy - qPos.Y, 2);
            GoTo(q, entry);
            return;
        }
        // Nobody reachable there: put it back on the PC it came from.
        var back = Screens.ClampOnto(from.Monitors.Length > 0 ? from.Monitors : World.Placeholder, lx, ly, 2);
        GoTo(from, back);
    }

    // ======================== remote messages (reader thread) ========================

    void OnMessage(Link l, byte[] msg)
    {
        var from = PeerOf(l);
        if (from == null) return;
        var type = (Msg)msg[0];
        using var r = Wire.Read(msg);
        switch (type)
        {
            case Msg.Hello:
                r.ReadByte(); // legacy side
                from.Name = r.ReadString();
                SetPeerMonitors(from, ReadMonitors(r));
                from.Modern = r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() >= Protocol;
                if (!from.Modern)
                    Warning = $"{from.Name} runs an older LAN Mouse. Install this version on every PC so they can work together.";
                FixLayout(announce: true);
                Fire();
                break;

            case Msg.Screens:
                SetPeerMonitors(from, ReadMonitors(r));
                FixLayout(announce: true);
                Fire();
                break;

            case Msg.Layout:
                if (from.Modern) AdoptLayout(from, r);
                break;

            case Msg.Enter:
            {
                if (!from.Modern) break;
                if (!settings.ShareInput)
                {
                    // Say so, instead of silently swallowing the cursor: the other PC brings it back and explains.
                    from.Send(Wire.Build(Msg.InputOff));
                    break;
                }
                // Someone else takes over: stop driving whatever we were driving, and tell a previous driver it lost us.
                if (target != null) ComeHome(null);
                var prev = controller;
                if (prev != null && prev != from) prev.Send(Wire.Build(Msg.Yield));
                nextDesktopCheck = 0;
                bool center = r.ReadBoolean();
                int ex = r.ReadInt32(), ey = r.ReadInt32();
                controllerAllowsEdge = r.ReadBoolean();
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
                controller = from;
                if (SecureDesktopBlocks(from)) break;
                InjMove(rx, ry);
                CursorHider.Set(false);
                Fire();
                break;
            }

            case Msg.Move:
            {
                if (controller != from || SecureDesktopBlocks(from)) break;
                double f = Screens.ScaleAt(rx, ry) / senderScale;
                double mx = r.ReadInt16() * f + carryX, my = r.ReadInt16() * f + carryY;
                int ix = (int)Math.Round(mx), iy = (int)Math.Round(my);
                carryX = mx - ix; carryY = my - iy;
                int nx = rx + ix, ny = ry + iy;
                movesApplied++;
                if (Screens.At(Screens.Monitors, nx, ny) == null)
                {
                    // Off our screens. If another PC's screen is there in the arrangement, hand the cursor on (the
                    // controlling PC decides where it goes). Not while input is blocked with no agent: the real cursor
                    // is frozen (e.g. on Task Manager) and our dead-reckoned position would march off for no reason.
                    if ((secureActive || !inputBlocked) && controllerAllowsEdge && SomeoneElseAt(nx, ny))
                    {
                        controller = null;
                        int applied = movesApplied;
                        from.Send(Wire.Build(Msg.Return, w => { w.Write(nx); w.Write(ny); w.Write(applied); }));
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
            }

            case Msg.Button:
                if (controller != from) break;
                nextDesktopCheck = 0; // a click is what usually opens the sign-in prompt: check right away next time
                int b = r.ReadByte(); bool bd = r.ReadBoolean();
                lock (injectedKeys) { if (bd) injectedButtons.Add(b); else injectedButtons.Remove(b); }
                InjButton(b, bd);
                break;

            case Msg.Wheel:
                if (controller != from) break;
                short delta = r.ReadInt16();
                InjWheel(delta, r.ReadBoolean());
                break;

            case Msg.Key:
            {
                if (controller != from || SecureDesktopBlocks(from)) break;
                ushort vk = r.ReadUInt16(), scan = r.ReadUInt16(); bool ext = r.ReadBoolean(), kd = r.ReadBoolean();
                bool injWin;
                lock (injectedKeys)
                {
                    if (kd) injectedKeys.Add((vk, scan, ext)); else injectedKeys.Remove((vk, scan, ext));
                    injWin = injectedKeys.Any(k => k.vk is 0x5B or 0x5C);
                }
                // Windows ignores a synthesized Win+L (only real keyboards may lock), so the L just got typed.
                // Do the lock ourselves and keep the L out.
                if (vk == 0x4C && (kd ? injWin : lockSwallowedL))
                {
                    lockSwallowedL = kd;
                    lock (injectedKeys) injectedKeys.Remove((vk, scan, ext));
                    if (kd)
                    {
                        // Let go of Win first, or it stays held through the lock screen.
                        lock (injectedKeys)
                            foreach (var k in injectedKeys.Where(k => k.vk is 0x5B or 0x5C).ToList())
                            {
                                InjKey(k.vk, k.scan, k.ext, false);
                                injectedKeys.Remove(k);
                            }
                        LockWorkStation();
                    }
                    break;
                }
                InjKey(vk, scan, ext, kd);
                break;
            }

            case Msg.Return:
            {
                int hx = r.ReadInt32(), hy = r.ReadInt32();
                int applied = r.BaseStream.Position < r.BaseStream.Length ? r.ReadInt32() : -1;
                OnReturn(from, hx, hy, applied);
                break;
            }

            case Msg.InputOff:
                if (target == from)
                {
                    target = null;
                    cooldownUntil = Environment.TickCount64 + 1500; // don't bounce straight back
                    lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
                    Native.SetCursorPos(pinHome.X, pinHome.Y);
                    CursorHider.Set(false);
                    Fire();
                }
                Notify?.Invoke($"{from.Name} has \"Mouse & keyboard\" turned off in its LAN Mouse settings, so it can't be controlled. Turn it on there.");
                break;

            case Msg.Locked:
                if (target == from)
                {
                    target = null;
                    cooldownUntil = Environment.TickCount64 + 1500; // don't slide straight back onto the locked screen
                    lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
                    Native.SetCursorPos(pinHome.X, pinHome.Y);
                    CursorHider.Set(false);
                    Fire();
                }
                Notify?.Invoke($"{from.Name} is showing its sign-in screen. Windows only lets you type the PIN with that PC's own keyboard.");
                break;

            case Msg.Yield:
                if (target == from)
                {
                    target = null;
                    lock (forwardedKeys) { forwardedKeys.Clear(); forwardedButtons.Clear(); }
                    Native.SetCursorPos(pinHome.X, pinHome.Y);
                    CursorHider.Set(false);
                    Fire();
                }
                break;

            case Msg.Release:
                if (controller != from) break;
                CursorHider.Set(true); // the driving PC took the cursor elsewhere
                controller = null;
                ReleaseInjected();
                Fire();
                break;

            default:
                break; // unknown (e.g. window-sharing messages from an older version): ignore

            case Msg.Offer:
            {
                int id = r.ReadInt32();
                var kinds = (ClipKinds)r.ReadByte() & Allowed();
                string summary = r.ReadString();
                if (kinds != ClipKinds.None) Clipboard?.ApplyOffer(new ClipOffer(from.Id, id, kinds, summary));
                break;
            }
        }
    }

    /// <summary>Is world point (our coordinates x,y) on another PC's screen in the arrangement?</summary>
    bool SomeoneElseAt(int x, int y)
    {
        lock (worldGate)
        {
            if (!world.Pos.TryGetValue(MyId, out var me)) return false;
            var owner = world.Owner(x + me.X, y + me.Y, MonitorsOf);
            return owner != null && owner != MyId;
        }
    }

    ClipKinds Allowed() =>
        (settings.ShareText ? ClipKinds.Text : 0) | (settings.ShareImages ? ClipKinds.Image : 0) | (settings.ShareFiles ? ClipKinds.Files : 0);

    // ======================== clipboard: offers (control links) ========================

    /// <summary>Tell every PC what we just copied. Only a description goes over the network now; data moves on paste.</summary>
    public void SendOffer(int id, ClipKinds kinds, string summary)
    {
        kinds &= Allowed();
        if (kinds == ClipKinds.None) return;
        var msg = Wire.Build(Msg.Offer, w => { w.Write(id); w.Write((byte)kinds); w.Write(summary); });
        foreach (var p in peers.Values) p.Send(msg);
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
        if (cts == null || peers.IsEmpty) { s.Dispose(); return; }
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
            Notify?.Invoke($"Couldn't send to {NameByAddress(l.Address)}: {e.Message}");
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
        if (!peers.TryGetValue(offer.PeerId, out var main) || cts == null) return null;
        Link? l = null;
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectTimeout.CancelAfter(4000);
                try { await s.ConnectAsync(main.Address, DataPort, connectTimeout.Token).ConfigureAwait(false); }
                catch { s.Dispose(); Notify?.Invoke($"Couldn't reach {main.Name} to paste. Click \"Allow through firewall\" on that PC."); return null; }
            }
            l = await Task.Run(() => Link.Handshake(s, settings.Id, master, initiator: true, dataLink: true), ct).ConfigureAwait(false);
            if (l == null) return null;
            Track(l);
            var download = new Download(this, offer.Summary, main.Name);
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

    string NameByAddress(string address) => peers.Values.FirstOrDefault(p => p.Address == address)?.Name ?? "the other PC";

    void ReportProgress(string? text, double frac) => Progress?.Invoke(text, frac);
    void ReportNotify(string text) => Notify?.Invoke(text);

    /// <summary>Receives one transfer (text, picture or files) on its own connection.</summary>
    sealed class Download
    {
        readonly Engine e;
        readonly string label, peerName;
        readonly TaskCompletionSource<object?> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object?> Result => done.Task;

        long total, got, lastReport;
        MemoryStream? image;
        FileEntry[] files = Array.Empty<FileEntry>();
        string folder = "";
        int index = -1;
        FileStream? current;
        long remaining;

        public Download(Engine e, string label, string peerName) { this.e = e; this.label = label; this.peerName = peerName; }

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
                        if (total > 20_000_000) e.ReportNotify($"Pasting {label} from {peerName}...");
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
