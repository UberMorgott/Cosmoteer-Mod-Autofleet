using System;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>
/// The decisions of <see cref="ShipAgent"/> that need no game state, kept free of game types so the test project
/// compiles this file on its own.
/// </summary>
internal sealed partial class ShipAgent
{
    /// <summary>Guard engages a found enemy unless it is already fighting a still hostile target (or another module evades).</summary>
    internal static bool GuardRetarget(bool evading, bool byGuard, bool targetHostile) => !evading || (byGuard && !targetHostile);

    /// <summary>
    /// Harvest target emptied while we fly to it: no wanted yield left and our crew is out of reach (in reach, our own
    /// salvage may still be hauling its last drops; the end-of-command check handles that).
    /// </summary>
    internal static bool EmptiedEnRoute(ulong yield, ulong want, float dist, float crewReach) => (yield & want) == 0 && dist > crewReach;

    /// <summary>A Loot salvage target whose junk kind is not an enabled source (asteroids / non-junk: never).</summary>
    internal static bool LootSourceOff(int kinds, World.JunkKind kind) => kind != World.JunkKind.None && (kinds & World.KindBit(kind)) == 0;

    /// <summary>Value integrity: intact part value over the value of every part the ship should have (destroyed ones
    /// count as 0, unbuilt blueprint drafts don't count). Not HP. 1 when nothing is known.</summary>
    internal static float HullIntegrity(int partsValue, int repairCost, int destroyedValue)
    {
        int whole = partsValue + destroyedValue;
        return whole <= 0 ? 1f : Math.Clamp((partsValue - repairCost) / (float)whole, 0f, 1f);
    }

    /// <summary>Hull value at which a damage retreat ends (hysteresis over the trigger).</summary>
    internal static float DamageRecover(float enter) => Math.Min(1f, Math.Max(0.9f, enter + 0.05f));

    /// <summary>Slot angle relative to the anchor's heading: evenly around it, the first slot straight behind.</summary>
    internal static float FormationAngle(int k, int n) => MathF.PI + 2f * MathF.PI * k / Math.Max(n, 1);

    /// <summary>Edge distance of a slot: the configured one, or one own diameter for the vanilla default (a follow without
    /// a radius stays where it is and ignores FollowAngle: BaseFollowCommand.GetDestination); raised so neighbouring
    /// slots on the ring are at least 1.2 own diameters apart (chord 2·D·sin(π/n), D = center distance). Rounded up to
    /// 50 so hull damage does not re-issue the follow every tick.</summary>
    internal static float FormationEdge(int n, float anchorR, float ownR, float configured)
    {
        float edge = configured >= 0f ? configured : MathF.Ceiling(2f * ownR / 50f) * 50f;
        if (n < 2)
        {
            return edge;
        }
        float need = MathF.Ceiling((ownR * 1.2f / MathF.Sin(MathF.PI / n) - anchorR - ownR) / 50f) * 50f;
        return Math.Max(edge, need);
    }

    /// <summary>Hire budget: credits above the floor not already promised to an unsettled hire.</summary>
    internal static int HireBudget(int spendable, int floor, int reserved) => Math.Max(0, spendable - floor - reserved);

    /// <summary>Receiver side: crew still wanted = min(target, beds) - assigned (away/in transit included) - incoming.</summary>
    internal static int CrewWanted(int target, int beds, int assigned, int incoming) => Math.Max(0, Math.Min(target, beds) - assigned - incoming);

    /// <summary>Donor side: crew that may leave = aboard (away crew never count) - already leaving - keep.</summary>
    internal static int CrewSpare(int aboard, int leaving, int keep) => Math.Max(0, aboard - leaving - keep);

    /// <summary>Hysteresis per output: 0 (off) at/above the cap, 1 (on) below cap - max(batch, 10% of cap, 1), else -1.</summary>
    internal static int FactoryState(int total, int max, int batch)
    {
        if (total >= max)
        {
            return 0;
        }
        // A margin as large as the cap would never restart: then empty stock (0 < max) always restarts.
        int margin = Math.Max(1, Math.Max(batch, (max + 9) / 10));
        return total < Math.Max(1, max - margin) ? 1 : -1;
    }

    /// <summary>Offline invariants, run by tools/jitcheck (production hysteresis).</summary>
    internal static string? ProductionSelfCheck()
    {
        if (FactoryState(100, 100, 1) != 0 || FactoryState(95, 100, 1) != -1 || FactoryState(89, 100, 1) != 1 || FactoryState(90, 100, 1) != -1)
        {
            return "factory hysteresis wrong";
        }
        if (FactoryState(0, 1, 1) != 1 || FactoryState(0, 10, 20) != 1 || FactoryState(1, 10, 20) != -1 || FactoryState(10, 10, 20) != 0)
        {
            return "factory never restarts for a small cap";
        }
        if (FactoryState(0, 0, 1) != 0 || FactoryState(3, 5, 1) != 1 || FactoryState(4, 5, 1) != -1 || FactoryState(30, 50, 20) != -1 || FactoryState(29, 50, 20) != 1)
        {
            return "factory margin wrong";
        }
        return null;
    }

    /// <summary>Offline invariants of the pure agent rules, part of <see cref="SelfCheck"/>. null = pass.</summary>
    internal static string? RulesSelfCheck()
    {
        if (!EmptiedEnRoute(0b100, 0b011, 5000f, 300f)) return "looted target kept en route";
        if (EmptiedEnRoute(0b110, 0b011, 5000f, 300f)) return "target with wanted yield dropped en route";
        if (EmptiedEnRoute(0, 0b011, 100f, 300f)) return "own salvage cut off while hauling its last drops";
        if (!LootSourceOff(World.KindWreck, World.JunkKind.Cache) || LootSourceOff(World.KindCache | World.KindDerelict, World.JunkKind.Derelict)
            || LootSourceOff(0, World.JunkKind.None)) return "loot source toggle not applied to the current target";
        // Profiles saved before caches/derelicts existed inherit their wrecks toggle; explicit values and new modules stand.
        var old = new ModuleInstance { Type = Registry.Loot, Params = { [Registry.PWrecks] = 1, [Registry.PDerelicts] = 0 } };
        if (Resolver.ParamOf(old, Registry.PCaches) != 1 || Resolver.ParamOf(old, Registry.PDerelicts) != 0) return "new loot sources do not inherit the old wrecks toggle";
        if (Resolver.ParamOf(Registry.NewInstance(Registry.Loot, "x"), Registry.PCaches) != 0) return "new loot module collects caches by default";
        Resolver.Materialize(old);
        old.Params[Registry.PWrecks] = 0;
        if (Resolver.ParamOf(old, Registry.PCaches) != 1) return "caches still follow wrecks after the split";
        if (!GuardRetarget(true, true, false)) return "guard keeps a dead target while another enemy is near";
        if (GuardRetarget(true, true, true)) return "guard drops a live target for another enemy";
        if (GuardRetarget(true, false, false)) return "guard overrides an evasion it does not own";
        if (!GuardRetarget(false, false, false)) return "guard ignores an enemy while idle";
        // Crew: target capped by beds, in-transit/incoming counted on the receiver; donor keeps `keep` aboard.
        if (CrewWanted(20, 12, 5, 2) != 5 || CrewWanted(0, 30, 0, 0) != 0 || CrewWanted(10, 30, 8, 3) != 0) return "crew wanted wrong";
        if (CrewSpare(15, 2, 10) != 3 || CrewSpare(10, 0, 10) != 0 || CrewSpare(9, 0, 10) != 0) return "crew spare wrong";
        if (HireBudget(10000, 5000, 1000) != 4000 || HireBudget(4000, 5000, 0) != 0 || HireBudget(6000, 5000, 2000) != 0) return "hire budget wrong";
        // Damage trigger: destroyed parts count as lost value; hysteresis stays within 0..1 for every slider value.
        if (MathF.Abs(HullIntegrity(600, 100, 400) - 0.5f) > 1e-4f || HullIntegrity(0, 0, 0) != 1f) return "hull value integrity wrong";
        if (DamageRecover(0.65f) != 0.9f || DamageRecover(0.95f) != 1f || DamageRecover(0.95f) <= 0.95f) return "damage retreat hysteresis wrong";
        // Formation: distinct slots; ring wide enough that neighbours (ship radius 100) keep 1.2 diameters apart.
        if (FormationAngle(0, 3) == FormationAngle(1, 3) || MathF.Abs(FormationAngle(0, 1) - MathF.PI) > 1e-4f) return "formation slots collide or the single slot is not behind";
        float e6 = FormationEdge(6, 200f, 100f, 0f), d6 = e6 + 200f + 100f;
        if (2f * d6 * MathF.Sin(MathF.PI / 6) < 240f - 1e-3f) return "formation ring too tight for its ships";
        if (FormationEdge(1, 200f, 100f, -1f) <= 0f || FormationEdge(2, 200f, 100f, 5000f) != 5000f) return "formation edge ignores the configured / default distance";
        return null;
    }
}
