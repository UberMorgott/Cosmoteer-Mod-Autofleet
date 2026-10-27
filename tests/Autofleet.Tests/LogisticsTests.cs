using MiningDrone.Autofleet;
using Xunit;

namespace MiningDrone.Tests;

public class LogisticsTests
{
    [Theory]
    [InlineData(100, int.MaxValue, 0)] // keep-all floor: nothing spare
    [InlineData(100, 30, 70)]
    [InlineData(20, 30, 0)]
    public void Surplus_AboveTheFloor(int have, int floor, int expected) => Assert.Equal(expected, Logistics.Surplus(have, floor));

    [Theory]
    [InlineData(50, 20, 30)]
    [InlineData(50, 80, 0)]
    public void Deficit_BelowTheTarget(int target, int have, int expected) => Assert.Equal(expected, Logistics.Deficit(target, have));

    // Sell/pickup: above max keeps max(M, floor); above min keeps the floor; never / unknown / keep-all give nothing.
    [Theory]
    [InlineData(Logistics.AboveMax, 100, 30, 60, 40)]
    [InlineData(Logistics.AboveMax, 100, 80, 60, 20)]
    [InlineData(Logistics.AboveMax, 100, 30, null, 0)]
    [InlineData(Logistics.AboveMin, 100, 30, 60, 70)]
    [InlineData(Logistics.Never, 100, 0, 0, 0)]
    [InlineData(-1, 100, 0, 0, 0)]
    [InlineData(Logistics.AboveMax, 100, int.MaxValue, 0, 0)]
    [InlineData(Logistics.AboveMin, 100, int.MaxValue, null, 0)]
    [InlineData(Logistics.AboveMax, 50, 10, 60, 0)]
    public void Allowed_SellPolicy(int code, int have, int floor, int? max, int expected) =>
        Assert.Equal(expected, Logistics.Allowed(code, have, floor, max));
}
