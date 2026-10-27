using System;

namespace MiningDrone.Autofleet;

/// <summary>Manual commands a module contributes to the command bar.</summary>
[Flags]
internal enum ShipCmd
{
    None = 0,
    Unload = 1,
    Return = 2,
}

/// <summary>
/// Module-derived manual commands (spec §1, command bar): the command bar shows what the ship's effective modules offer, never a
/// hard-coded role. Universal commands (start, pause/resume, unassign) are not module commands. Only Offload
/// contributes today: unload now + return to the offload point (DoReturnHome flies to <see cref="AgentConfig.OffloadId"/>).
/// </summary>
internal static class ShipCommands
{
    public static ShipCmd Of(AgentConfig c) => c.Offload.Active ? ShipCmd.Unload | ShipCmd.Return : ShipCmd.None;

    /// <summary>Contributed and executable now: both offload commands need a resolved offload point.</summary>
    public static bool CanRun(AgentConfig c, ShipCmd cmd) => (Of(c) & cmd) != ShipCmd.None && c.OffloadId != null;

    /// <summary>Offline invariants, run by tools/jitcheck: the command set follows the profile's modules.</summary>
    internal static string? SelfCheck()
    {
        var guard = new Profile { Id = "selfcheck-cmd-guard" };
        guard.Modules.Add(Registry.NewInstance(Registry.Guard, "g"));
        guard.Modules.Add(Registry.NewInstance(Registry.AnchorLink, "an"));
        var miner = new Profile { Id = "selfcheck-cmd-miner" };
        miner.Modules.Add(Registry.NewInstance(Registry.Offload, "o"));
        miner.Modules.Add(Registry.NewInstance(Registry.Mine, "m"));
        Store.Profiles.Add(guard);
        Store.Profiles.Add(miner);
        try
        {
            AgentConfig Cfg(Profile p, string? offloadTarget)
            {
                var a = new ShipAssignment { ShipId = "selfcheck-cmd", ProfileId = p.Id };
                a.Bindings.OffloadTarget = offloadTarget;
                var c = new AgentConfig();
                Resolver.Refresh(c, a);
                return c;
            }
            AgentConfig g = Cfg(guard, "base");
            if (Of(g) != ShipCmd.None || CanRun(g, ShipCmd.Unload))
            {
                return "profile without offload offers unload/return";
            }
            AgentConfig m = Cfg(miner, "base");
            if (Of(m) != (ShipCmd.Unload | ShipCmd.Return) || !CanRun(m, ShipCmd.Unload) || !CanRun(m, ShipCmd.Return))
            {
                return "offload profile lacks unload/return";
            }
            if (CanRun(Cfg(miner, null), ShipCmd.Unload))
            {
                return "unload runnable without an offload point";
            }
            miner.Modules[0].Enabled = false;
            if (Of(Cfg(miner, "base")) != ShipCmd.None)
            {
                return "disabled offload module still offers commands";
            }
            return null;
        }
        finally
        {
            Store.Profiles.Remove(guard);
            Store.Profiles.Remove(miner);
        }
    }
}
