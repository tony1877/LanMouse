using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace LanMouse;

public enum Side { Left, Right, Top, Bottom }

public sealed class KnownPc
{
    public string Name { get; set; } = "";
    public int[][] Monitors { get; set; } = Array.Empty<int[]>();
}

public sealed class Settings
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanMouse");
    static readonly string FilePath = Path.Combine(Dir, "settings.v3.json");

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string KeyProtected { get; set; } = "";
    public Side Side { get; set; } = Side.Right;
    public string PeerAddress { get; set; } = "";
    public bool ShareInput { get; set; } = true;
    public bool ShareText { get; set; } = true;
    public bool ShareImages { get; set; } = true;
    public bool ShareFiles { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public bool EdgeSwitch { get; set; } = true;
    public bool HotkeySwitch { get; set; } = true;
    public bool SignInSupport { get; set; }  // use the privileged agent to reach the sign-in screen (opt-in)

    // -- arrangement: where the other PC's desktop sits, in this PC's screen coordinates --
    public bool HasLayout { get; set; }
    public int PeerOffsetX { get; set; }
    public int PeerOffsetY { get; set; }
    /// <summary>When the arrangement was last changed (UTC ticks); the newer one wins when both PCs meet.</summary>
    public long LayoutStamp { get; set; }
    /// <summary>The other PC's monitors as last seen, so the arrangement can be shown while disconnected.</summary>
    public int[][] PeerMonitorCache { get; set; } = Array.Empty<int[]>();

    // -- arrangement of any number of PCs (v1.3+): each PC's desktop origin in a shared world space --
    public Dictionary<string, int[]> WorldPositions { get; set; } = new();
    /// <summary>When the arrangement was last changed (UTC ticks); the newest wins across all PCs.</summary>
    public long WorldStamp { get; set; }
    /// <summary>Every PC seen so far, so they can be drawn (and numbered) while offline.</summary>
    public Dictionary<string, KnownPc> KnownPcs { get; set; } = new();

    /// <summary>Pairing key, encrypted at rest with Windows DPAPI (only your user account can read it).</summary>
    [JsonIgnore]
    public string Key
    {
        get
        {
            if (string.IsNullOrEmpty(KeyProtected)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(KeyProtected), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        set => KeyProtected = string.IsNullOrEmpty(value) ? "" :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("LanMouse") is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("LanMouse", $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue("LanMouse", false);
        }
    }

    public static Side Opposite(Side s) => s switch
    {
        Side.Left => Side.Right,
        Side.Right => Side.Left,
        Side.Top => Side.Bottom,
        _ => Side.Top,
    };
}
