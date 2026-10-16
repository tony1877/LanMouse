using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;

namespace LanMouse;

/// <summary>
/// Optional "sign-in support". Windows blocks ordinary programs from sending input to its secure sign-in screen
/// (PIN/password, UAC, Ctrl+Alt+Del). The only thing allowed to is code running as LocalSystem on that desktop.
///
/// So this is a small Windows service (LocalSystem) that keeps a worker process running on whichever desktop is
/// currently receiving input, including the secure one, and that worker injects the mouse/keyboard LAN Mouse sends
/// it over a local named pipe. It is installed and removed only when the user asks, and does nothing unless the
/// other PC is actively controlling this one while its sign-in screen is up.
/// </summary>
static class Agent
{
    public const string ServiceName = "LanMouseAgent";
    public const string PipeName = "LanMouseAgentInput";
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LanMouse");
    public static readonly string TokenFile = Path.Combine(DataDir, "agent.token");

    // pipe command bytes
    public const byte CmdMove = 1, CmdButton = 2, CmdWheel = 3, CmdKey = 4, CmdPing = 5;

    public static bool IsInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query " + ServiceName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Launch an elevated copy of ourselves to install (or remove) the service. One UAC prompt.</summary>
    public static bool RunElevated(string verb)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, verb) { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi);
            p!.WaitForExit(30000);
            return p.ExitCode == 0;
        }
        catch { return false; } // user declined the UAC prompt
    }

    // ------------------------ install / uninstall (run elevated) ------------------------

    public static int Install()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            // Shared secret so only a process that can read this file (processes in this user's session) may drive
            // the worker. ProgramData is readable by the service (SYSTEM) and this user by default.
            // Installing again (or over an older version) must replace the running service: otherwise its worker
            // keeps the old token and binary, and silently rejects every connection. Stopping it also ends its worker.
            Sc($"stop {ServiceName}");
            Sc($"delete {ServiceName}");
            for (int i = 0; i < 20 && IsInstalled(); i++) Thread.Sleep(250);
            File.WriteAllText(TokenFile, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

            Sc($"create {ServiceName} binPath= \"\\\"{Environment.ProcessPath}\\\" --agent-service\" start= auto DisplayName= \"LAN Mouse sign-in support\"");
            Sc($"description {ServiceName} \"Lets LAN Mouse control this PC's Windows sign-in screen from your other PC. Remove it in LAN Mouse settings.\"");
            Sc($"start {ServiceName}");
            Log($"installed, binPath={Environment.ProcessPath}");
            return 0;
        }
        catch { return 1; }
    }

    public static int Uninstall()
    {
        try
        {
            Sc($"stop {ServiceName}");
            Sc($"delete {ServiceName}");
            try { File.Delete(TokenFile); } catch { }
            return 0;
        }
        catch { return 1; }
    }

    static void Sc(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit(8000);
        }
        catch { }
    }

    // ------------------------ the service (LocalSystem, session 0) ------------------------

    static nint statusHandle;
    static ServiceCtrlHandler? ctrlHandlerRef;
    static ServiceMainProc? mainProcRef;
    static volatile bool serviceRunning;
    static Process? worker;
    static uint workerSession = 0xFFFFFFFF;

    public static readonly string LogFile = Path.Combine(DataDir, "agent.log");

    public static void Log(string m)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {m}{Environment.NewLine}");
        }
        catch { }
    }

    public static void RunService()
    {
        mainProcRef = ServiceMain;
        var table = new[]
        {
            new SERVICE_TABLE_ENTRY { name = ServiceName, proc = mainProcRef },
            new SERVICE_TABLE_ENTRY { name = null, proc = null },
        };
        StartServiceCtrlDispatcher(table); // blocks until the service stops
    }

    static void ServiceMain(int argc, nint argv)
    {
        ctrlHandlerRef = ControlHandler;
        statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, ctrlHandlerRef, 0);
        if (statusHandle == 0) return;
        Report(SERVICE_RUNNING);
        serviceRunning = true;
        Log("service running");
        try { Supervise(); } catch (Exception e) { Log("supervise error: " + e.Message); }
        StopWorker();
        Report(SERVICE_STOPPED);
        Log("service stopped");
    }

    static int ControlHandler(int control, int evType, nint evData, nint ctx)
    {
        if (control is SERVICE_CONTROL_STOP or SERVICE_CONTROL_SHUTDOWN)
        {
            serviceRunning = false;
            Report(SERVICE_STOP_PENDING);
        }
        return 0;
    }

    /// <summary>
    /// Keep one worker running in the physical console session. The worker itself follows the active input desktop
    /// (the service runs in session 0 and can't see the console's secure desktop, so it must not try).
    /// </summary>
    static void Supervise()
    {
        while (serviceRunning)
        {
            uint session = WTSGetActiveConsoleSessionId();
            bool dead = worker == null || worker.HasExited;
            if (session != 0xFFFFFFFF && (dead || session != workerSession))
            {
                StopWorker();
                try { worker = LaunchOnConsole("--agent-worker", session); workerSession = session; Log($"launched worker pid={worker.Id} session={session}"); }
                catch (Exception e) { worker = null; Log("launch failed: " + e.Message); }
            }
            Thread.Sleep(1000);
        }
    }

    static void StopWorker()
    {
        try { if (worker is { HasExited: false }) worker.Kill(); } catch { }
        worker = null; workerSession = 0xFFFFFFFF;
    }

    /// <summary>Start a copy of ourselves as LocalSystem in the given console session, on its interactive window station.</summary>
    static Process LaunchOnConsole(string args, uint session)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0002 /* TOKEN_DUPLICATE */ | 0x0008 /* TOKEN_QUERY */ | 0x0001 /* ASSIGN_PRIMARY */, out nint tok))
            throw new InvalidOperationException("OpenProcessToken " + Marshal.GetLastWin32Error());
        try
        {
            if (!DuplicateTokenEx(tok, 0x10000000 /* MAXIMUM_ALLOWED */, 0, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out nint dup))
                throw new InvalidOperationException("DuplicateTokenEx " + Marshal.GetLastWin32Error());
            try
            {
                var gch = GCHandle.Alloc(session, GCHandleType.Pinned);
                try { SetTokenInformation(dup, 12 /* TokenSessionId */, gch.AddrOfPinnedObject(), 4); }
                finally { gch.Free(); }

                // Launch on the default desktop; the worker re-attaches its own thread to whatever desktop has input.
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "WinSta0\\Default" };
                string cmd = $"\"{Environment.ProcessPath}\" {args}";
                if (!CreateProcessAsUser(dup, null, cmd, 0, 0, false, 0x08000000 /* CREATE_NO_WINDOW */, 0, null, ref si, out var pi))
                    throw new InvalidOperationException("CreateProcessAsUser " + Marshal.GetLastWin32Error());
                CloseHandle(pi.hThread);
                var p = Process.GetProcessById(pi.dwProcessId);
                CloseHandle(pi.hProcess);
                return p;
            }
            finally { CloseHandle(dup); }
        }
        finally { CloseHandle(tok); }
    }

    static void Report(int state)
    {
        var s = new SERVICE_STATUS
        {
            type = 0x10 /* WIN32_OWN_PROCESS */, state = state,
            accepted = state == SERVICE_RUNNING ? 0x1 /* STOP */ | 0x4 /* SHUTDOWN */ : 0,
            waitHint = state == SERVICE_STOP_PENDING ? 5000 : 0,
        };
        SetServiceStatus(statusHandle, ref s);
    }

    // ------------------------ the worker (LocalSystem, on the input desktop) ------------------------

    // One injector per desktop. A thread that has called SendInput is bound to its desktop and can't switch
    // (SetThreadDesktop then fails with ERROR_BUSY), so each desktop gets its own fresh thread that attaches
    // once, before it ever injects.
    static readonly Dictionary<string, DesktopInjector> injectors = new();

    public static void RunWorker()
    {
        Log("worker started");

        while (true)
        {
            try
            {
                using var pipe = CreatePipe();
                pipe.WaitForConnection();
                Log("worker: client connected");
                // Read the token per connection, so a reinstall (new token) never leaves us rejecting everyone.
                string token;
                try { token = File.ReadAllText(TokenFile).Trim(); }
                catch (Exception e) { Log("worker: cannot read token: " + e.Message); continue; }
                Serve(pipe, token);
                Log("worker: client gone");
            }
            catch (Exception e) { Log("worker pipe error: " + e.Message); Thread.Sleep(200); }
        }
    }

    /// <summary>Run an action on a thread attached to whatever desktop currently has input.</summary>
    static void OnInputDesktop(Action a)
    {
        nint h = OpenInputDesktop(0, false, 0x10000000 /* GENERIC_ALL */);
        if (h == 0) { Log("worker: OpenInputDesktop failed " + Marshal.GetLastWin32Error()); return; }
        var buf = new byte[256];
        string name = GetUserObjectInformation(h, 2 /* UOI_NAME */, buf, buf.Length, out int len) && len > 1
            ? System.Text.Encoding.Unicode.GetString(buf, 0, len - 2) : h.ToString();
        if (injectors.TryGetValue(name, out var inj)) CloseDesktop(h); // already have a thread on this desktop
        else { inj = new DesktopInjector(name, h); injectors[name] = inj; }
        inj.Post(a);
    }

    sealed class DesktopInjector
    {
        readonly BlockingCollection<Action> queue = new();
        readonly nint desktop;
        readonly string name;

        public DesktopInjector(string name, nint desktop)
        {
            this.name = name; this.desktop = desktop;
            new Thread(Run) { IsBackground = true, Name = "Inject:" + name }.Start();
        }

        public void Post(Action a) { try { queue.Add(a); } catch { } }

        void Run()
        {
            // Fresh thread: no windows or input yet, so attaching to the desktop succeeds here where it wouldn't later.
            if (!SetThreadDesktop(desktop)) { Log($"worker: SetThreadDesktop({name}) failed {Marshal.GetLastWin32Error()}"); return; }
            Log("worker: attached to desktop " + name);
            foreach (var a in queue.GetConsumingEnumerable()) { try { a(); } catch { } }
        }
    }

    static NamedPipeServerStream CreatePipe()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // The logged-in user drives it; they must also supply the token below.
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.None, 4096, 4096, sec);
    }

    static void Serve(NamedPipeServerStream pipe, string token)
    {
        var br = new BinaryReader(pipe);
        var given = br.ReadString();
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(given), System.Text.Encoding.ASCII.GetBytes(token)))
        {
            Log("worker: token mismatch, dropping client");
            return;
        }
        while (pipe.IsConnected)
        {
            // Route each event to a thread attached to the desktop that has input right now (it may switch mid-session).
            switch (br.ReadByte())
            {
                case CmdMove: { int x = br.ReadInt32(), y = br.ReadInt32(); OnInputDesktop(() => Inject.MoveTo(x, y)); break; }
                case CmdButton: { int b = br.ReadByte(); bool d = br.ReadBoolean(); OnInputDesktop(() => Inject.Button(b, d)); break; }
                case CmdWheel: { short dl = br.ReadInt16(); bool hz = br.ReadBoolean(); OnInputDesktop(() => Inject.Wheel(dl, hz)); break; }
                case CmdKey: { ushort vk = br.ReadUInt16(), sc = br.ReadUInt16(); bool e = br.ReadBoolean(), d = br.ReadBoolean(); OnInputDesktop(() => Inject.Key(vk, sc, e, d)); break; }
                case CmdPing: break;
            }
        }
    }

    // ------------------------ Win32 ------------------------

    const int SERVICE_RUNNING = 4, SERVICE_STOPPED = 1, SERVICE_STOP_PENDING = 3;
    const int SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_SHUTDOWN = 5;

    delegate void ServiceMainProc(int argc, nint argv);
    delegate int ServiceCtrlHandler(int control, int evType, nint evData, nint ctx);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_TABLE_ENTRY { [MarshalAs(UnmanagedType.LPWStr)] public string? name; public ServiceMainProc? proc; }

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS { public int type, state, accepted, win32Exit, specificExit, checkPoint, waitHint; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool StartServiceCtrlDispatcher(SERVICE_TABLE_ENTRY[] table);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint RegisterServiceCtrlHandlerEx(string name, ServiceCtrlHandler handler, nint ctx);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetServiceStatus(nint handle, ref SERVICE_STATUS status);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(nint process, int access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(nint token, int access, nint attrs, int impLevel, int type, out nint dup);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetTokenInformation(nint token, int cls, nint info, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(nint token, string? app, string cmd, nint procAttr, nint threadAttr, bool inherit, int flags, nint env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(nint h);
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(nint desk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(nint desk);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformation(nint obj, int index, byte[] info, int len, out int needed);
}
