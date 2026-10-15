using System;
using System.Collections.Generic;
using System.Linq;

namespace LanMouse;

/// <summary>
/// The shared arrangement of every PC. Each PC's desktop (its monitors in its own screen coordinates) is placed at an
/// offset in one common "world" space, exactly like monitors in Windows display settings. Every PC holds the same
/// map; the most recent edit wins and is passed to everyone.
/// Pure data + rules (no networking), so it is easy to reason about.
/// </summary>
sealed class World
{
    public readonly Dictionary<string, (int X, int Y)> Pos = new();
    public long Stamp;

    public static readonly Screens.Bounds[] Placeholder = { new(0, 0, 1920, 1080) };

    public World Clone()
    {
        var w = new World { Stamp = Stamp };
        foreach (var kv in Pos) w.Pos[kv.Key] = kv.Value;
        return w;
    }

    public Screens.Bounds[] Placed(string id, Func<string, Screens.Bounds[]> monitorsOf) =>
        Pos.TryGetValue(id, out var p) ? Screens.Shift(monitorsOf(id), p.X, p.Y) : Array.Empty<Screens.Bounds>();

    Screens.Bounds[] Others(string id, Func<string, Screens.Bounds[]> monitorsOf) =>
        Pos.Keys.Where(k => k != id).SelectMany(k => Placed(k, monitorsOf)).ToArray();

    /// <summary>Closest spot to <paramref name="want"/> where PC <paramref name="id"/> sits flush against the others without overlapping.</summary>
    public (int X, int Y) SnapPosition(string id, int wantX, int wantY, Func<string, Screens.Bounds[]> monitorsOf)
    {
        var others = Others(id, monitorsOf);
        return others.Length == 0 ? (wantX, wantY) : Screens.Snap(others, monitorsOf(id), wantX, wantY);
    }

    /// <summary>
    /// Give every PC in <paramref name="ids"/> a place, and move any PC that overlaps another (monitor setup changed)
    /// to the nearest free spot. Deterministic (sorted ids), so PCs that run it on the same map agree.
    /// Returns true if anything changed.
    /// </summary>
    public bool Fix(IEnumerable<string> ids, Func<string, Screens.Bounds[]> monitorsOf)
    {
        bool changed = false;
        foreach (var id in ids.Distinct().OrderBy(i => i, StringComparer.Ordinal))
        {
            if (Pos.ContainsKey(id)) continue;
            var placed = Pos.Keys.SelectMany(k => Placed(k, monitorsOf)).ToArray();
            if (placed.Length == 0) { Pos[id] = (0, 0); changed = true; continue; }
            // New PC: to the right of everything, tops aligned.
            var box = Screens.Box(placed);
            var mb = Screens.Box(monitorsOf(id));
            Pos[id] = (box.Right - mb.Left, box.Top - mb.Top);
            Pos[id] = SnapPosition(id, Pos[id].X, Pos[id].Y, monitorsOf);
            changed = true;
        }
        var order = Pos.Keys.OrderBy(i => i, StringComparer.Ordinal).ToList();
        for (int i = 1; i < order.Count; i++)
        {
            var id = order[i];
            var earlier = order.Take(i).SelectMany(k => Placed(k, monitorsOf)).ToArray();
            if (!Screens.Overlaps(Placed(id, monitorsOf), earlier)) continue;
            var p = Pos[id];
            Pos[id] = SnapPosition(id, p.X, p.Y, monitorsOf);
            changed = true;
        }
        return changed;
    }

    /// <summary>PC numbers for the hotkeys: left to right, then top to bottom, by the middle of each PC's desktop.</summary>
    public List<string> Numbered(Func<string, Screens.Bounds[]> monitorsOf) =>
        Pos.Keys
            .Select(id => (id, box: Screens.Box(Placed(id, monitorsOf))))
            .OrderBy(t => t.box.Left + t.box.Width / 2)
            .ThenBy(t => t.box.Top + t.box.Height / 2)
            .ThenBy(t => t.id, StringComparer.Ordinal)
            .Select(t => t.id)
            .ToList();

    /// <summary>Which PC's screens contain world point (x,y), if any.</summary>
    public string? Owner(int x, int y, Func<string, Screens.Bounds[]> monitorsOf) =>
        Pos.Keys.FirstOrDefault(id => Screens.At(Placed(id, monitorsOf), x, y) != null);
}
