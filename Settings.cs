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
    /// <summary>
    /// The arrangement as it was just before a stand-in screen (ScreenLink's virtual display) appeared on this PC;
    /// put back when it goes away. Null when no stand-in is shown.
    /// </summary>
    public Dictionary<string, int[]>? WorldBeforeStandIn { get; set; }
    /// <summary>Every PC seen so far, so they can be drawn (and numbered) while offline.</summary>
    public Dictionary<string, KnownPc> KnownPcs { get; set; } = new();

    /// <summary>The key is encrypted for this machine rather than this user (the agent's copy, read as LocalSystem).</summary>
    public bool MachineKey { get; set; }

    DataProtectionScope KeyScope => MachineKey ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;

    /// <summary>Pairing key, encrypted at rest with Windows DPAPI (only your user account can read it).</summary>
    [JsonIgnore]
    public string Key
    {
        get
        {
            if (string.IsNullOrEmpty(KeyProtected)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(KeyProtected), null, KeyScope)); }
            catch { return ""; }
        }
        set => KeyProtected = string.IsNullOrEmpty(value) ? "" :
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, KeyScope));
    }

    /// <summary>
    /// With sign-in support on, the agent keeps this PC controllable before anyone signs in, using a copy of these
    /// settings in ProgramData (key encrypted for the machine, file readable only by SYSTEM, admins and this user).
    /// Off inside the agent itself, so it never rewrites the copy.
    /// </summary>
    public static bool WriteAgentCopy = true;
    public static readonly string AgentCopyPath = Path.Combine(Agent.DataDir, "agent-settings.json");

    public static Settings Load() => LoadFrom(FilePath) ?? new Settings();

    public static Settings? LoadFrom(string path)
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        if (WriteAgentCopy && SignInSupport) SaveAgentCopy();
    }

    void SaveAgentCopy()
    {
        try
        {
            var key = Key;
            var copy = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
            copy.MachineKey = true;
            copy.Key = key;
            if (!File.Exists(AgentCopyPath)) File.WriteAllText(AgentCopyPath, "");
            var acl = new System.Security.AccessControl.FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[]
            {
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            })
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                    System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(AgentCopyPath).SetAccessControl(acl);
            File.WriteAllText(AgentCopyPath, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { SecureInput.ClientLog("cannot write the agent's settings copy: " + e.Message); }
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
