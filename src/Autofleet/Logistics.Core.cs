using System;

namespace MiningDrone.Autofleet;

/// <summary>
/// The policy math of <see cref="Logistics"/> that needs no game state, kept free of game types so the test project
/// compiles this file on its own.
/// </summary>
internal static partial class Logistics
{
    internal static int Surplus(int have, int floor) => floor == int.MaxValue ? 0 : Math.Max(0, have - floor);

    internal static int Deficit(int target, int have) => Math.Max(0, target - have);

    /// <summary>Sell / Pickup policy codes (stable, persisted): 0 or absent = never, 1 = above keep-max, 2 = above the
    /// protected floor. Any other value is unknown: the action stays off and a blocker is shown.</summary>
    public const int Never = 0, AboveMax = 1, AboveMin = 2;

    /// <summary>Pure policy math: units a donor may give under <paramref name="code"/>.</summary>
    internal static int Allowed(int code, int have, int floor, int? max) => code switch
    {
        AboveMax => max is int m && floor != int.MaxValue ? Surplus(have, Math.Max(m, floor)) : 0,
        AboveMin => Surplus(have, floor),
        _ => 0,
    };
}
