using System;
using System.Threading.Tasks;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;

namespace MiningDrone.Autofleet;

/// <summary>Input barrier of <see cref="Ledger"/>: needs the running game's net manager.</summary>
internal static partial class Ledger
{
    /// <summary>
    /// Done once an input enqueued now has executed or been rejected. Counts executed network input ticks
    /// (NetManager.UntilAfterTick: BaseMPManager.cs:970/1061, SPManager.cs:80-86), not time. SP executes queued inputs
    /// on the next tick; MP schedules a local input at most MaxInputTickDelay ticks ahead (MPHostManager.cs:258) and
    /// sends it on the next Update (BaseMPManager.cs:756), hence the margin.
    /// </summary>
    public static Func<bool> Barrier(GameRoot game)
    {
        int n = game.NetManager is BaseMPManager mp ? mp.Rules.MaxInputTickDelay + 2 : 2;
        Task t = game.NetManager.UntilAfterTick(n);
        return () => t.IsCompleted;
    }
}
