using System.Collections.Generic;

namespace MiningDrone;

/// <summary>Check state of a fleet-tree node (root or profile folder) over all its ships, collapsed ones included.</summary>
internal enum CheckState
{
    None,
    Partial,
    All,
}

/// <summary>
/// The window's target set: checked ship IDs, independent of the game selection (imported once when the window
/// opens, or on demand). Pure so tools/jitcheck can test the tri-state rules.
/// </summary>
internal static class FleetChecks
{
    public static int Count(List<string> ids, HashSet<string> chk)
    {
        int n = 0;
        for (int i = 0; i < ids.Count; i++)
        {
            n += chk.Contains(ids[i]) ? 1 : 0;
        }
        return n;
    }

    public static CheckState State(List<string> ids, HashSet<string> chk)
    {
        int n = Count(ids, chk);
        return n == 0 ? CheckState.None : n == ids.Count ? CheckState.All : CheckState.Partial;
    }

    /// <summary>Checks or clears every ship of a node; a click on a partial node checks all of it.</summary>
    public static void Set(List<string> ids, HashSet<string> chk, bool on)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (on)
            {
                chk.Add(ids[i]);
            }
            else
            {
                chk.Remove(ids[i]);
            }
        }
    }

    /// <summary>Offline invariants, run by tools/jitcheck.</summary>
    internal static string? SelfCheck()
    {
        var a = new List<string> { "a1", "a2" };
        var b = new List<string> { "b1", "b2", "b3" };
        var all = new List<string> { "a1", "a2", "b1", "b2", "b3" };
        var chk = new HashSet<string>();
        if (State(all, chk) != CheckState.None || State(new List<string>(), chk) != CheckState.None)
        {
            return "empty tree not unchecked";
        }
        Set(a, chk, true);
        if (State(a, chk) != CheckState.All || State(b, chk) != CheckState.None || State(all, chk) != CheckState.Partial)
        {
            return "whole profile check wrong";
        }
        chk.Add("b2");
        if (State(b, chk) != CheckState.Partial || Count(all, chk) != 3)
        {
            return "profile + single ship wrong";
        }
        Set(b, chk, true);
        if (State(b, chk) != CheckState.All || State(all, chk) != CheckState.All)
        {
            return "partial click does not check all";
        }
        // Reassigning a ship to another profile moves it between folders; its check is by ID and survives.
        b.Remove("b2");
        a.Add("b2");
        if (State(a, chk) != CheckState.All || State(b, chk) != CheckState.All)
        {
            return "regroup lost a check";
        }
        all.Add("c1");
        if (State(all, chk) != CheckState.Partial)
        {
            return "new ship appeared checked";
        }
        Set(all, chk, false);
        return chk.Count == 0 ? null : "root uncheck left ships";
    }
}
