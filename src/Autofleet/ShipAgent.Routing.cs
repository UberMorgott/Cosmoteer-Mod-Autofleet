using System;
using System.Collections.Generic;
using Cosmoteer.Game;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Halfling.Geometry;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>Shared routing service: travel/follow legs around enemy ranges (Avoidance policy) and stuck obstacles.</summary>
internal sealed partial class ShipAgent
{
    private const long AvoidBlacklistMs = 300_000;
    private readonly List<Vector2> _wp = new();
    private readonly Dictionary<Vector2, long> _stasisSkip = new();
    private Vector2? _travelDest;
    private Ship? _travelShip;
    private int _travelAttempts;
    private float _travelBest;
    private long _travelBestAt;
    private bool _routed;
    private Vector2? _activeLeg;

    /// <summary>Temporary avoidance circles learned from stuck recovery (body we got wedged on), with expiry.</summary>
    private readonly List<(Circle Zone, long Until)> _obstacles = new();

    /// <summary>Appends live stuck obstacles to World.ThreatZones for one routing call; returns how many were added.</summary>
    private int PushObstacles(Vector2 dest)
    {
        int added = 0;
        for (int i = _obstacles.Count - 1; i >= 0; i--)
        {
            if (_obstacles[i].Until <= _now)
            {
                _obstacles.RemoveAt(i);
                continue;
            }
            Circle c = _obstacles[i].Zone;
            if (c.Contains(dest) || c.Contains(Ship.WorldCenter))
            {
                continue; // never route around the destination itself or a body we are still inside
            }
            World.ThreatZones.Add(c);
            added++;
        }
        return added;
    }

    private static void PopObstacles(int added)
    {
        if (added > 0)
        {
            World.ThreatZones.RemoveRange(World.ThreatZones.Count - added, added);
        }
    }

    /// <summary>
    /// Issues the final command, preceded by MoveCommand waypoints around threat circles when the Avoidance policy is
    /// on (or a stuck obstacle is known) and the straight line is blocked. First command replaces the queue, the rest
    /// are appended (separate MPEnqueueCommandsInput with append: true), all registered as ours. Returns false when no
    /// clear route exists.
    /// </summary>
    private bool IssueTravel(GameRoot game, Vector2 dest, Command final, bool autoCancelSalvage, Ship? targetShip)
    {
        bool newTarget = _travelDest == null || _travelDest.Value.DistanceTo(dest) > 300f;
        if (newTarget)
        {
            _travelAttempts = 0;
            _travelBest = float.MaxValue;
            _travelBestAt = _now;
        }
        _travelDest = dest;
        _travelShip = targetShip;
        _routed = false;
        int threats = Cfg.Avoid.Active ? World.ThreatZones.Count : 0;
        if (!Cfg.Avoid.Active)
        {
            World.ClearThreats();
        }
        int obstacles = PushObstacles(dest);
        try
        {
            if (World.ThreatZones.Count > 0)
            {
                if (threats > 0 && World.InThreat(dest))
                {
                    Log.Info($"[{Id}] route: destination inside enemy range ({threats} zones); giving up on it");
                    return false;
                }
                _wp.Clear();
                if (!World.Route(Ship.WorldCenter, dest, _wp))
                {
                    Log.Info($"[{Id}] route: no clear path around {threats} enemy zones + {obstacles} obstacle(s); giving up on this target");
                    return false;
                }
                if (_wp.Count > 0)
                {
                    Log.Info($"[{Id}] route: {_wp.Count} waypoint(s) around {threats} enemy zones + {obstacles} obstacle(s) to target at {Ship.WorldCenter.DistanceTo(dest):0}");
                    Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), _wp[0], null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage);
                    for (int i = 1; i < _wp.Count; i++)
                    {
                        Append(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), _wp[i], null, CollisionAvoidanceFlags.All, isTentative: false));
                    }
                    Append(game, final);
                    _routed = true;
                    return true;
                }
            }
            Issue(game, final, autoCancelSalvage);
            return true;
        }
        finally
        {
            PopObstacles(obstacles);
        }
    }

    private Ship? _followShip;
    private string _followLeg = "";
    private int _followAttempts;
    private long _followRetryAt;

    /// <summary>
    /// Follow a friendly ship (offload target / anchor) with the Avoidance policy honoured: FollowCommand can't take
    /// waypoints, so move waypoints come first and the Follow is appended. If the ship itself sits inside an enemy
    /// zone, the approach goes to its least-threatened side (shortest final leg). If no clear route exists, the
    /// ship holds at a safe point near it and retries (the target is never blacklisted).
    /// </summary>
    /// <summary>A fresh copy of the last issued follow (distance, slot angle, crew wait), so a re-route keeps the leg's
    /// parameters (an escort keeps its formation slot); edge 60 when none is known.</summary>
    private FollowCommand RebuildFollow(Ship target)
    {
        FollowCommand? p = _followProto;
        var f = new FollowCommand(Ship, Ship.Commands.AllocLocalCommandID(), target, CollisionAvoidanceFlags.All, isTentative: false, waitForCrew: p?.WaitForCrew ?? true);
        f.EdgeToEdgeFollowRadius = p != null ? p.EdgeToEdgeFollowRadius : 60f;
        if (p != null)
        {
            f.FollowAngle = p.FollowAngle;
            f.UseWorldRelativeFollowAngle = p.UseWorldRelativeFollowAngle;
        }
        return f;
    }

    private FollowCommand? _followProto;

    private void IssueFollow(GameRoot game, Ship target, FollowCommand follow, string leg)
    {
        if (!ReferenceEquals(_followShip, target) || _followLeg != leg)
        {
            _followAttempts = 0;
        }
        _followShip = target;
        _followProto = follow;
        _followLeg = leg;
        _travelDest = null;
        _travelShip = null;
        _routed = false;
        if (!Cfg.Avoid.Active || World.ThreatZones.Count == 0)
        {
            Issue(game, follow, autoCancelSalvage: true);
            return;
        }
        // Leg end: the slot point of a slotted follow (formation), else the followed ship (BaseFollowCommand.GetDestination).
        Vector2 end = follow.FollowAngle.HasValue && follow.EdgeToEdgeFollowRadius.HasValue ? follow.GetDestination().Location : target.WorldCenter;
        Vector2 goal = end;
        if (World.InThreat(goal))
        {
            goal = World.SafeSideOf(goal, target.WorldBoundingRadius + 600f);
        }
        _wp.Clear();
        bool ok = !World.InThreat(goal) && World.Route(Ship.WorldCenter, goal, _wp);
        if (!ok)
        {
            _followAttempts++;
            Vector2 hold = World.EscapePoint(Ship.WorldCenter);
            Log.Info($"[{Id}] leg '{leg}': no safe route to {target.Metadata.PlainShipName} (attempt {_followAttempts}); holding at a safe point and retrying");
            Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), hold, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true);
            _followRetryAt = _now + 10_000;
            return;
        }
        if (_wp.Count == 0 && goal.DistanceTo(end) < 1f)
        {
            Issue(game, follow, autoCancelSalvage: true);
            return;
        }
        Log.Info($"[{Id}] leg '{leg}': {_wp.Count} waypoint(s){(goal.DistanceTo(end) > 1f ? " + safe-side approach" : "")} around {World.ThreatZones.Count} enemy zones to {target.Metadata.PlainShipName}");
        if (_wp.Count > 0)
        {
            Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), _wp[0], null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true);
            for (int i = 1; i < _wp.Count; i++)
            {
                Append(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), _wp[i], null, CollisionAvoidanceFlags.All, isTentative: false));
            }
            if (goal.DistanceTo(end) > 1f)
            {
                Append(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), goal, null, CollisionAvoidanceFlags.All, isTentative: false));
            }
        }
        else
        {
            Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), goal, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true);
        }
        if (World.InThreat(end))
        {
            // The ship itself sits in an enemy zone: the follow would drag us back in. Stop at the safe point and
            // re-check every 10 s; the follow (and offload) resumes once the zone has cleared.
            Log.Info($"[{Id}] leg '{leg}': {target.Metadata.PlainShipName} is inside enemy range; holding on its safe side");
            _followRetryAt = _now + 10_000;
            _routed = true;
            return;
        }
        Append(game, follow);
        _routed = true;
    }

    /// <summary>
    /// Avoidance policy, every tick: escape if inside a threat circle (emergency intent); give up a target that is
    /// now inside one; re-route when the active leg became blocked or there is no progress; after the configured
    /// attempts the target is skipped for 5 min of game time.
    /// </summary>
    private bool AvoidTick(GameRoot game, long now)
    {
        World.BuildThreatZones(game.Sim, Ship, now);
        Vector2 pos = Ship.WorldCenter;
        if (EscapeEpisode(game, now, ModuleType.Avoidance))
        {
            return true; // an active escape keeps going: contact loss is handled by its own 5 s safe-exit
        }
        if (World.ThreatZones.Count == 0)
        {
            return false;
        }
        if (_followShip != null && (State == MinerState.FlyToBase || State == MinerState.Holding || State == MinerState.Offload || State == MinerState.Trading))
        {
            // Validate the ACTIVE leg (a routed waypoint MoveCommand, or the follow itself) against refreshed zones,
            // and require progress on it; routed follow legs are no longer exempt.
            Command? act = Ship.Commands.CurDisplayedCommand;
            // A slotted follow (formation) is judged against its slot point, not the followed ship's center.
            Vector2 legEndF = _routed && act is MoveCommand && IsOurCommand(act) ? act.GetDestination().Location
                : act is FollowCommand sf && IsOurCommand(act) && sf.FollowAngle.HasValue && sf.EdgeToEdgeFollowRadius.HasValue ? sf.GetDestination().Location
                : _followShip.WorldCenter;
            if (_activeLeg == null || _activeLeg.Value.DistanceTo(legEndF) > 50f)
            {
                _activeLeg = legEndF;
                _travelBest = float.MaxValue;
                _travelBestAt = now;
            }
            float dF = pos.DistanceTo(legEndF);
            if (dF < _travelBest - 200f)
            {
                _travelBest = dF;
                _travelBestAt = now;
            }
            bool legBad = act is MoveCommand && IsOurCommand(act) && (World.FirstBlockingPublic(pos, legEndF) || World.InThreat(legEndF));
            bool followBad = !(act is MoveCommand) && pos.DistanceTo(legEndF) > 1500f && World.FirstBlockingPublic(pos, legEndF);
            bool stalledF = State == MinerState.FlyToBase && now - _travelBestAt > 60_000 && dF > 1500f;
            bool blockedF = legBad || followBad || stalledF;
            bool retry = _followRetryAt != 0 && now >= _followRetryAt;
            if ((blockedF || retry) && _followShip.ExistsInSim(game.Sim))
            {
                _followRetryAt = 0;
                Log.Info($"[{Id}] leg '{_followLeg}': {(retry ? "retrying" : "path became blocked")}, re-routing");
                IssueFollow(game, _followShip, RebuildFollow(_followShip), _followLeg);
                return true;
            }
            return false;
        }
        if (_travelDest is not Vector2 dest || (State != MinerState.Mining && State != MinerState.Approach && State != MinerState.CollectLoose && State != MinerState.Trading))
        {
            return false;
        }
        // Active leg = destination of the command being executed now (a waypoint while routed, else the target).
        // Obstruction and progress are judged on that leg, so valid sideways detours are not treated as stalls and
        // enemies that move into a routed leg still trigger a re-route.
        Vector2 legEnd = dest;
        Command? active = Ship.Commands.CurDisplayedCommand;
        if (_routed && active is MoveCommand && IsOurCommand(active))
        {
            legEnd = active.GetDestination().Location;
        }
        if (World.InThreat(dest))
        {
            // Target inside an enemy range (e.g. a wreck at someone else's fight): keep flying while the leg stays clear,
            // so a far enemy doesn't turn us away; drop it (short skip) only when entry is near. Skipping the re-route
            // below is deliberate: it would hit IssueTravel's dest-in-zone rejection and blacklist the target at once.
            Vector2 v = Ship.Physics.Body is { } b ? (Vector2)b.LinearVelocity : default;
            if (!Escape.LegEnters(pos, v, legEnd, Threats()))
            {
                return false;
            }
            Log.Info($"[{Id}] target inside enemy range and entry is near ({pos.DistanceTo(dest):0} away, {ZoneInfo(dest)}); skipping it for {TargetZoneSkipMs / 1000} s");
            GiveUpTravel(now, ms: TargetZoneSkipMs);
            return true;
        }
        if (_activeLeg == null || _activeLeg.Value.DistanceTo(legEnd) > 50f)
        {
            _activeLeg = legEnd;
            _travelBest = float.MaxValue;
            _travelBestAt = now;
        }
        float d = pos.DistanceTo(legEnd);
        if (d < _travelBest - 200f)
        {
            _travelBest = d;
            _travelBestAt = now;
        }
        bool blocked = World.FirstBlockingPublic(pos, legEnd) || (_routed && World.InThreat(legEnd));
        bool stalled = now - _travelBestAt > 60_000 && pos.DistanceTo(dest) > 1500f;
        if (blocked || stalled)
        {
            _travelAttempts++;
            if (_travelAttempts > Math.Max(1, Cfg.AvoidAttempts))
            {
                Log.Info($"[{Id}] route: {_travelAttempts - 1} attempts failed; skipping target for 5 min");
                GiveUpTravel(now);
                return true;
            }
            Log.Info($"[{Id}] route: {(blocked ? "path blocked by enemy zone" : "no progress 60 s")}, re-routing (attempt {_travelAttempts})");
            _travelBestAt = now;
            if (_travelShip != null && State == MinerState.Mining)
            {
                IssueSalvage(game, _travelShip, now);
            }
            else if (State == MinerState.Trading)
            {
                // Re-route the trade leg explicitly (reacquisition would keep the blocked move); no route = station cools down.
                if (!IssueTravel(game, dest, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), dest, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true, null))
                {
                    EndTrade(now, true, L.T("MiningDrone/RouteBlocked"));
                }
                else
                {
                    _tradeLegAt = _lastIssueAt;
                }
            }
            else
            {
                SetState(MinerState.FindTarget, L.T("MiningDrone/Replanning"));
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Escape episode of an emergency policy (Avoidance; self-defence evade, which never takes over another escape):
    /// starts when the ship is inside an enemy range (World.ThreatZones built by the caller), then EscapeTick runs it
    /// to its end. True while it owns movement.
    /// </summary>
    private bool EscapeEpisode(GameRoot game, long now, ModuleType by)
    {
        Vector2 pos = Ship.WorldCenter;
        bool evading = State == MinerState.Evading && _evadeBy == by;
        if (!evading && (by == ModuleType.Avoidance || State != MinerState.Evading) && InDanger(pos, out string why))
        {
            Log.Info($"[{Id}] {why} ({World.ThreatZones.Count} zones): evading by {by} (speed {World.MinerSpeed(Ship):0}/s)");
            EndStuck();
            _escFrom = State;
            _esc = default;
            _escPlanAt = 0;
            _escSafeSince = now;
            _evadeBy = by;
            SetOwner(by, L.T("MiningDrone/PreemptZone"));
            SetState(MinerState.Evading, L.T("MiningDrone/EvadingZone"));
            evading = true;
        }
        if (evading)
        {
            EscapeTick(game, now, pos);
        }
        return evading;
    }

    /// <summary>Escape trigger over World.ThreatZones: inside a range now or entering one within Escape.Horizon
    /// (Escape.Danger). Exit hysteresis is the escape's own: SafeMargin past every circle for EscapeSafeMs.</summary>
    private bool InDanger(Vector2 pos, out string why)
    {
        why = "";
        Vector2 vel = Ship.Physics.Body is { } b ? (Vector2)b.LinearVelocity : default;
        Escape.Threat[] t = Threats();
        int hit = -1;
        float best = float.PositiveInfinity;
        for (int i = 0; i < t.Length; i++)
        {
            float e = Escape.EntryTime(pos, vel, t[i]);
            if (e < best)
            {
                best = e;
                hit = i;
            }
        }
        if (hit < 0 || best > Escape.Horizon)
        {
            return false;
        }
        Vector2 r = t[hit].Pos - pos, w = t[hit].Vel - vel;
        float closing = r.Length > 1f ? -(r.X * w.X + r.Y * w.Y) / r.Length : 0f;
        why = best <= 0f ? $"inside enemy range (dist {r.Length:0}, radius {t[hit].R:0})"
            : $"enemy range entry in {best:0.0} s (dist {r.Length:0}, radius {t[hit].R:0}, closing {closing:0}/s)";
        return true;
    }

    /// <summary>Enemy zones (not stuck obstacles) as escape threats, same index as World.ThreatZones.</summary>
    private static Escape.Threat[] Threats()
    {
        int n = Math.Min(World.ThreatZones.Count, World.ThreatVels.Count);
        var threats = new Escape.Threat[n];
        for (int i = 0; i < n; i++)
        {
            threats[i] = new Escape.Threat(World.ThreatZones[i].Center, World.ThreatVels[i], World.ThreatZones[i].Radius);
        }
        return threats;
    }

    /// <summary>Log text: the zone nearest to p (distance to its center, radius).</summary>
    private static string ZoneInfo(Vector2 p)
    {
        int k = -1;
        for (int i = 0; i < World.ThreatZones.Count; i++)
        {
            if (k < 0 || p.DistanceTo(World.ThreatZones[i].Center) - World.ThreatZones[i].Radius < p.DistanceTo(World.ThreatZones[k].Center) - World.ThreatZones[k].Radius)
            {
                k = i;
            }
        }
        return k < 0 ? "no zone" : $"zone dist {p.DistanceTo(World.ThreatZones[k].Center):0} radius {World.ThreatZones[k].Radius:0}";
    }

    private Escape.State _esc;
    private MinerState _escFrom;
    private long _escPlanAt, _escLastPlan, _escSafeSince;
    private Vector2 _escDest, _escIssued;
    private const long EscapePlanMs = 500, EscapeSafeMs = 5000;
    private const float EscapeLead = 4000f, EscapeReissueDeg = 8f, EscapeReissueNear = 2000f;

    /// <summary>
    /// Avoidance escape (replaces "fixed 16-way point, leave after 3 s outside the circle", which re-entered the zone
    /// toward the old goal every few seconds = zigzag). Re-plans every 500 ms of game time with Escape.Step (pursuit-aware,
    /// committed side, turn-limited) and re-issues the lead waypoint only when the heading moved ≥8°, the waypoint is
    /// near or the command ended (≤2 inputs/s). Leaves only after 5 s of predicted safety; if the interrupted leg would
    /// run back into a pursuer, that target is given up instead of resumed.
    /// </summary>
    private void EscapeTick(GameRoot game, long now, Vector2 pos)
    {
        if (!EscapePlan(game, now, pos, out Escape.Threat[] threats, out Vector2 vel, out bool safe, out float dt))
        {
            return;
        }
        if (safe && now - _escSafeSince > EscapeSafeMs)
        {
            if (_travelDest is Vector2 dest && !Escape.LegSafe(pos, vel, dest, threats))
            {
                Log.Info($"[{Id}] escape: clear, but the interrupted leg runs back into a pursuer; dropping that target");
                SetOwner(null, "");
                GiveUpTravel(now, _escFrom);
                return;
            }
            Log.Info($"[{Id}] escape: no predicted intercept for {EscapeSafeMs / 1000} s; resuming");
            SetOwner(null, "");
            SetState(MinerState.FindTarget, L.T("MiningDrone/ThreatGone"));
            return;
        }
        EscapeSteer(game, now, pos, dt, safe, threats.Length);
    }

    /// <summary>One 500 ms escape re-plan (Escape.Step over World.ThreatZones/ThreatVels); false between re-plans.
    /// Tracks _escSafeSince (reset while any intercept is predicted).</summary>
    private bool EscapePlan(GameRoot game, long now, Vector2 pos, out Escape.Threat[] threats, out Vector2 vel, out bool safe, out float dt)
    {
        threats = Array.Empty<Escape.Threat>();
        vel = default;
        safe = false;
        dt = 0f;
        if (now < _escPlanAt)
        {
            return false;
        }
        dt = _escPlanAt == 0 ? 0f : (now - _escLastPlan) / 1000f;
        _escLastPlan = now;
        _escPlanAt = now + EscapePlanMs;
        threats = Threats();
        vel = Ship.Physics.Body is { } body ? (Vector2)body.LinearVelocity : default;
        Ship? home = ResolveBase(game);
        Vector2 homeDir = default;
        if (home != null && home.WorldCenter.DistanceTo(pos) > 1f)
        {
            homeDir = (home.WorldCenter - pos) * (1f / home.WorldCenter.DistanceTo(pos));
        }
        int side = _esc.Side;
        _esc = Escape.Step(_esc, pos, vel, threats, homeDir, dt, out safe);
        if (side != 0 && side != _esc.Side)
        {
            Log.Info($"[{Id}] escape: switching side (committed side predicted unsafe twice)");
        }
        if (!safe)
        {
            _escSafeSince = now;
        }
        return true;
    }

    /// <summary>Issues the escape lead waypoint when the heading moved ≥8°, the waypoint is near or the command ended.</summary>
    private void EscapeSteer(GameRoot game, long now, Vector2 pos, float dt, bool safe, int n)
    {
        bool ended = Ship.Commands.CurDisplayedCommand == null && now - _lastIssueAt > IssueGraceMs;
        if (dt == 0f || ended || MathF.Abs(Escape.Angle(_escIssued, _esc.Heading)) * 180f / MathF.PI >= EscapeReissueDeg || pos.DistanceTo(_escDest) < EscapeReissueNear)
        {
            _escIssued = _esc.Heading;
            _escDest = pos + _esc.Heading * EscapeLead;
            Log.Debug(Id, $"escape: heading {MathF.Atan2(_esc.Heading.Y, _esc.Heading.X) * 180f / MathF.PI:0}° side {_esc.Side} safe {safe} ({n} threats)");
            Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), _escDest, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true);
        }
    }

    private bool _retreatHome;

    /// <summary>
    /// Retreat policy, every tick while it owns movement: the predictive escape arc (shared with Avoidance, threats
    /// built here even when Avoidance is off) until no intercept is predicted for 5 s, then the leg home once its first
    /// stretch is predicted clear; back to escaping when a threat closes in again. Without a safe leg home it holds.
    /// Ending the episode stays with the caller (enemy off radar for the resume delay).
    /// </summary>
    private void RetreatTick(GameRoot game, long now)
    {
        if (now < _escPlanAt)
        {
            return;
        }
        World.BuildThreatZones(game.Sim, Ship, now);
        Vector2 pos = Ship.WorldCenter;
        EscapePlan(game, now, pos, out Escape.Threat[] threats, out Vector2 vel, out bool safe, out float dt);
        if (!safe || now - _escSafeSince <= EscapeSafeMs)
        {
            if (_retreatHome)
            {
                Log.Info($"[{Id}] retreat: predicted intercept on the way home; escaping again");
                _retreatHome = false;
                dt = 0f; // force a fresh escape waypoint over the follow
            }
            EscapeSteer(game, now, pos, dt, safe, threats.Length);
            return;
        }
        Ship? home = ResolveBase(game);
        // IssueFollow may only hold at a safe point (no route / base inside enemy range) and asks for a retry.
        bool retry = _retreatHome && _followRetryAt != 0 && now >= _followRetryAt;
        if ((!_retreatHome || retry) && home != null && Escape.LegSafe(pos, vel, home.WorldCenter, threats))
        {
            Log.Info($"[{Id}] retreat: clear of every pursuer; {(retry ? "retrying the leg" : "heading")} home to {home.Metadata.PlainShipName}");
            _retreatHome = true;
            _followRetryAt = 0;
            IssueFollow(game, home, new FollowCommand(Ship, Ship.Commands.AllocLocalCommandID(), home, CollisionAvoidanceFlags.All, isTentative: false) { EdgeToEdgeFollowRadius = 60f }, "retreat");
            SetState(MinerState.Evading, RetreatStatus(home: true));
        }
    }

    private const long TargetZoneSkipMs = 60_000; // target fell into an enemy range: retry soon, the fight may move on

    private void GiveUpTravel(long now, MinerState? task = null, long ms = AvoidBlacklistMs)
    {
        if ((task ?? State) == MinerState.Trading)
        {
            EndTrade(now, true, L.T("MiningDrone/RouteBlocked")); // the station cools down; another one is tried
            return;
        }
        if (_travelShip != null)
        {
            Blacklist(_travelShip, now, ms);
        }
        else if (_nugget != null && (task ?? State) == MinerState.CollectLoose)
        {
            _nuggetBlacklist[_nugget] = now + ms;
        }
        else if (_travelDest is Vector2 td)
        {
            _stasisSkip[td] = now + ms;
        }
        _travelDest = null;
        _travelShip = null;
        Target = null;
        SetState(MinerState.FindTarget, L.T("MiningDrone/RouteBlocked"));
    }
}
