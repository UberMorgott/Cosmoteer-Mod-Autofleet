using Xunit;

namespace MiningDrone.Tests;

/// <summary>Goal-progress watchdog of a travel leg (ShipAgent.GoalProgress): back-and-forth avoidance stalls, real approach does not.</summary>
public class GoalProgressTests
{
    private const float Gain = 50f;
    private const long Window = 20_000;

    [Fact]
    public void Oscillation_InFrontOfACluster_Stalls()
    {
        // Ship swings side to side 300 units every second (the displacement watchdog sees motion) at a fixed gap.
        var p = new ShipAgent.GoalProgress();
        bool stalled = false;
        for (long t = 0; t <= 21_000; t += 500)
        {
            stalled = p.Stalled(false, 5000f, 0f, t / 500 % 2 == 0 ? 2000f : 2100f, t, Gain, Window);
        }
        Assert.True(stalled);
    }

    [Fact]
    public void SlowSteadyApproach_NeverStalls()
    {
        // 10 units per second: each 50-unit gain is counted cumulatively from the last best.
        var p = new ShipAgent.GoalProgress();
        for (long t = 0; t <= 120_000; t += 1000)
        {
            Assert.False(p.Stalled(false, 5000f, 0f, 3000f - t / 100f, t, Gain, Window));
        }
    }

    [Fact]
    public void NewLeg_StartsOver()
    {
        var p = new ShipAgent.GoalProgress();
        p.Stalled(false, 0f, 0f, 1000f, 0, Gain, Window);
        Assert.False(p.Stalled(true, 0f, 0f, 1000f, 30_000, Gain, Window)); // re-issued / next waypoint
        Assert.True(p.Stalled(false, 0f, 0f, 1000f, 50_001, Gain, Window));
    }

    [Fact]
    public void MovingFollowedShip_StartsOver_StaticOneDoesNot()
    {
        var p = new ShipAgent.GoalProgress();
        p.Stalled(false, 0f, 0f, 1000f, 0, Gain, Window);
        Assert.False(p.Stalled(false, 100f, 0f, 1000f, 30_000, Gain, Window)); // the followed ship travelled
        Assert.True(p.Stalled(false, 110f, 0f, 1000f, 50_001, Gain, Window)); // jitter below the gain is no movement
    }
}
