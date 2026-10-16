using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace LanMouse;

/// <summary>
/// App-side client for the optional sign-in-support agent. When this PC is being controlled and its secure sign-in
/// screen is up, the normal SendInput can't reach it; these calls hand the same input to the agent (running as
/// LocalSystem on that desktop) over a local named pipe. Does nothing if the agent isn't installed.
/// </summary>
sealed class SecureInput : IDisposable
{
    NamedPipeClientStream? pipe;
    BinaryWriter? writer;
    string token = "";
    long nextTry;

    public bool Connected => pipe is { IsConnected: true };

    /// <summary>Connect to the agent if possible. Safe to call often; it backs off between attempts.</summary>
    public bool TryConnect() => EnsureConnected();

    bool EnsureConnected()
    {
        if (Connected) return true;
        if (Environment.TickCount64 < nextTry) return false;
        nextTry = Environment.TickCount64 + 500;
        Close();
        try
        {
            token = File.ReadAllText(Agent.TokenFile).Trim();
            var p = new NamedPipeClientStream(".", Agent.PipeName, PipeDirection.Out);
            p.Connect(300);
            var w = new BinaryWriter(p);
            w.Write(token); // handshake: the agent drops us if this doesn't match
            w.Flush();
            pipe = p; writer = w;
            ClientLog("connected to agent");
            return true;
        }
        catch (Exception e) { ClientLog("connect failed: " + e.Message); Close(); return false; }
    }

    void Send(Action<BinaryWriter> body)
    {
        if (!EnsureConnected()) return;
        try { body(writer!); writer!.Flush(); }
        catch { Close(); }
    }

    public void Move(int x, int y) => Send(w => { w.Write(Agent.CmdMove); w.Write(x); w.Write(y); });
    public void Button(int button, bool down) => Send(w => { w.Write(Agent.CmdButton); w.Write((byte)button); w.Write(down); });
    public void Wheel(short delta, bool horizontal) => Send(w => { w.Write(Agent.CmdWheel); w.Write(delta); w.Write(horizontal); });
    public void Key(ushort vk, ushort scan, bool ext, bool down) => Send(w => { w.Write(Agent.CmdKey); w.Write(vk); w.Write(scan); w.Write(ext); w.Write(down); });

    internal static void ClientLog(string m)
    {
        try { File.AppendAllText(Path.Combine(Settings.Dir, "secure-client.log"), $"{DateTime.Now:HH:mm:ss.fff} {m}{Environment.NewLine}"); }
        catch { }
    }

    void Close()
    {
        try { writer?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        writer = null; pipe = null;
    }

    public void Dispose() => Close();
}
