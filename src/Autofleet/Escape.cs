using System;
using Halfling.Geometry;

namespace MiningDrone.Autofleet;

/// <summary>
/// Pure pursuit-aware escape steering for the Avoidance policy (no Ship, clock or commands: offline-testable).
/// Each threat is predicted to its closest point of approach (CPA, relative motion, capped at Horizon); the flee
/// direction is the clearance-weighted repulsion from those predicted positions, bent to a committed side
/// (tangent component) so the ship arcs around instead of reversing. The side is chosen once per episode by scoring
/// both turn-limited arcs and only flips after two consecutive re-plans where the committed side is predicted unsafe
/// and the other one clearly better. Heading changes are rate-limited (TurnRate), so the issued waypoints trace an arc.
/// </summary>
internal static class Escape
{
    internal readonly record struct Threat(Vector2 Pos, Vector2 Vel, float R);

    /// <summary>Heading = last commanded flee direction (unit; zero before the first step). Side = +1 / -1 (0 unset).</summary>
    internal readonly record struct State(Vector2 Heading, int Side, int FlipVotes);

    internal const float Horizon = 8f;          // s, CPA / safety look-ahead
    internal const float TurnRate = 25f * MathF.PI / 180f; // rad/s heading change limit
    internal const float SafeMargin = 1000f;    // predicted clearance past the threat circle that counts as safe
    private const float ArcTime = 4f;           // s, side-scoring arc length
    private const float MinSpeed = 100f;        // assumed speed when (nearly) stopped
    private const float FlipGain = 500f;        // other side must be this much clearer to earn a flip vote
    private const float Bend = 0.6f, BendFast = 0.15f; // tangent share; small when a faster pursuer closes (run straight)

    private static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;
    private static Vector2 Perp(Vector2 v) => new Vector2(-v.Y, v.X);
    private static Vector2 Unit(Vector2 v, Vector2 fallback) { float l = v.Length; return l > 1e-3f ? v * (1f / l) : fallback; }
    private static Vector2 Rotate(Vector2 v, float a) { float c = MathF.Cos(a), s = MathF.Sin(a); return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c); }
    internal static float Angle(Vector2 from, Vector2 to) => MathF.Atan2(from.X * to.Y - from.Y * to.X, Dot(from, to));

    private static Vector2 TurnToward(Vector2 from, Vector2 to, float maxStep)
    {
        float a = Angle(from, to);
        return MathF.Abs(a) <= maxStep ? to : Rotate(from, MathF.Sign(a) * maxStep);
    }

    /// <summary>Min clearance to every (straight-moving) threat while we fly a turn-limited arc from heading toward goal.</summary>
    private static float ArcClearance(Vector2 pos, Vector2 heading, Vector2 goal, float speed, Threat[] t, float time)
    {
        const float dt = 0.5f;
        float min = float.MaxValue;
        Vector2 p = pos, h = heading;
        for (float s = dt; s <= time + 1e-3f; s += dt)
        {
            h = TurnToward(h, goal, TurnRate * dt);
            p += h * (speed * dt);
            for (int i = 0; i < t.Length; i++)
            {
                min = MathF.Min(min, p.DistanceTo(t[i].Pos + t[i].Vel * s) - t[i].R);
            }
        }
        return min;
    }

    /// <summary>Would flying straight at dest keep SafeMargin clear of every threat over the horizon? (Resume check.)</summary>
    internal static bool LegSafe(Vector2 pos, Vector2 vel, Vector2 dest, Threat[] t)
    {
        Vector2 d = Unit(dest - pos, default);
        float time = MathF.Min(Horizon, pos.DistanceTo(dest) / MathF.Max(vel.Length, MinSpeed));
        return d == default || time < 0.5f || ArcClearance(pos, d, d, MathF.Max(vel.Length, MinSpeed), t, time) >= SafeMargin;
    }

    /// <summary>
    /// Seconds until we enter threat circle t under straight relative motion (0 = inside now; +inf = never: receding,
    /// co-moving or passing wide). Solves |r + w·s| = R for the first root, so entry before a later CPA counts.
    /// </summary>
    internal static float EntryTime(Vector2 pos, Vector2 vel, Threat t)
    {
        Vector2 r = t.Pos - pos, w = t.Vel - vel;
        float rr = Dot(r, r), rw = Dot(r, w), ww = Dot(w, w), c = rr - t.R * t.R;
        if (c < 0f)
        {
            return 0f;
        }
        float disc = rw * rw - ww * c;
        if (rw >= 0f || ww < 1e-3f || disc < 0f)
        {
            return float.PositiveInfinity;
        }
        return (-rw - MathF.Sqrt(disc)) / ww;
    }

    /// <summary>Escape trigger: inside the threat's reach now, or on course to enter it within Horizon. A far enemy that
    /// sits still or fights elsewhere is not a danger until we (or it) actually close in.</summary>
    internal static bool Danger(Vector2 pos, Vector2 vel, Threat t) => EntryTime(pos, vel, t) <= Horizon;

    /// <summary>Target-leg look-ahead: a target that fell inside an enemy range is only dropped once flying on would
    /// enter a range within this many seconds (longer than Horizon, so we turn off before an escape is needed).</summary>
    internal const float LegLook = 2f * Horizon;

    /// <summary>Would flying straight at dest (at least MinSpeed) enter any threat circle within LegLook?</summary>
    internal static bool LegEnters(Vector2 pos, Vector2 vel, Vector2 dest, Threat[] t)
    {
        float speed = MathF.Max(vel.Length, MinSpeed);
        Vector2 v = Unit(dest - pos, default) * speed; // zero-length leg: zero velocity, so only "inside now" counts
        float time = MathF.Min(LegLook, pos.DistanceTo(dest) / speed);
        for (int i = 0; i < t.Length; i++)
        {
            if (EntryTime(pos, v, t[i]) <= time)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// One re-plan. dt = seconds since the previous step (ignored on the first step, which may turn freely: fleeing
    /// straight into the threat to honour a turn limit is worse than one sharp turn). homeDir: unit vector to the
    /// base/anchor or zero. safe: no threat's predicted CPA clearance falls below SafeMargin within Horizon.
    /// </summary>
    internal static State Step(State prev, Vector2 pos, Vector2 vel, Threat[] t, Vector2 homeDir, float dt, out bool safe)
    {
        safe = true;
        if (t.Length == 0)
        {
            // Nothing to flee: hold the course. Bending it with no repulsion rotated the heading every re-plan (loops).
            return new State(Unit(prev.Heading, Unit(vel, new Vector2(1f, 0f))), prev.Side, 0);
        }
        float ownSpeed = vel.Length, speed = MathF.Max(ownSpeed, MinSpeed);
        bool fastPursuer = false;
        Vector2 away = default, nearestAway = default;
        float nearest = float.MaxValue;
        for (int i = 0; i < t.Length; i++)
        {
            Vector2 r = t[i].Pos - pos, w = t[i].Vel - vel;
            float tc = Math.Clamp(-Dot(r, w) / MathF.Max(Dot(w, w), 1e-3f), 0f, Horizon);
            float clr = (r + w * tc).Length - t[i].R;
            if (clr < SafeMargin)
            {
                safe = false;
            }
            if (Dot(r, w) < 0f && t[i].Vel.Length > ownSpeed)
            {
                fastPursuer = true;
            }
            Vector2 fromRaw = Unit(-r, default);
            Vector2 from = Unit(-(r + w * tc), fromRaw); // away along the predicted separation at closest approach
            away += from * (1f / MathF.Max(clr, 200f));
            if (r.Length - t[i].R < nearest)
            {
                nearest = r.Length - t[i].R;
                nearestAway = fromRaw;
            }
        }
        // Cancelling repulsion: keep the previous heading, then plain away from the nearest, then a fixed axis.
        Vector2 baseDir = Unit(away, Unit(prev.Heading, Unit(nearestAway, new Vector2(1f, 0f))));
        float bend = fastPursuer ? BendFast : Bend;
        Vector2 Cand(int side) => Unit(baseDir + Perp(baseDir) * (bend * side), baseDir);
        bool first = prev.Heading == default;
        Vector2 cur = first ? Unit(vel, baseDir) : prev.Heading;
        float Score(int side)
        {
            Vector2 c = Cand(side);
            // Home only breaks near-ties (clearance first).
            return ArcClearance(pos, first ? c : cur, c, speed, t, ArcTime) + 0.2f * Dot(c, homeDir) * SafeMargin;
        }
        int sideNow = prev.Side;
        int votes = prev.FlipVotes;
        if (sideNow == 0)
        {
            sideNow = Score(-1) > Score(1) ? -1 : 1;
            votes = 0;
        }
        else
        {
            float keep = Score(sideNow), other = Score(-sideNow);
            votes = keep - 0.2f * SafeMargin < 0f && other > keep + FlipGain ? votes + 1 : 0;
            if (votes >= 2)
            {
                sideNow = -sideNow;
                votes = 0;
            }
        }
        Vector2 goal = Cand(sideNow);
        Vector2 heading = first ? goal : TurnToward(cur, goal, TurnRate * MathF.Max(dt, 0f));
        return new State(heading, sideNow, votes);
    }

    /// <summary>Offline invariants, run by tools/jitcheck.</summary>
    internal static string? SelfCheck()
    {
        Vector2 pos = new Vector2(0f, 0f);
        // Pursuer 3000 to the east closing at 150/s, ours stopped; slight per-step jitter + permuted second threat.
        var a = new Threat(new Vector2(3000f, 0f), new Vector2(-150f, 0f), 1500f);
        var b = new Threat(new Vector2(2500f, 2500f), new Vector2(0f, 0f), 800f);
        var st = default(State);
        Vector2 vel = default;
        int side0 = 0;
        for (int k = 0; k < 50; k++)
        {
            float j = (k % 3 - 1) * 20f;
            var ts = k % 2 == 0
                ? new[] { a with { Pos = a.Pos + new Vector2(j, -j) }, b }
                : new[] { b, a with { Pos = a.Pos + new Vector2(-j, j) } };
            Vector2 prevH = st.Heading;
            st = Step(st, pos, vel, ts, default, 0.5f, out _);
            if (float.IsNaN(st.Heading.X) || float.IsNaN(st.Heading.Y) || MathF.Abs(st.Heading.Length - 1f) > 1e-3f)
            {
                return "escape heading not a finite unit vector";
            }
            if (k == 0)
            {
                side0 = st.Side;
            }
            else
            {
                if (st.Side != side0)
                {
                    return "escape flipped side without a threat change";
                }
                float turn = MathF.Abs(Angle(prevH, st.Heading));
                // Curvature = dθ / ds; ds = speed·dt with speed ≥ MinSpeed, so κ ≤ TurnRate / MinSpeed.
                if (turn > TurnRate * 0.5f + 1e-4f || turn / (MathF.Max(vel.Length, MinSpeed) * 0.5f) > TurnRate / MinSpeed + 1e-6f)
                {
                    return "escape heading turned faster than the arc limit";
                }
                if (Dot(st.Heading, new Vector2(1f, 0f)) > 0f)
                {
                    return "escape heads toward the pursuer";
                }
            }
            vel = st.Heading * 120f;
        }
        // All-zero inputs stay finite.
        var z = Step(default, pos, default, new[] { new Threat(pos, default, 0f) }, default, 0f, out bool zs);
        if (float.IsNaN(z.Heading.X) || zs)
        {
            return "escape degenerate input not finite / wrongly safe";
        }
        // Faster pursuer whose intercept is inside the horizon is never "safe"; a slow one far away is.
        Step(default, pos, new Vector2(-100f, 0f), new[] { new Threat(new Vector2(2500f, 0f), new Vector2(-300f, 0f), 1000f) }, default, 0.5f, out bool fs);
        Step(default, pos, new Vector2(-200f, 0f), new[] { new Threat(new Vector2(9000f, 0f), new Vector2(-50f, 0f), 1000f) }, default, 0.5f, out bool ss);
        if (fs || !ss)
        {
            return "escape safety verdict wrong (fast intercept / slow far pursuer)";
        }
        // Faster pursuer from the east while we already run west: keep running west (Codex review case).
        var fp = Step(default, pos, new Vector2(-100f, 0f), new[] { new Threat(new Vector2(2000f, 0f), new Vector2(-300f, 0f), 1000f) }, default, 0.5f, out _);
        if (Dot(fp.Heading, new Vector2(1f, 0f)) >= 0f)
        {
            return "escape turns toward a faster pursuer";
        }
        // Threats gone mid-episode (the 5 s safe wait keeps steering): the course must not keep turning (in-game loops).
        foreach (float sdt in new[] { 0.5f, 1.25f })
        {
            for (int side = -1; side <= 1; side += 2)
            {
                Vector2 h0 = new Vector2(0.6f, -0.8f);
                var q = new State(h0, side, 1);
                Vector2 qp = pos;
                for (int k = 0; k < 10; k++)
                {
                    q = Step(q, qp, q.Heading * 105f, Array.Empty<Threat>(), default, sdt, out bool qs);
                    qp += q.Heading * 105f * sdt;
                    if (!qs || float.IsNaN(q.Heading.X) || MathF.Abs(q.Heading.Length - 1f) > 1e-3f || MathF.Abs(Angle(h0, q.Heading)) > 1e-3f)
                    {
                        return "escape keeps turning with no threats (loops)";
                    }
                }
            }
        }
        var e0 = Step(default, pos, default, Array.Empty<Threat>(), default, 0f, out bool e0s);
        if (!e0s || float.IsNaN(e0.Heading.X) || MathF.Abs(e0.Heading.Length - 1f) > 1e-3f)
        {
            return "escape default state with no threats not finite / safe";
        }
        if (LegSafe(pos, default, new Vector2(6000f, 0f), new[] { a }) || !LegSafe(pos, default, new Vector2(-6000f, 0f), new[] { a }))
        {
            return "escape resume check wrong (toward / away from pursuer)";
        }
        // Trigger: real danger only (reach now or entry within Horizon), never mere radar contact.
        var zone = new Threat(new Vector2(9000f, 0f), default, 3000f);
        if (Danger(pos, default, zone))
        {
            return "danger: far stationary enemy (fighting elsewhere) triggers";
        }
        if (Danger(pos, new Vector2(0f, 200f), zone with { Vel = new Vector2(0f, 200f) }))
        {
            return "danger: co-moving enemy triggers";
        }
        if (Danger(pos, default, new Threat(new Vector2(9000f, 5000f), new Vector2(-1000f, 0f), 3000f)))
        {
            return "danger: enemy passing wide triggers";
        }
        if (!Danger(pos, default, zone with { Vel = new Vector2(-800f, 0f) }))
        {
            return "danger: intercept within horizon missed";
        }
        if (!Danger(new Vector2(7000f, 0f), new Vector2(-500f, 0f), zone))
        {
            return "danger: inside reach while receding missed";
        }
        // Entry at 6 s but CPA at 9 s (beyond Horizon): still danger; entry at 8.5 s: not yet.
        var late = new Threat(new Vector2(9000f, 0f), new Vector2(-1000f, 0f), 3000f);
        if (!Danger(pos, default, late) || MathF.Abs(EntryTime(pos, default, late) - 6f) > 1e-3f)
        {
            return "danger: entry before a late CPA missed";
        }
        if (Danger(pos, default, late with { Pos = new Vector2(11500f, 0f) }))
        {
            return "danger: entry past the horizon triggers";
        }
        // Target in a stationary enemy's range: keep flying while far, drop it once entry is near.
        Threat[] at = { new Threat(new Vector2(9000f, 0f), default, 3000f) };
        if (LegEnters(pos, new Vector2(105f, 0f), new Vector2(9000f, 0f), at) || !LegEnters(new Vector2(4500f, 0f), new Vector2(105f, 0f), new Vector2(9000f, 0f), at))
        {
            return "target leg look-ahead wrong (far / near the enemy range)";
        }
        // Short / zero-length legs (near a waypoint) far from the range are safe; a zero-length leg inside it is not.
        if (LegEnters(pos, new Vector2(105f, 0f), new Vector2(40f, 0f), at) || LegEnters(pos, new Vector2(105f, 0f), pos, at)
            || !LegEnters(new Vector2(7000f, 0f), default, new Vector2(7000f, 0f), at))
        {
            return "target leg look-ahead wrong (short / zero-length leg)";
        }
        return null;
    }
}
