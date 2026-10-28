using MiningDrone.Autofleet;
using Xunit;

namespace MiningDrone.Tests;

/// <summary>Only the real owner's client drives a ship; a ship that changes owner drops its assignment.</summary>
public class OwnershipTests
{
    [Theory]
    [InlineData(0, 0, true)]   // own ship
    [InlineData(1, 0, false)]  // teammate's ship, commandable via SharedShip: not ours to drive
    [InlineData(-1, 0, false)] // unowned
    [InlineData(1, -4, true)]  // creative "play as everyone"
    public void OnlyOwnerDrives(int owner, int local, bool drives) => Assert.Equal(drives, World.Drives(owner, local));

    [Fact]
    public void TransferDropsPreviousOwnersAssignment()
    {
        var a = new ShipAssignment { ShipId = "s", OwnerIndex = 0, Enabled = true, UserPaused = true, Temp = TempIntent.ReturnHome };
        Assert.True(World.Drives(0, 0));
        int owner = 1; // MPTransferShipControlInput / SharedShip take: Metadata.PlayerIndex 0 -> 1
        Assert.False(World.Drives(owner, 0));
        Assert.True(World.Drives(owner, 1)); // the new owner may assign it
        a.Disable();
        Assert.False(a.Enabled);
        Assert.False(a.UserPaused);
        Assert.Equal(TempIntent.None, a.Temp);
    }
}
