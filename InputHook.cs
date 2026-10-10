using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LanMouse;

/// <summary>
/// Global low-level mouse + keyboard hooks on a dedicated thread, so a busy UI never stalls the cursor.
/// Callbacks return true to swallow the event locally.
/// </summary>
sealed class InputHook : IDisposable
{
    public delegate bool MouseHandler(int msg, in Native.MSLLHOOKSTRUCT data);
    public delegate bool KeyHandler(int msg, in Native.KBDLLHOOKSTRUCT data);

    readonly MouseHandler onMouse;
    readonly KeyHandler onKey;
    readonly Native.HookProc mouseProc, keyProc; // kept alive for the GC
    readonly Thread thread;
    uint threadId;
    nint mouseHook, keyHook;

    public InputHook(MouseHandler onMouse, KeyHandler onKey)
    {
        this.onMouse = onMouse;
        this.onKey = onKey;
        mouseProc = MouseProc;
        keyProc = KeyProc;
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
        ready.Set();
        while (Native.GetMessage(out _, 0, 0, 0) > 0) { }
        Native.UnhookWindowsHookEx(mouseHook);
        Native.UnhookWindowsHookEx(keyHook);
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
