using System;
using System.Collections.Generic;

namespace MiningDrone.Autofleet;

/// <summary>
/// Reservation ledger for inputs that leave no local trace until they execute (MPSetCrewSourceInput, MPHireCrewInput
/// have no ExecuteLocalImmediate; MPSetUITogglesInput neither): crew on the move, beds, incoming/outgoing crew per ship, credits. Resources need
/// none (Logistics). In memory, owner client only; cleared when the sim changes (load). An entry is held until its
/// input has certainly executed or been rejected (<see cref="Barrier"/>); after that live game state is authoritative
/// and is re-read, so release never means "succeeded".
/// </summary>
internal static partial class Ledger
{
    internal enum Kind
    {
        Soul,
        Bed,
        ShipIn,
        ShipOut,
        Money,
        Toggle, // a part UI toggle with an MPSetUITogglesInput in flight (Production)
    }

    private struct Entry
    {
        public Kind K;
        public object? Key;
        public int Qty;
        public Func<bool> Settled;
    }

    private static readonly List<Entry> Entries = new();

    public static void Reserve(Kind k, object? key, int qty, Func<bool> settled) =>
        Entries.Add(new Entry { K = k, Key = key, Qty = qty, Settled = settled });

    public static int Sum(Kind k, object? key)
    {
        Prune();
        int n = 0;
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].K == k && Equals(Entries[i].Key, key))
            {
                n += Entries[i].Qty;
            }
        }
        return n;
    }

    public static bool Has(Kind k, object key) => Sum(k, key) != 0;

    /// <summary>Anything still unsettled (one hire input in flight per client).</summary>
    public static bool Any(Kind k)
    {
        Prune();
        return Entries.Exists(e => e.K == k);
    }

    public static void Clear() => Entries.Clear();

    private static void Prune() => Entries.RemoveAll(static e => e.Settled());

    /// <summary>Offline invariants, run by tools/jitcheck.</summary>
    internal static string? SelfCheck()
    {
        Clear();
        bool done = false;
        object a = new(), b = new();
        Reserve(Kind.Bed, a, 1, () => done);
        Reserve(Kind.Bed, a, 1, () => done);
        Reserve(Kind.ShipIn, b, 3, () => false);
        Reserve(Kind.Money, null, 50, () => done);
        if (Sum(Kind.Bed, a) != 2 || Sum(Kind.Bed, b) != 0 || Sum(Kind.ShipIn, b) != 3 || Sum(Kind.Money, null) != 50 || !Any(Kind.Money))
        {
            Clear();
            return "sum wrong";
        }
        done = true;
        string? r = Sum(Kind.Bed, a) != 0 || Any(Kind.Money) || Sum(Kind.ShipIn, b) != 3 ? "settled entries not released" : null;
        Clear();
        return r;
    }
}
