using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace LanMouse;

/// <summary>
/// One authenticated, AES-GCM encrypted TCP connection to the peer.
/// Two send lanes: input events always jump ahead of bulk clipboard/file chunks.
/// </summary>
sealed class Link : IDisposable
{
    const int MaxFrame = 16 * 1024 * 1024;
    static readonly byte[] HelloMagic = "LMv3"u8.ToArray();

    readonly Socket sock;
    readonly NetworkStream stream;
    readonly AesGcm aes;
    readonly byte myLane, peerLane;
    ulong sendCounter, recvCounter;
    readonly Channel<byte[]> fast = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly Channel<byte[]> bulk = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true });
    int closed;

    public string PeerId { get; }
    public string Address { get; }
    /// <summary>Tie-break so both sides keep the same connection if two get opened at once.</summary>
    public bool InitiatedByLowerId { get; }
    public event Action<Link, byte[]>? Message;
    public event Action<Link>? Closed;
    public bool IsClosed => closed != 0;
    readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Completes once everything queued before <see cref="FinishSending"/> is on the wire.</summary>
    public Task Drained => drained.Task;

    Link(Socket sock, byte[] sessionKey, string peerId, bool iAmLower, bool initiator)
    {
        this.sock = sock;
        stream = new NetworkStream(sock, ownsSocket: true);
        aes = new AesGcm(sessionKey, 16);
        PeerId = peerId;
        Address = (sock.RemoteEndPoint as System.Net.IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";
        myLane = iAmLower ? (byte)0 : (byte)1;
        peerLane = (byte)(1 - myLane);
        InitiatedByLowerId = initiator == iAmLower;
    }

    /// <summary>Mutual challenge-response; neither side ever sends the key or anything derived from it alone.</summary>
    public static Link? Handshake(Socket sock, string myId, byte[] master, bool initiator, bool dataLink = false)
    {
        try
        {
            sock.NoDelay = true;
            sock.ReceiveTimeout = sock.SendTimeout = 5000;
            byte[] me = Convert.FromHexString(myId), myNonce = RandomNumberGenerator.GetBytes(32);
            var hello = new byte[4 + 16 + 32];
            HelloMagic.CopyTo(hello, 0); me.CopyTo(hello, 4); myNonce.CopyTo(hello, 20);
            sock.Send(hello);

            var theirs = ReadExact(sock, hello.Length);
            if (!theirs.AsSpan(0, 4).SequenceEqual(HelloMagic)) throw new InvalidDataException("Not a LAN Mouse v3 peer");
            var peer = theirs.AsSpan(4, 16).ToArray();
            var peerNonce = theirs.AsSpan(20, 32).ToArray();
            int cmp = me.AsSpan().SequenceCompareTo(peer);
            if (cmp == 0) throw new InvalidDataException("Connected to myself");
            bool iAmLower = cmp < 0;

            var salt = iAmLower ? Concat(myNonce, peerNonce) : Concat(peerNonce, myNonce);
            var session = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, "lanmouse session"u8.ToArray());
            var auth = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, "lanmouse auth"u8.ToArray());

            sock.Send(HMACSHA256.HashData(auth, Concat("proof"u8.ToArray(), me)));
            var proof = ReadExact(sock, 32);
            if (!CryptographicOperations.FixedTimeEquals(proof, HMACSHA256.HashData(auth, Concat("proof"u8.ToArray(), peer))))
                throw new CryptographicException("Pairing key mismatch");

            sock.SendTimeout = dataLink ? 0 : 5000;
            // Control link: peer pings every 2 s, so silence means it's gone.
            // Transfer links can sit quiet while the far side reads a disk; the control link's liveness covers them.
            sock.ReceiveTimeout = dataLink ? 0 : 10000;
            return new Link(sock, session, Convert.ToHexString(peer).ToLowerInvariant(), iAmLower, initiator);
        }
        catch
        {
            sock.Dispose();
            return null;
        }
    }

    static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }

    static byte[] ReadExact(Socket s, int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = s.Receive(buf, got, n - got, SocketFlags.None);
            if (r == 0) throw new EndOfStreamException();
            got += r;
        }
        return buf;
    }

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = "LinkRead", Priority = ThreadPriority.AboveNormal }.Start();
        new Thread(SendLoop) { IsBackground = true, Name = "LinkSend", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void Send(byte[] msg) => fast.Writer.TryWrite(msg);

    public ValueTask SendBulkAsync(byte[] msg, CancellationToken ct) => bulk.Writer.WriteAsync(msg, ct);

    void SendLoop()
    {
        try
        {
            while (true)
            {
                // Send every queued input event in one write (a 1000 Hz mouse makes many tiny messages);
                // bulk data goes one chunk at a time so input can always cut in.
                bool any = false;
                while (outBuf.Length < 64 * 1024 && fast.Reader.TryRead(out var m)) { AppendFrame(m); any = true; }
                if (!any && bulk.Reader.TryRead(out var b)) { AppendFrame(b); any = true; }
                if (any)
                {
                    stream.Write(outBuf.GetBuffer(), 0, (int)outBuf.Length);
                    outBuf.SetLength(0);
                    continue;
                }
                var waitFast = fast.Reader.WaitToReadAsync().AsTask();
                var waitBulk = bulk.Reader.WaitToReadAsync().AsTask();
                Task.WaitAny(waitFast, waitBulk);
                if (waitFast.IsCompleted && !waitFast.Result && waitBulk.IsCompleted && !waitBulk.Result)
                {
                    stream.Flush();
                    drained.TrySetResult();
                    return;
                }
                if (IsClosed) return;
            }
        }
        catch { Dispose(); }
    }

    readonly MemoryStream outBuf = new();

    void AppendFrame(byte[] plain)
    {
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        nonce[0] = myLane;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], sendCounter++);
        var frame = new byte[4 + plain.Length + 16];
        BinaryPrimitives.WriteInt32LittleEndian(frame, plain.Length + 16);
        aes.Encrypt(nonce, plain, frame.AsSpan(4, plain.Length), frame.AsSpan(4 + plain.Length, 16));
        outBuf.Write(frame);
    }

    void ReadLoop()
    {
        try
        {
            Span<byte> nonce = stackalloc byte[12];
            var header = new byte[4];
            while (true)
            {
                stream.ReadExactly(header);
                int len = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (len < 17 || len > MaxFrame) throw new InvalidDataException("Bad frame");
                var frame = new byte[len];
                stream.ReadExactly(frame);
                nonce.Clear();
                nonce[0] = peerLane;
                BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], recvCounter++);
                var plain = new byte[len - 16];
                aes.Decrypt(nonce, frame.AsSpan(0, len - 16), frame.AsSpan(len - 16), plain);
                Message?.Invoke(this, plain);
            }
        }
        catch { Dispose(); }
    }

    /// <summary>No more messages will be queued; the send thread drains what's left and stops.</summary>
    public void FinishSending()
    {
        fast.Writer.TryComplete();
        bulk.Writer.TryComplete();
    }

    public void Dispose()
    {
        drained.TrySetResult();
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        fast.Writer.TryComplete();
        bulk.Writer.TryComplete();
        try { sock.Shutdown(SocketShutdown.Both); } catch { }
        stream.Dispose();
        Closed?.Invoke(this);
    }
}

enum Msg : byte
{
    Hello = 1, Enter, Move, Button, Wheel, Key, Return, Release, Yield, Text, Ping, Screens, Layout, Offer, Pull, Gone,
    FilesStart = 20, ImageStart, Chunk, End,
    Locked = 40,
    // 30-38 were window sharing (removed); old peers may still send them and they are ignored.
}

/// <summary>Tiny message builder/reader for the wire format.</summary>
static class Wire
{
    public static byte[] Build(Msg type, Action<BinaryWriter>? body = null)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)type);
            body?.Invoke(w);
        }
        return ms.ToArray();
    }

    public static BinaryReader Read(byte[] msg) => new(new MemoryStream(msg, 1, msg.Length - 1), Encoding.UTF8);
}
