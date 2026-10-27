using MiningDrone.Autofleet;
using Xunit;

namespace MiningDrone.Tests;

/// <summary>
/// The mod's offline invariants (the SelfCheck methods tools/jitcheck also runs against the real game assemblies),
/// headless. Each returns null on pass, else what broke. One class: several share static state (Store, Ledger).
/// </summary>
public class SelfCheckTests
{
    [Fact]
    public void FleetChecks_TriStateRules() => Assert.Null(FleetChecks.SelfCheck());

    [Fact]
    public void FleetJump_EligibilityOrder() => Assert.Null(FleetJump.SelfCheck());

    [Fact]
    public void Ledger_ReservationsReleaseWhenSettled() => Assert.Null(Ledger.SelfCheck());

    [Fact]
    public void World_CollectPriorityAndJunkKinds() => Assert.Null(World.PrioritySelfCheck());

    [Fact]
    public void ShipAgent_ProductionHysteresis() => Assert.Null(ShipAgent.ProductionSelfCheck());

    [Fact]
    public void ShipAgent_Rules() => Assert.Null(ShipAgent.RulesSelfCheck());

    [Fact]
    public void MinerWindow_SliderSnapAndCargoEdits() => Assert.Null(MinerWindow.SelfCheck());

    [Fact]
    public void ShipCommands_FollowProfileModules() => Assert.Null(ShipCommands.SelfCheck());

    [Fact]
    public void Store_LiveProfilesAndMigration() => Assert.Null(Store.SelfCheck());
}
