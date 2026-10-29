using System.Collections.Generic;
using Xunit;

namespace MiningDrone.Tests;

public class BanFilterTests
{
    private static readonly string[] Ids = { "iron", "copper", "gold", "hyperium" };

    private static int Index(string id) => System.Array.IndexOf(Ids, id);

    [Fact]
    public void Fingerprint_FollowsContentOfSetMutatedInPlace()
    {
        Assert.Equal(0UL, World.MaskOf<string>(null, Index)); // vanilla null = nothing banned
        var set = new HashSet<string> { "copper" };
        ulong before = World.MaskOf(set, Index);
        Assert.Equal(1UL << 1, before);
        set.Add("hyperium"); // same reference, new content
        Assert.NotEqual(before, World.MaskOf(set, Index));
        set.Remove("hyperium");
        Assert.Equal(before, World.MaskOf(set, Index));
        set.Add("unknown"); // unknown ids are not filterable: no bit
        Assert.Equal(before, World.MaskOf(set, Index));
    }

    [Fact]
    public void TemplateToggle_SendsOnlyToggledTypes_KeepsOtherShipTweaks()
    {
        ulong tplBefore = 0b0001, tplAfter = 0b0011; // template bans: copper newly unchecked
        ulong toggled = tplBefore ^ tplAfter;
        ulong shipBanned = 0b1001; // player also banned hyperium on this ship only
        ulong send = World.BanInputs(shipBanned, tplAfter, toggled);
        Assert.Equal(0b0010UL, send); // copper only; the hyperium tweak survives
        Assert.Equal(0UL, World.BanInputs(0b0011, tplAfter, toggled)); // already banned: no input
    }

    [Fact]
    public void Assignment_CopiesTemplate_SendsOnlyDifferences()
    {
        const ulong stackable = 0b1111;
        ulong target = 0b0110; // template bans copper + gold
        Assert.Equal(0b1100UL, World.BanInputs(0b1010, target, stackable)); // unban hyperium, ban gold; copper already banned
        Assert.Equal(0UL, World.BanInputs(target, target, stackable));
        Assert.Equal(0UL, World.BanInputs(1UL << 40, 0, stackable)); // bans outside the stackable scope are never touched
    }
}
