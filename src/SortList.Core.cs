using System;
using System.Collections.Generic;

namespace MiningDrone;

/// <summary>Game-free math of <see cref="SortList"/> (drag-reorder of a vertical list of variable-height blocks).
/// Positions are block tops relative to the list's top; "insert index" is the dragged block's index among the others.</summary>
internal static class SortMath
{
    /// <summary>Where the dragged block lands: the number of other blocks whose middle is above its middle.</summary>
    public static int InsertIndex(IReadOnlyList<float> tops, IReadOnlyList<float> heights, int drag, float dragMid)
    {
        int t = 0;
        for (int i = 0; i < tops.Count; i++)
        {
            if (i != drag && tops[i] + heights[i] / 2f < dragMid)
            {
                t++;
            }
        }
        return t;
    }

    /// <summary>Target top of block <paramref name="i"/> while the dragged block would land at insert index
    /// <paramref name="t"/>: blocks between the old and new slot move by the dragged height plus the padding.</summary>
    public static float TargetTop(IReadOnlyList<float> tops, IReadOnlyList<float> heights, int drag, int t, int i, float pad)
    {
        if (i == drag)
        {
            // the slot it would take: block t moves down to make room, or the block it follows moved up
            return t <= drag ? tops[t] : tops[t] + heights[t] - heights[drag];
        }
        float step = heights[drag] + pad;
        int j = i < drag ? i : i - 1;
        return tops[i] + (i < drag && j >= t ? step : i > drag && j < t ? -step : 0f);
    }

    /// <summary>Frame-rate independent exponential approach (time constant <paramref name="tauMs"/>).</summary>
    public static float Approach(float cur, float target, float dtMs, float tauMs)
    {
        float v = target + (cur - target) * MathF.Exp(-dtMs / tauMs);
        return MathF.Abs(v - target) < 0.5f ? target : v;
    }

    /// <summary>Moves <paramref name="item"/> right before <paramref name="before"/>, or right after
    /// <paramref name="after"/> when <paramref name="before"/> is null. False when nothing moved.</summary>
    public static bool MoveNear<T>(List<T> l, T item, T? before, T? after) where T : class
    {
        T? anchor = before ?? after;
        int from = l.IndexOf(item);
        if (from < 0 || anchor == null || ReferenceEquals(anchor, item) || !l.Contains(anchor))
        {
            return false;
        }
        l.RemoveAt(from);
        int at = l.IndexOf(anchor) + (before != null ? 0 : 1);
        l.Insert(at, item);
        return at != from;
    }

    /// <summary>The neighbours of the dropped key in the new order: (next, previous) among the other keys.</summary>
    public static (string? Before, string? After) Neighbours(IReadOnlyList<string> keys, int drag, int t)
    {
        string? before = null, after = null;
        int j = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            if (i == drag)
            {
                continue;
            }
            if (j == t - 1)
            {
                after = keys[i];
            }
            if (j == t)
            {
                before = keys[i];
            }
            j++;
        }
        return (before, after);
    }
}
