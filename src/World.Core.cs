using System;

namespace MiningDrone;

/// <summary>
/// The parts of <see cref="World"/> that need no game state (junk kinds, stasis verdict, collect priority), kept free of
/// game types so the test project compiles this file on its own.
/// </summary>
internal static partial class World
{
    public enum JunkKind : byte
    {
        None,
        Wreck,
        Cache,
        Derelict,
    }

    /// <summary>Loot source bits (AgentConfig.LootKinds).</summary>
    public const int KindWreck = 1, KindCache = 2, KindDerelict = 4;

    /// <summary>Only the ship's real owner drives it (SharedShip makes teammates' ships commandable, so the vanilla
    /// commandable check would let two clients drive one ship). -4 = "play as everyone" (creative), as in vanilla
    /// CommandManager.IsCommandableBy. An offline owner's ships are driven by nobody.</summary>
    internal static bool Drives(int owner, int local) => local == -4 || owner == local;

    public static int KindBit(JunkKind k) => k == JunkKind.None ? 0 : 1 << ((int)k - 1);

    /// <summary>Exactly one kind per junk ship: abandoned provenance wins over storage-pod provenance; the rest are wrecks.
    /// Split-off fragments carry neither (Ship.CloneEmptyFrom copies no tags/labels/handler): they are wrecks.</summary>
    internal static JunkKind KindOf(bool junkShip, bool abandoned, bool pod) =>
        !junkShip ? JunkKind.None : abandoned ? JunkKind.Derelict : pod ? JunkKind.Cache : JunkKind.Wreck;

    /// <summary>Stasis candidate verdict: a confirmed wanted yield; else rejected when the whole yield is known; else unknown.</summary>
    internal enum Verdict : byte
    {
        Confirmed,
        Reject,
        Maybe,
    }

    internal static Verdict StasisVerdict(ulong known, bool complete, ulong want) =>
        (known & want) != 0 ? Verdict.Confirmed : complete ? Verdict.Reject : Verdict.Maybe;

    /// <summary>Distance floor of the priority cost: next to a low-rank target a higher rank still wins within ~1.7 km.</summary>
    public const float PriorityK = 1000f * 1000f;

    /// <summary>Penalty a stasis rock pays (the old 1 km live-vs-stasis bias), inside the priority cost.</summary>
    public const float StasisBias = 1000f * 1000f;

    /// <summary>Pure core of <see cref="RankTable(List{string}, ulong)"/>: <paramref name="listed"/> = resource indexes in
    /// priority order (-1 = unknown ID, skipped; duplicates keep the first). Ranks fit 0..64.</summary>
    internal static byte[] RankTable(int[] listed, ulong mask)
    {
        var ranks = new byte[65];
        byte next = 0;
        ulong seen = 0;
        foreach (int t in listed)
        {
            if (t is >= 0 and < 64 && (mask & (1UL << t)) != 0 && (seen & (1UL << t)) == 0)
            {
                seen |= 1UL << t;
                ranks[t] = next++;
            }
        }
        for (int t = 0; t < 64; t++)
        {
            if ((seen & (1UL << t)) == 0)
            {
                ranks[t] = next;
            }
        }
        ranks[64] = next;
        return ranks;
    }

    /// <summary>Best (lowest) rank among the yields; no known yield (stasis "maybe") = the last rank.</summary>
    internal static int RankOf(ulong yields, byte[] ranks)
    {
        int best = ranks[64];
        for (ulong y = yields; y != 0; y &= y - 1)
        {
            best = Math.Min(best, ranks[System.Numerics.BitOperations.TrailingZeroCount(y)]);
        }
        return best;
    }

    /// <summary>Priority cost: each rank down doubles the effective distance. Double: 4^64 x 1e14 still fits.
    /// shortcut: type rank and distance only, no yield amount (rock masks carry no quantity); add one if a per-type amount cache appears.</summary>
    internal static double PriorityCost(float d2, int rank) => Math.ScaleB((double)d2 + PriorityK, 2 * rank);

    /// <summary>Doubling search may stop at radius r: nothing outside it (d >= r, rank >= 0) can beat this cost.</summary>
    internal static bool RingDone(double bestCost, float r) => bestCost <= PriorityCost(r * r, 0);

    /// <summary>Offline invariants of the collect priority (D18) and junk kinds, part of <see cref="SelfCheck"/>. null = pass.</summary>
    internal static string? PrioritySelfCheck()
    {
        // order: type 5 (e.g. hyperium) > unknown > duplicate > type 2 (gold); type 7 selected but unlisted; type 9 not selected.
        ulong sel = (1UL << 5) | (1UL << 2) | (1UL << 7);
        byte[] rk = RankTable(new[] { 5, -1, 5, 9, 2 }, sel);
        if (rk[5] != 0 || rk[2] != 1 || rk[7] != 2 || rk[64] != 2) return "priority ranks not dense over listed selected types";
        if (RankOf((1UL << 2) | (1UL << 5), rk) != 0 || RankOf(0, rk) != 2) return "target rank is not its best yield";
        // Long list of unknown IDs: ranks stay small, costs finite.
        var longList = new int[1000];
        Array.Fill(longList, -1);
        longList[999] = 7;
        if (RankTable(longList, sel)[7] != 0) return "unknown IDs shift ranks";
        if (double.IsInfinity(PriorityCost(1e14f, 64))) return "priority cost overflows";
        // Equal distance: higher priority wins. Twice as far per rank: hyperium at 2.5 km beats gold at 1 km.
        if (CostOf(1e6f, 1UL << 5, rk) >= CostOf(1e6f, 1UL << 2, rk)) return "higher priority loses at equal distance";
        double gold1k = CostOf(1e6f, 1UL << 2, rk), hyp2500 = CostOf(2500f * 2500f, 1UL << 5, rk);
        if (hyp2500 >= gold1k) return "farther higher-priority target loses";
        if (CostOf(5000f * 5000f, 1UL << 5, rk) <= gold1k) return "lower priority never wins when much nearer (no fallback)";
        // Doubling search: gold found in the 2 km ring must not stop it (hyperium may sit at 2-4 km); a near hyperium does.
        if (RingDone(gold1k, 2000f)) return "ring search stops before a higher rank could win";
        if (!RingDone(CostOf(1e6f, 1UL << 5, rk), 2000f)) return "ring search continues past a settled best";
        // Stasis bias goes in before the rank scaling: it is 4x heavier on a rank-1 rock.
        if (CostOf(4e6f, 1UL << 2, rk, StasisBias) - CostOf(4e6f, 1UL << 2, rk) != 4.0 * StasisBias) return "stasis bias applied after rank scaling";
        // No priority: raw squared distance (old nearest behaviour).
        if (CostOf(1234f, 1UL << 2, null) != 1234f) return "legacy cost changed";
        // Junk kinds: one per ship, abandoned beats pod provenance; non-junk is none; bits match the Loot toggles.
        if (KindOf(false, true, true) != JunkKind.None || KindOf(true, false, false) != JunkKind.Wreck || KindOf(true, false, true) != JunkKind.Cache
            || KindOf(true, true, true) != JunkKind.Derelict) return "junk ship kind wrong";
        if (KindBit(JunkKind.Wreck) != KindWreck || KindBit(JunkKind.Cache) != KindCache || KindBit(JunkKind.Derelict) != KindDerelict || KindBit(JunkKind.None) != 0)
            return "junk kind bits do not match the Loot toggles";
        // Stasis junk: a wanted cargo-only yield confirms; a complete non-matching record is rejected; a partial one (cargo
        // memory, unknown hull) or a pristine one stays unknown.
        if (StasisVerdict(1UL << 3, false, 1UL << 3) != Verdict.Confirmed) return "stasis cargo match not confirmed";
        if (StasisVerdict(1UL << 4, true, 1UL << 3) != Verdict.Reject || StasisVerdict(0, true, 1UL << 3) != Verdict.Reject) return "complete stasis record without the wanted type kept";
        if (StasisVerdict(1UL << 4, false, 1UL << 3) != Verdict.Maybe || StasisVerdict(0, false, 1UL << 3) != Verdict.Maybe) return "partial stasis record treated as known";
        return null;
    }

    /// <summary>Cost of a candidate: raw squared distance without priority (old behaviour), else the priority cost.</summary>
    private static double CostOf(float d2, ulong yields, byte[]? ranks, float bias = 0f) =>
        ranks == null ? d2 : PriorityCost(d2 + bias, RankOf(yields, ranks));
}
