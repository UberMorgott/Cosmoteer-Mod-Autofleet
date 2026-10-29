using System;
using System.Collections.Generic;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>The profile whose cargo policy the cargo editor edits (live for all its ships). The collect grid is also
/// drawn per ship over the vanilla ban sets (MinerWindow.ShipScope overrides Count/Collects).</summary>
internal class CargoScope
{
    public Profile Profile = null!;

    // shortcut: indexed Read/Write are left from the removed multi-ship scope (always one policy); fold into Profile if the editor is reworked.
    public virtual int Count => 1;

    /// <summary>Unit <paramref name="s"/> collects <paramref name="rid"/> (profile template: null Collect = all).</summary>
    public virtual bool Collects(int s, string rid) => Profile.Cargo.Collect is not { } c || c.Contains(rid);

    public CargoPolicy Read(int i) => Profile.Cargo;

    public CargoPolicy Write(int i)
    {
        Store.MarkLibrary();
        return Profile.Cargo;
    }
}

/// <summary>
/// The editing rules of <see cref="MinerWindow"/> that need no game state (slider snapping, cargo policy edits), kept
/// free of game types so the test project compiles this file on its own.
/// </summary>
internal sealed partial class MinerWindow
{
    /// <summary>Halfling ScrollBar.Value snapping (ScrollBar.cs Value setter), so a slider round-trips without a change.</summary>
    private static float Snap(float v, double step) => step > 0 ? MathF.Round(v / (float)step) * (float)step : v;

    internal static string? SliderSelfCheck()
    {
        // Snap must be stable (a snapped value re-snaps to itself) for every stepped slider value we draw; a real ScrollBar
        // can't be built offline (Widget type initializer needs the game), so Snap mirrors its setter by hand.
        foreach (ModuleDef d in Registry.All)
        {
            foreach (ParamDef p in d.Params)
            {
                if (p.Kind != ParamKind.Number || p.Step <= 0)
                {
                    continue;
                }
                for (double x = p.Min; x <= p.Max; x += p.Step)
                {
                    float v = (float)p.Clamp(Math.Round(x / p.Step) * p.Step), s = Snap(v, p.Step);
                    if (Snap(s, p.Step) != s)
                    {
                        return $"slider snap unstable {d.TypeId}.{p.Key} {v:R} -> {s:R}";
                    }
                }
            }
        }
        return Snap(0.65f, 0.05) != 0.65f ? null : "expected 0.65 @ 0.05 to snap off its float (regression case)";
    }

    /// <summary>Moves <paramref name="rid"/> to displayed row <paramref name="to"/> (↑/↓ = its row ∓1, a drop = the
    /// insert index). Missing displayed rows are appended in displayed order first (materializes the shown order); the
    /// displayed rows are rewritten in their own slots, so hidden entries (unknown / unselected) keep their places.</summary>
    internal static List<string> MovePriority(List<string>? list, List<string> shown, string rid, int to)
    {
        list ??= new List<string>();
        foreach (string s in shown)
        {
            if (!list.Contains(s))
            {
                list.Add(s);
            }
        }
        int k = shown.IndexOf(rid);
        if (k < 0 || to < 0 || to >= shown.Count || to == k)
        {
            return list;
        }
        var order = new List<string>(shown);
        order.RemoveAt(k);
        order.Insert(to, rid);
        for (int i = 0, j = 0; i < list.Count && j < order.Count; i++)
        {
            if (shown.Contains(list[i]))
            {
                list[i] = order[j++];
            }
        }
        return list;
    }

    private static void SetKeepMin(CargoPolicy cp, string rid, int units) => (cp.KeepMin ??= new Dictionary<string, int>())[rid] = units;

    /// <summary>"Всё" for every policy in the scope; touches KeepAll only.</summary>
    private static void SetKeepAll(CargoScope sc, string rid, bool on)
    {
        for (int s = 0; s < sc.Count; s++)
        {
            List<string> list = sc.Write(s).KeepAll ??= new List<string>();
            list.Remove(rid);
            if (on)
            {
                list.Add(rid);
            }
        }
    }

    /// <summary>Offline invariants, run by tools/jitcheck: keep edits never touch Collect. Returns null on pass, else the failure.</summary>
    internal static string? SelfCheck()
    {
        if (SliderSelfCheck() is string slider) return slider;
        var sc = new CargoScope();
        var p = new Profile();
        p.Cargo.Collect = new List<string> { "iron" };
        sc.Profile = p;
        SetKeepAll(sc, "iron", true);
        SetKeepAll(sc, "iron", false);
        SetKeepMin(sc.Write(0), "iron", 0);
        if (p.Cargo.Collect is not { Count: 1 } pc || pc[0] != "iron")
        {
            return "profile: keep edits changed Collect";
        }
        // Collect priority: first move materializes the shown order; hidden entries stay; Collect untouched; Clone copies.
        var prio = MovePriority(new List<string> { "x.unknown", "gold" }, new List<string> { "gold", "iron", "hyperium" }, "hyperium", 1);
        if (string.Join(",", prio) != "x.unknown,gold,hyperium,iron") return "priority move wrong: " + string.Join(",", prio);
        p.Cargo.CollectPriority = MovePriority(null, new List<string> { "iron" }, "iron", 0);
        if (p.Cargo.Collect is not { Count: 1 } || p.Cargo.Clone().CollectPriority is not { Count: 1 }) return "priority edit touched Collect or Clone lost it";
        var drop = MovePriority(new List<string> { "a", "x.hidden", "b", "c" }, new List<string> { "a", "b", "c" }, "a", 2);
        if (string.Join(",", drop) != "b,x.hidden,c,a") return "priority drop wrong: " + string.Join(",", drop);
        return null;
    }
}
