using Xunit;

namespace MiningDrone.Tests;

public class CargoFillTests
{
    [Fact]
    public void Percent_MatchesShownCells()
    {
        Assert.Equal(92, ShipAgent.CellPercent(76, 82, 90)); // the reported "76/82 (100%)" case
        Assert.Equal(99, ShipAgent.CellPercent(399, 400, 400)); // never rounds up to 100 while a cell is free
        Assert.Equal(100, ShipAgent.CellPercent(82, 82, 82));
        Assert.Equal(0, ShipAgent.CellPercent(0, 82, 82));
    }

    [Fact]
    public void NoAvailableCells_FullOnlyWithAHold()
    {
        Assert.Equal(100, ShipAgent.CellPercent(0, 0, 10));
        Assert.Equal(1f, ShipAgent.CellFill(0, 0, 10));
        Assert.Equal(0, ShipAgent.CellPercent(0, 0, 0));
        Assert.Equal(0f, ShipAgent.CellFill(0, 0, 0));
    }

    [Theory]
    [InlineData(9, 10, 0.9f)]
    [InlineData(76, 82, 0.9f)]
    [InlineData(81, 82, 0.95f)]
    public void Threshold_AgreesWithShownPercent(int used, int avail, float threshold)
    {
        bool due = ShipAgent.CellFill(used, avail, avail) >= threshold;
        Assert.Equal(due, ShipAgent.CellPercent(used, avail, avail) >= (int)System.MathF.Round(threshold * 100f));
    }
}
