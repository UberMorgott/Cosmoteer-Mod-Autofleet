using System.Collections.Generic;
using MiningDrone.Autofleet;
using Xunit;

namespace MiningDrone.Tests;

public class SortTests
{
    // variable-height blocks, pad 8: tops 0, 48, 136, 184
    private static readonly float[] Tops = { 0, 48, 136, 184 };
    private static readonly float[] Heights = { 40, 80, 40, 40 };

    [Fact]
    public void InsertIndex_NoOpUpAndDown()
    {
        Assert.Equal(1, SortMath.InsertIndex(Tops, Heights, 1, 48 + 40)); // dragged block 1 at rest
        Assert.Equal(0, SortMath.InsertIndex(Tops, Heights, 2, 10));
        Assert.Equal(3, SortMath.InsertIndex(Tops, Heights, 0, 230));
    }

    [Fact]
    public void TargetTop_OpensGapForVariableHeights()
    {
        // block 0 (h40) dragged to the end: others move up by 48, dragged slot right under the last one
        Assert.Equal(0f, SortMath.TargetTop(Tops, Heights, 0, 3, 1, 8));
        Assert.Equal(88f, SortMath.TargetTop(Tops, Heights, 0, 3, 2, 8));
        Assert.Equal(184f, SortMath.TargetTop(Tops, Heights, 0, 3, 0, 8));
        // block 1 (h80) dragged to the top: block 0 moves down by 88, dragged takes top 0
        Assert.Equal(88f, SortMath.TargetTop(Tops, Heights, 1, 0, 0, 8));
        Assert.Equal(0f, SortMath.TargetTop(Tops, Heights, 1, 0, 1, 8));
        Assert.Equal(136f, SortMath.TargetTop(Tops, Heights, 1, 0, 2, 8));
        // no-op: nothing moves
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(Tops[i], SortMath.TargetTop(Tops, Heights, 2, 2, i, 8));
        }
    }

    [Fact]
    public void Approach_FrameRateIndependentAndSettles()
    {
        float one = SortMath.Approach(100, 0, 32, 60);
        float two = SortMath.Approach(SortMath.Approach(100, 0, 16, 60), 0, 16, 60);
        Assert.Equal(one, two, 3);
        Assert.Equal(0f, SortMath.Approach(0.3f, 0, 16, 60));
    }

    [Fact]
    public void NeighboursAndMoveNear()
    {
        var keys = new List<string> { "a", "b", "c", "d" };
        Assert.Equal(("d", "c"), SortMath.Neighbours(keys, 0, 2));
        Assert.Equal(((string?)null, "d"), SortMath.Neighbours(keys, 1, 3));
        Assert.Equal(("a", (string?)null), SortMath.Neighbours(keys, 3, 0));
        var l = new List<string>(keys);
        Assert.True(SortMath.MoveNear(l, "a", "d", null));
        Assert.Equal("b,c,a,d", string.Join(",", l));
        Assert.True(SortMath.MoveNear(l, "b", null, "d"));
        Assert.Equal("c,a,d,b", string.Join(",", l));
        Assert.False(SortMath.MoveNear(l, "c", "a", null)); // already right before a
        Assert.False(SortMath.MoveNear(l, "x", "a", null));
    }

    [Fact]
    public void MoveModule_DisplayOrderMatchesDrop()
    {
        var p = new Profile();
        ModuleInstance M(string id, int prio)
        {
            var m = new ModuleInstance { Id = id, Priority = prio };
            p.Modules.Add(m);
            return m;
        }
        ModuleInstance a = M("a", 50), b = M("b", 30), c = M("c", 30), d = M("d", 10);
        // a dropped between b and c (displayed a,b,c,d -> b,a,c,d)
        Assert.True(Registry.MoveModule(p, a, c, b));
        Assert.Equal("b,a,c,d", Shown(p));
        Assert.Equal(30, b.Priority);
        Assert.Equal(10, d.Priority);
        // c dropped to the bottom
        Assert.True(Registry.MoveModule(p, c, null, d));
        Assert.Equal("b,a,d,c", Shown(p));
        // no-op: d right before c already with c's priority
        Assert.False(Registry.MoveModule(p, d, c, a));
    }

    private static string Shown(Profile p)
    {
        var l = new List<ModuleInstance>(p.Modules);
        l.Sort((x, y) => x.Priority != y.Priority ? y.Priority.CompareTo(x.Priority) : p.Modules.IndexOf(x).CompareTo(p.Modules.IndexOf(y)));
        return string.Join(",", l.ConvertAll(m => m.Id));
    }
}
