using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LanMouse;

/// <summary>
/// Hides the mouse pointer on this PC while it isn't the one being used (swaps every system cursor for a blank one),
/// and brings the user's own cursor scheme back afterwards. Work happens on a small background thread so the
/// input hook never waits on it.
/// </summary>
static class CursorHider
{
    // OCR_NORMAL, IBEAM, WAIT, CROSS, UP, SIZENWSE, SIZENESW, SIZEWE, SIZENS, SIZEALL, NO, HAND, APPSTARTING, HELP, PIN, PERSON
    static readonly uint[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672 };
    const uint SPI_SETCURSORS = 0x57;

    [DllImport("user32.dll")] static extern nint CreateCursor(nint inst, int x, int y, int w, int h, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")] static extern bool SetSystemCursor(nint cursor, uint id);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, nint pv, uint winIni);

    static readonly byte[] AndMask = Repeat(0xFF, 128), XorMask = new byte[128];
    static byte[] Repeat(byte b, int n) { var a = new byte[n]; Array.Fill(a, b); return a; }

    static volatile bool want, hidden;
    static readonly AutoResetEvent signal = new(false);
    static readonly object applyLock = new();

    static CursorHider()
    {
        new Thread(() =>
        {
            while (true)
            {
                signal.WaitOne();
                Apply();
            }
        }) { IsBackground = true, Name = "CursorHider" }.Start();
    }

    /// <summary>Ask for the pointer to be hidden or shown. Returns immediately.</summary>
    public static void Set(bool hide)
    {
        if (want == hide && hidden == hide) return;
        want = hide;
        signal.Set();
    }

    static void Apply()
    {
        lock (applyLock)
        {
            if (want == hidden) return;
            if (want)
            {
                // SetSystemCursor takes ownership of the handle, so each slot needs its own blank cursor.
                foreach (var id in Ids)
                {
                    var blank = CreateCursor(0, 0, 0, 32, 32, AndMask, XorMask);
                    if (blank != 0) SetSystemCursor(blank, id);
                }
                hidden = true;
            }
            else
            {
                SystemParametersInfo(SPI_SETCURSORS, 0, 0, 0); // reload the user's cursor scheme
                hidden = false;
            }
        }
    }

    /// <summary>Put the user's cursors back right now (startup, exit, crash). Safe to call any time.</summary>
    public static void RestoreNow()
    {
        want = false;
        lock (applyLock)
        {
            SystemParametersInfo(SPI_SETCURSORS, 0, 0, 0);
            hidden = false;
        }
    }
}
