using System;
using System.Collections.Generic;
using Cosmoteer.Data;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Halfling;

namespace MiningDrone.Autofleet;

/// <summary>
/// Shared resource accounting for transfers (Offload, Supply). No ledger for resources: MPMoveResourcesInput creates
/// an unconfirmed local job on enqueue (ExecuteLocalImmediate, MPMoveResourcesInput.cs:95-100) and a rejected input
/// resets it (Execute :110-113), so the Displayed anticipated pickups/deliveries already hold every transfer of this
/// client, in flight or open, whichever ship owns the job (ship-wide aggregates, ResourceManager.cs:2955-2990).
/// </summary>
internal static partial class Logistics
{
    /// <summary>Units the profile wants aboard (keep-min), 0 when unmanaged.</summary>
    private static int KeepMin(CargoPolicy? cargo, ID<ResourceRules> t) =>
        cargo?.KeepMin != null && cargo.KeepMin.TryGetValue(t.ToString(), out int u) ? Math.Max(0, u) : 0;

    /// <summary>Units that must stay aboard a donor: keep-all = everything, else the larger of the vanilla desire and the
    /// effective keep-min (the profile value counts at once, before SyncKeep pushes it as desire).</summary>
    public static int Floor(Ship ship, ID<ResourceRules> t, CargoPolicy? cargo) =>
        cargo?.KeepAll?.Contains(t.ToString()) == true ? int.MaxValue : Math.Max(World.Keep(ship, t), KeepMin(cargo, t));

    /// <summary>Units a receiver should hold: the larger of its vanilla desire and its effective keep-min.</summary>
    public static int Target(Ship ship, ID<ResourceRules> t, CargoPolicy? cargo) => Math.Max(World.Keep(ship, t), KeepMin(cargo, t));

    /// <summary>Donor surplus: stored units minus every anticipated pickup (all job owners) minus the floor. Carried and
    /// in-transit units don't count (they may still be on their way in).</summary>
    public static int Free(Ship donor, ID<ResourceRules> t, CargoPolicy? cargo) =>
        Surplus(donor.Resources.GetResourceTotal(t, includeStored: true, includeCarried: false, includeOffShipCarried: false,
            includeAnticipatedDeliveries: false, includeAnticipatedPickups: true, MPValueType.Displayed), Floor(donor, t, cargo));

    /// <summary>Receiver deficit: target minus stored, carried and anticipated deliveries.</summary>
    public static int Need(Ship receiver, ID<ResourceRules> t, CargoPolicy? cargo) =>
        Deficit(Target(receiver, t, cargo), receiver.Resources.GetResourceTotal(t, includeStored: true, includeCarried: true, includeOffShipCarried: false,
            includeAnticipatedDeliveries: true, includeAnticipatedPickups: false, MPValueType.Displayed));

    // ---------------------------------------------------------------- keep-max / sell / pickup (P4, D17)

    /// <summary>The enabled assignment's cargo policy of a ship, or null (not automated / not enabled).</summary>
    public static CargoPolicy? CargoOf(Ship ship) => Store.Find(ship.UniqueID.ToString()) is { Enabled: true } a ? a.Cargo : null;

    /// <summary>Valid keep-max ("не больше"): set, not below keep-min and not keep-all; otherwise null (unmanaged).</summary>
    public static int? Max(CargoPolicy? cargo, ID<ResourceRules> t)
    {
        string id = t.ToString();
        if (cargo?.KeepMax == null || !cargo.KeepMax.TryGetValue(id, out int m) || cargo.KeepAll?.Contains(id) == true)
        {
            return null;
        }
        return m >= KeepMin(cargo, t) && m >= 0 ? m : null;
    }

    /// <summary>Policy code for a type (absent = <see cref="Never"/>, unknown values returned as -1).</summary>
    public static int Code(Dictionary<string, int>? policy, ID<ResourceRules> t) =>
        policy == null || !policy.TryGetValue(t.ToString(), out int c) ? Never : c is Never or AboveMax or AboveMin ? c : -1;

    /// <summary>Units held or on the way in: stored, carried and anticipated deliveries.</summary>
    internal static int Holding(Ship ship, ID<ResourceRules> t) =>
        ship.Resources.GetResourceTotal(t, includeStored: true, includeCarried: true, includeOffShipCarried: false,
            includeAnticipatedDeliveries: true, includeAnticipatedPickups: false, MPValueType.Displayed);

    /// <summary>Keep-max that also clears the ship's vanilla desire (part of the floor, set in the game's own window);
    /// below it the cap is ignored and <see cref="MaxConflict"/> reports it.</summary>
    public static int? EffectiveMax(Ship ship, ID<ResourceRules> t, CargoPolicy? cargo) =>
        Max(cargo, t) is int m && m >= World.Keep(ship, t) ? m : null;

    /// <summary>First type whose keep-max is below the ship's vanilla desire (cap ignored), as a blocker, or null.</summary>
    public static string? MaxConflict(Ship ship, CargoPolicy? cargo)
    {
        if (cargo?.KeepMax == null)
        {
            return null;
        }
        foreach (string id in cargo.KeepMax.Keys)
        {
            var t = new ID<ResourceRules>(id);
            if (Max(cargo, t) != null && EffectiveMax(ship, t, cargo) == null)
            {
                return L.F("MiningDrone/BlockMaxBelowMin", NameOf(id));
            }
        }
        return null;
    }

    /// <summary>Receiver ceiling: keep-max minus what it holds or receives; unlimited without a valid keep-max.</summary>
    public static int Headroom(Ship receiver, ID<ResourceRules> t, CargoPolicy? cargo) =>
        EffectiveMax(receiver, t, cargo) is int m ? Deficit(m, Holding(receiver, t)) : int.MaxValue;

    /// <summary>Types of <paramref name="mask"/> already at or above their keep-max (incoming counted): automatic mining
    /// and looting skip them.</summary>
    public static ulong AtMaxMask(Ship ship, CargoPolicy? cargo, ulong mask)
    {
        ulong at = 0;
        if (cargo?.KeepMax == null || cargo.KeepMax.Count == 0)
        {
            return 0;
        }
        ResourceRules[] all = World.Resources;
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            if ((mask & (1UL << i)) != 0 && Headroom(ship, all[i].ID, cargo) <= 0)
            {
                at |= 1UL << i;
            }
        }
        return at;
    }

    /// <summary>Units the Sell policy lets go: stored minus every anticipated pickup, above max(keep-max, floor) or above
    /// the floor. Never: 0. Used by Trade as the cap of every submitted quantity.</summary>
    public static int Sellable(Ship ship, ID<ResourceRules> t, CargoPolicy? cargo) =>
        Allowed(Code(cargo?.Sell, t), ship.Resources.GetResourceTotal(t, includeStored: true, includeCarried: false, includeOffShipCarried: false,
            includeAnticipatedDeliveries: false, includeAnticipatedPickups: true, MPValueType.Displayed), Floor(ship, t, cargo), EffectiveMax(ship, t, cargo));

    /// <summary>Configuration problems of a cargo policy, as blockers: keep-max below keep-min (ignored), unknown sell /
    /// pickup codes (action off), "above max" without a valid keep-max (inert).</summary>
    public static void Problems(CargoPolicy cargo, List<string> blockers)
    {
        if (cargo.KeepMax != null)
        {
            foreach (string id in cargo.KeepMax.Keys)
            {
                if (Max(cargo, new ID<ResourceRules>(id)) == null && cargo.KeepAll?.Contains(id) != true)
                {
                    blockers.Add(L.F("MiningDrone/BlockMaxBelowMin", NameOf(id)));
                }
            }
        }
        Codes(cargo, cargo.Sell, blockers);
        Codes(cargo, cargo.Pickup, blockers);
    }

    private static void Codes(CargoPolicy cargo, Dictionary<string, int>? policy, List<string> blockers)
    {
        if (policy == null)
        {
            return;
        }
        foreach (string id in policy.Keys)
        {
            var t = new ID<ResourceRules>(id);
            int c = Code(policy, t);
            if (c < 0)
            {
                blockers.Add(L.F("MiningDrone/BlockPolicyCode", NameOf(id)));
            }
            else if (c == AboveMax && Max(cargo, t) == null)
            {
                blockers.Add(L.F("MiningDrone/BlockAboveMaxNoMax", NameOf(id)));
            }
        }
    }

    /// <summary>Localized resource name once the rules are loaded, else the raw id.</summary>
    private static string NameOf(string id) =>
        World.IndexOf(new ID<ResourceRules>(id)) is int i && i >= 0 ? World.Resources[i].NameKey.Text : id;

    /// <summary>Offline invariants, run by tools/jitcheck.</summary>
    internal static string? SelfCheck()
    {
        if (Surplus(100, int.MaxValue) != 0 || Surplus(100, 30) != 70 || Surplus(20, 30) != 0)
        {
            return "surplus wrong";
        }
        if (Deficit(50, 20) != 30 || Deficit(50, 80) != 0)
        {
            return "deficit wrong";
        }
        // Sell/pickup: above max keeps max(M, floor); above min keeps the floor; never / unknown / keep-all give nothing.
        if (Allowed(AboveMax, 100, 30, 60) != 40 || Allowed(AboveMax, 100, 80, 60) != 20 || Allowed(AboveMax, 100, 30, null) != 0
            || Allowed(AboveMin, 100, 30, 60) != 70 || Allowed(Never, 100, 0, 0) != 0 || Allowed(-1, 100, 0, 0) != 0
            || Allowed(AboveMax, 100, int.MaxValue, 0) != 0 || Allowed(AboveMin, 100, int.MaxValue, null) != 0 || Allowed(AboveMax, 50, 10, 60) != 0)
        {
            return "sell policy wrong";
        }
        var d = new Dictionary<string, int> { ["a"] = 1, ["b"] = 7 };
        if (Code(d, new ID<ResourceRules>("a")) != AboveMax || Code(d, new ID<ResourceRules>("b")) != -1 || Code(d, new ID<ResourceRules>("c")) != Never || Code(null, new ID<ResourceRules>("a")) != Never)
        {
            return "policy code wrong";
        }
        var cp = new CargoPolicy { KeepMin = new() { ["a"] = 50 }, KeepMax = new() { ["a"] = 40, ["b"] = 0, ["c"] = 10 }, KeepAll = new() { "c" } };
        if (Max(cp, new ID<ResourceRules>("a")) != null || Max(cp, new ID<ResourceRules>("b")) != 0 || Max(cp, new ID<ResourceRules>("c")) != null)
        {
            return "keep-max validation wrong";
        }
        return null;
    }
}
