using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace LanMouse;

/// <summary>
/// Global low-level mouse + keyboard hooks on a dedicated thread, so a busy UI never stalls the cursor.
/// Callbacks return true to swallow the event locally. The same thread also reports window move/size
/// loops (for dragging windows to the other PC) and can run queued work in order with the hook callbacks.
/// </summary>
sealed class InputHook : IDisposable
{
    public delegate bool MouseHandler(int msg, in Native.MSLLHOOKSTRUCT data);
    public delegate bool KeyHandler(int msg, in Native.KBDLLHOOKSTRUCT data);

    const uint WM_APP_RUN = 0x8001;
    const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;

    delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);

    readonly MouseHandler onMouse;
    readonly KeyHandler onKey;
    readonly Action<nint, bool>? onMoveSize;
    readonly Native.HookProc mouseProc, keyProc; // kept alive for the GC
    readonly WinEventProc winEventProc;
    readonly Thread thread;
    readonly ConcurrentQueue<Action> work = new();
    uint threadId;
    nint mouseHook, keyHook, winEventHook;

    public InputHook(MouseHandler onMouse, KeyHandler onKey, Action<nint, bool>? onMoveSize = null)
    {
        this.onMouse = onMouse;
        this.onKey = onKey;
        this.onMoveSize = onMoveSize;
        mouseProc = MouseProc;
        keyProc = KeyProc;
        winEventProc = WinEvent;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "InputHook", Priority = ThreadPriority.Highest };
        thread.Start();
        ready.Wait();
    }

    void Run(ManualResetEventSlim ready)
    {
        threadId = Native.GetCurrentThreadId();
        var mod = Native.GetModuleHandle(null);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
        keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
        winEventHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, 0, winEventProc, 0, 0, 0);
        ready.Set();
        while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            if (msg.message == WM_APP_RUN)
                while (work.TryDequeue(out var a)) { try { a(); } catch { } }
        }
        Native.UnhookWindowsHookEx(mouseHook);
        Native.UnhookWindowsHookEx(keyHook);
        if (winEventHook != 0) UnhookWinEvent(winEventHook);
    }

    /// <summary>Run on the hook thread, in order with the hook callbacks (so cursor bookkeeping never races).</summary>
    public void Post(Action a)
    {
        work.Enqueue(a);
        Native.PostThreadMessage(threadId, WM_APP_RUN, 0, 0);
    }

    void WinEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == 0) return; // OBJID_WINDOW only
        try { onMoveSize?.Invoke(hwnd, ev == EVENT_SYSTEM_MOVESIZESTART); } catch { }
    }

    nint MouseProc(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(l);
                if (onMouse((int)w, data)) return 1;
            }
            catch { /* never let an exception kill the hook */ }
        }
        return Native.CallNextHookEx(mouseHook, code, w, l);
    }

    nint KeyProc(int code, nint w, nint l)
    {
        if (code >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(l);
                if (onKey((int)w, data)) return 1;
            }
            catch { }
        }
        return Native.CallNextHookEx(keyHook, code, w, l);
    }

    public void Dispose()
    {
        if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, 0, 0);
        thread.Join(1000);
    }
}
