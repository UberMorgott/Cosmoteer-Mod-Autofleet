using System;
using System.Collections.Generic;
using Cosmoteer;
using Cosmoteer.Game;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Parts.Weapons;
using Halfling.Geometry;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>Stuck detection/recovery (shared movement layer) and save provenance (capture + reconcile).</summary>
internal sealed partial class ShipAgent
{
    // ------------------------------------------------------------------ stuck detection / recovery

    private const long StuckWindowMs = 8000;
    private const float StuckMinMove = 50f;
    private const float UnstuckDistance = 300f;
    private const long StepMs = 6000;
    private const long HelpRetryMs = 30_000;
    private const CollisionAvoidanceFlags RecoveryAvoid = CollisionAvoidanceFlags.Doodads | CollisionAvoidanceFlags.Nebulas;

    private bool _stuckActive;
    private int _stuckStep;
    private int _stuckCycles;
    private Vector2 _stuckRef;
    private long _stuckRefAt;
    private Vector2 _stuckStart;
    private long _stepAt;
    private Ship? _stuckBody;
    private Vector2 _moveDir;
    private MinerState _stuckFrom;
    private bool _stuckHelp;

    /// <summary>How far the ship is from where its follow wants it: the slot point of a slotted follow (formation), else
    /// the hull gap to the followed ship.</summary>
    private float FollowGap(Command c, Ship followed) => c is FollowCommand fc && fc.FollowAngle.HasValue && fc.EdgeToEdgeFollowRadius.HasValue
        ? Ship.WorldCenter.DistanceTo(fc.GetDestination().Location)
        : Ship.WorldBoundingCircle.DistanceTo(followed.WorldBoundingCircle);

    /// <summary>Ship is on a travel leg of ours (move/follow/approach), where standing still means it is stuck.</summary>
    private bool Travelling(GameRoot game)
    {
        Command? c = Ship.Commands.CurDisplayedCommand;
        if (c == null || !IsOurCommand(c) || (c is MoveCommand && c.IsSatisfied()))
        {
            return false;
        }
        switch (State)
        {
            case MinerState.Approach:
                return true;
            case MinerState.FlyToBase:
                return _base == null || Ship.WorldBoundingCircle.DistanceTo(_base.WorldBoundingCircle) > GameApp.Rules.Crew.LoseHomeShipWarningDistance * 0.5f;
            case MinerState.CollectLoose:
                return c is MoveCommand;
            case MinerState.Mining:
                return _resalvageAfterMove || (Target != null && c is SalvageCommand && Ship.WorldBoundingCircle.DistanceTo(Target.WorldBoundingCircle) > 400f);
            case MinerState.Holding:
                return _followShip != null && FollowGap(c, _followShip) > 2000f;
            case MinerState.Trading:
                return c is MoveCommand || (c is FollowCommand && _followShip != null && FollowGap(c, _followShip) > 2000f);
            case MinerState.Evading:
                return c is MoveCommand || (c is FollowCommand && _followShip != null && FollowGap(c, _followShip) > 2000f);
            default:
                return false;
        }
    }

    /// <summary>
    /// Progress below 50 units over 8 s of game time on a travel leg = stuck. Recovery escalates: reverse, sidestep
    /// left/right, move off the touching body, strip it (salvage) / shoot it (attack), reverse + sidestep again,
    /// re-plan with the body as an avoidance circle, finally give up the target. Inputs only (lockstep-safe).
    /// </summary>
    private bool StuckTick(GameRoot game, long now)
    {
        Vector2 pos = Ship.WorldCenter;
        if (!_stuckActive)
        {
            if (!Travelling(game))
            {
                _stuckRef = pos;
                _stuckRefAt = now;
                return false;
            }
            float moved = pos.DistanceTo(_stuckRef);
            if (moved >= StuckMinMove)
            {
                _moveDir = (pos - _stuckRef) * (1f / moved);
                _stuckRef = pos;
                _stuckRefAt = now;
                return false;
            }
            if (now - _stuckRefAt < StuckWindowMs)
            {
                return false;
            }
            _stuckActive = true;
            _stuckStep = 0;
            _stuckStart = pos;
            _stepAt = now - StepMs; // first step right away
            _stuckFrom = State;
            _stuckBody = World.TouchingBody(game.Sim, Ship);
            _stuckCycles++;
            Log.Info($"[{Id}] stuck: moved <{StuckMinMove:0} in {StuckWindowMs / 1000} s during {State} (owner {Owner?.ToString() ?? "-"}); touching {(_stuckBody == null ? "nothing found" : _stuckBody.Metadata.PlainShipName + (_stuckBody.IsJunk ? " (junk)" : ""))}; cycle {_stuckCycles}");
        }
        // Free = moved clear of the start AND no longer touching the body we were wedged in (a slide along it is not free).
        if (pos.DistanceTo(_stuckStart) >= UnstuckDistance && (_stuckBody == null || World.TouchingBody(game.Sim, Ship) != _stuckBody))
        {
            Log.Info($"[{Id}] stuck: free after step {_stuckStep} ({pos.DistanceTo(_stuckStart):0} moved); re-planning");
            RememberObstacle(120_000);
            EndStuck();
            _stuckCycles = 0;
            ResumeAfterStuck(L.T("MiningDrone/Unstuck"));
            return true;
        }
        long wait = _stuckHelp ? HelpRetryMs : StepMs;
        if (now - _stepAt < wait)
        {
            return true;
        }
        _stepAt = now;
        _stuckStep++;
        RunStuckStep(game, now, pos);
        return true;
    }

    private void RunStuckStep(GameRoot game, long now, Vector2 pos)
    {
        if (_stuckBody != null && !_stuckBody.ExistsInSim(game.Sim))
        {
            // Stripped, split or despawned meanwhile (another ship, our own salvage): re-detect what we touch now.
            _stuckBody = World.TouchingBody(game.Sim, Ship);
        }
        float r = Ship.WorldBoundingRadius;
        Vector2 away = AwayFromBody(pos);
        Vector2 perp = new Vector2(-away.Y, away.X);
        switch (_stuckStep)
        {
            case 1:
            case 7:
                {
                    Vector2 back = _moveDir.LengthSquared > 0.5f ? _moveDir * -1f : away;
                    StuckMove(game, pos + back * (r * 1.5f + 200f), "reverse");
                    return;
                }
            case 2:
            case 8:
                StuckMove(game, pos + perp * (r + 300f) + away * 100f, "sidestep left");
                return;
            case 3:
                StuckMove(game, pos - perp * (r + 300f) + away * 100f, "sidestep right");
                return;
            case 4:
                if (_stuckBody != null)
                {
                    Circle bc = _stuckBody.WorldBoundingCircle;
                    Vector2 dir = pos - bc.Center;
                    float len = dir.Length;
                    dir = len > 1f ? dir * (1f / len) : away;
                    StuckMove(game, bc.Center + dir * (bc.Radius + r + 300f), "move off " + _stuckBody.Metadata.PlainShipName);
                    return;
                }
                _stuckStep++;
                goto case 5;
            case 5:
                // Salvaging / shooting take long: an escape that got stuck only uses the short moves.
                if (_stuckFrom != MinerState.Evading && _stuckBody != null && _stuckBody.IsJunk && _stuckBody.CanBeSalvagedByLocalPlayer(allowSalvageOwnShip: false))
                {
                    // Crew and mining beams strip the parts within reach: those are the ones wedging the hull.
                    Log.Info($"[{Id}] stuck step 5: salvaging the body we are wedged in ({_stuckBody.Metadata.PlainShipName})");
                    Issue(game, new SalvageCommand(Ship, Ship.Commands.AllocLocalCommandID(), _stuckBody, BuildAutoCollect(), CollisionAvoidanceFlags.None, isTentative: false, waitForCrew: false), autoCancelSalvage: false);
                    SetState(MinerState.Stuck, L.F("MiningDrone/StuckStep", 5, L.T("MiningDrone/StuckSalvage")));
                    return;
                }
                _stuckStep++;
                goto case 6;
            case 6:
                if (_stuckFrom != MinerState.Evading && _stuckBody != null && _stuckBody.IsJunk && Ship.Weapons.OperationalWeapons.Count > 0 && _stuckBody.CanBeAttackedBy(Ship))
                {
                    // Only junk (asteroids, wrecks): never auto-fire on a ship. AllowJunkTargets = target.IsJunk (AttackCommand.cs:160).
                    // rotationOnly only changes how the initial facing is computed; the attack can still move the ship.
                    Log.Info($"[{Id}] stuck step 6: shooting the body we are wedged in ({_stuckBody.Metadata.PlainShipName}) with {Ship.Weapons.OperationalWeapons.Count} weapon(s)");
                    Issue(game, new AttackCommand(Ship, Ship.Commands.AllocLocalCommandID(), _stuckBody, CollisionAvoidanceFlags.None, isTentative: false, isConfirmed: true, rotationOnly: true), autoCancelSalvage: false);
                    SetState(MinerState.Stuck, L.F("MiningDrone/StuckStep", 6, L.T("MiningDrone/StuckShoot")));
                    return;
                }
                if (_stuckBody != null)
                {
                    Log.Info($"[{Id}] stuck step 6: no salvage and no weapons usable on {_stuckBody.Metadata.PlainShipName}");
                }
                _stuckStep++;
                goto case 7;
            case 9:
                if (_stuckCycles < 2)
                {
                    Log.Info($"[{Id}] stuck step 9: re-planning with {(_stuckBody == null ? "no known obstacle" : _stuckBody.Metadata.PlainShipName + " as an avoidance circle")}");
                    RememberObstacle(300_000);
                    EndStuck();
                    ResumeAfterStuck(L.T("MiningDrone/StuckReplan"));
                    return;
                }
                _stuckStep++;
                goto case 10;
            case 10:
                GiveUpStuckTarget(now);
                return;
            default:
                // Help mode: wedged in a body we can neither strip nor shoot. Keep retrying the escape moves slowly.
                _stuckHelp = true;
                _stuckStep = 0;
                RuntimeBlocker = L.T("MiningDrone/StuckHelp");
                SetState(MinerState.Stuck, L.T("MiningDrone/StuckHelp"));
                Log.Info($"[{Id}] stuck: still wedged after all steps; needs help, retrying every {HelpRetryMs / 1000} s");
                return;
        }
    }

    private Vector2 AwayFromBody(Vector2 pos)
    {
        if (_stuckBody != null)
        {
            Vector2? part = World.NearestPartCenter(_stuckBody, pos);
            Vector2 from = part ?? _stuckBody.WorldBoundingCircle.Center;
            Vector2 d = pos - from;
            float len = d.Length;
            if (len > 1f)
            {
                return d * (1f / len);
            }
        }
        if (_moveDir.LengthSquared > 0.5f)
        {
            return _moveDir * -1f;
        }
        return new Vector2(1f, 0f);
    }

    private void StuckMove(GameRoot game, Vector2 dest, string what)
    {
        Log.Info($"[{Id}] stuck step {_stuckStep}: {what} to {dest.DistanceTo(Ship.WorldCenter):0} away");
        Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), dest, null, RecoveryAvoid, isTentative: false), autoCancelSalvage: true);
        SetState(MinerState.Stuck, L.F("MiningDrone/StuckStep", _stuckStep, what));
    }

    private void RememberObstacle(long ms)
    {
        if (_stuckBody != null && _stuckBody.ExistsInSim(_game!.Sim) && !ReferenceEquals(_stuckBody, Target))
        {
            Circle bc = _stuckBody.WorldBoundingCircle;
            _obstacles.Add((new Circle(bc.Center, bc.Radius + Ship.WorldBoundingRadius + 200f), _now + ms));
        }
    }

    private void GiveUpStuckTarget(long now)
    {
        string what = "none";
        if (Target != null)
        {
            Blacklist(Target, now, AvoidBlacklistMs);
            what = Target.Metadata.PlainShipName;
        }
        else if (_nugget != null && _stuckFrom == MinerState.CollectLoose)
        {
            _nuggetBlacklist[_nugget] = now + AvoidBlacklistMs;
            what = "loose resource";
        }
        else if (_stuckFrom == MinerState.Approach)
        {
            SkipStasis(_approachDest, now);
            what = "stasis rock";
        }
        Log.Info($"[{Id}] stuck step 10: giving up target ({what}) for 5 min");
        RememberObstacle(300_000);
        bool body = _stuckBody != null && World.TouchingBody(_game!.Sim, Ship) == _stuckBody;
        EndStuck();
        Target = null;
        if (body)
        {
            // Still wedged: next cycle starts in help mode right away.
            _stuckActive = true;
            _stuckStart = Ship.WorldCenter;
            _stepAt = _now;
            _stuckStep = 10;
            _stuckHelp = true;
            RuntimeBlocker = L.T("MiningDrone/StuckHelp");
            SetState(MinerState.Stuck, L.T("MiningDrone/StuckHelp"));
            return;
        }
        SetOwner(null, "");
        SetState(MinerState.FindTarget, L.T("MiningDrone/StuckGaveUp"));
    }

    /// <summary>Back to planning; an interrupted escape (Avoidance / Retreat) resumes its episode with a fresh waypoint.</summary>
    private void ResumeAfterStuck(string status)
    {
        if (_stuckFrom == MinerState.Evading)
        {
            _escPlanAt = 0;
            _retreatHome = false;
            SetState(MinerState.Evading, status);
            return;
        }
        SetState(MinerState.FindTarget, status);
    }

    private void SkipStasis(Vector2 loc, long now) => _stasisSkip[loc] = now + AvoidBlacklistMs;

    private void EndStuck()
    {
        if (_stuckHelp)
        {
            RuntimeBlocker = "";
        }
        _stuckActive = false;
        _stuckHelp = false;
        _stuckStep = 0;
        _stuckRef = Ship.WorldCenter;
        _stuckRefAt = _now;
    }

    // ------------------------------------------------------------------ provenance

    /// <summary>Runtime provenance written into the per-save snapshot (taken when the save starts).</summary>
    public RuntimeRecord Capture()
    {
        var r = new RuntimeRecord
        {
            Module = Owner?.ToString(),
            State = (int)(State == MinerState.Stuck ? _stuckFrom : State),
            TargetShipId = Target?.UniqueID.ToString(),
            ApproachX = _approachDest.X,
            ApproachKind = (int)_stasisKind,
            ApproachY = _approachDest.Y,
            DamageLatch = _damaged,
            StationId = State == MinerState.Trading ? _station?.ToString() : null,
        };
        if (_ownIds.Count > 0)
        {
            r.OwnCommands = new List<long[]>(_ownIds.Count);
            foreach (CommandID id in _ownIds)
            {
                r.OwnCommands.Add(Encode(id));
            }
        }
        if (_defIds.Count > 0)
        {
            r.DefTargets = new List<long[]>(_defIds.Count);
            foreach (CommandID id in _defIds.Values)
            {
                r.DefTargets.Add(Encode(id));
            }
        }
        return r;
    }

    private static long[] Encode(CommandID id) => new[] { id.PlayerID.HasValue ? 1L : 0L, id.PlayerID.HasValue ? (long)(ulong)id.PlayerID.Value : 0L, (long)id.SequenceNum };

    /// <summary>Re-adopts our self-defence weapon targets from the save, so pause / module off still removes them.</summary>
    private void ReconcileDefence(RuntimeRecord r)
    {
        if (r.DefTargets == null)
        {
            return;
        }
        foreach (Weapon w in Ship.Weapons) // all weapons: one out of order at load still holds our target
        {
            foreach (WeaponTarget t in w.ExplicitTargets)
            {
                if (Matches(r.DefTargets, t.UniqueID))
                {
                    _defIds[w] = t.UniqueID;
                    break;
                }
            }
        }
        _defLastHot = _now;
        Log.Info($"[{Id}] reconcile: adopted {_defIds.Count} self-defence weapon target(s)");
    }

    private static bool Matches(List<long[]> own, CommandID id)
    {
        long has = id.PlayerID.HasValue ? 1L : 0L;
        long pid = id.PlayerID.HasValue ? (long)(ulong)id.PlayerID.Value : 0L;
        for (int i = 0; i < own.Count; i++)
        {
            long[] o = own[i];
            if (o.Length == 3 && o[0] == has && o[1] == pid && o[2] == id.SequenceNum)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// After a load: before issuing anything, observe what the save already contains. Our own commands (by persisted
    /// CommandID) are adopted instead of re-issued; the module/state they belonged to resumes; a pending offload
    /// resumes without re-queuing (transfers are anticipation-aware); a manual order in the save stays manual.
    /// </summary>
    private void Reconcile(GameRoot game, RuntimeRecord r)
    {
        Store.Touch(Assignment); // provenance consumed
        Assignment.Runtime = null;
        ReconcileDefence(r);
        _damaged = r.DamageLatch;
        var st = (MinerState)r.State;
        if (State == MinerState.Paused)
        {
            Log.Info($"[{Id}] reconcile: paused by user in the save; staying paused");
            return;
        }
        ModuleType? owner = Enum.TryParse(r.Module, out ModuleType m) ? m : null;
        if (st == MinerState.Manual)
        {
            _manualQuietSince = _now;
            SetState(MinerState.Manual, L.T("MiningDrone/ManualControl"));
            Log.Info($"[{Id}] reconcile: manual control in the save; waiting for the player's orders to finish");
            return;
        }
        var matched = new List<Command>();
        if (r.OwnCommands != null)
        {
            List<Command> q = Ship.Commands._commands;
            for (int i = 0; i < q.Count; i++)
            {
                if (q[i].IsDisplayed && Matches(r.OwnCommands, q[i].UniqueID))
                {
                    matched.Add(q[i]);
                }
            }
        }
        if (matched.Count > 0)
        {
            ForgetCommand();
            foreach (Command c in matched)
            {
                _ownIds.Add(c.UniqueID);
            }
            _lastCmd = matched[0];
            _lastCmdId = matched[0].UniqueID;
            _expectType = matched[0].GetType();
            _ownSeen = true;
            _lastIssueAt = _now;
            _lastProgressAt = _now;
            if (matched[matched.Count - 1] is FollowCommand fc && fc.HasTarget)
            {
                _followShip = fc.Target;
                _followProto = fc;
                _followLeg = "reconciled";
            }
            _routed = matched.Count > 1;
            switch (st)
            {
                case MinerState.Mining:
                    Ship? t = r.TargetShipId == null ? null : World.FindShipById(game.Sim, r.TargetShipId);
                    if (t != null)
                    {
                        Target = t;
                        _travelShip = t;
                        _travelDest = t.WorldCenter;
                        SetOwner(owner ?? ModuleType.Mine, "");
                        SetState(MinerState.Mining, L.T("MiningDrone/Mining"));
                        Log.Info($"[{Id}] reconcile: adopted {matched.Count} own command(s); mining {t.Metadata.PlainShipName} continues");
                        return;
                    }
                    break;
                case MinerState.Approach when owner == ModuleType.Loot && r.ApproachKind == 0:
                    // Saved without its source kind: can't be checked against the toggles, so stop it and re-plan.
                    if (!HasForeignQueued())
                    {
                        Stop(game);
                    }
                    break;
                case MinerState.Approach:
                    _stasisKind = (World.JunkKind)r.ApproachKind;
                    _approachDest = new Vector2(r.ApproachX, r.ApproachY);
                    _travelDest = _approachDest;
                    SetOwner(owner ?? ModuleType.Mine, "");
                    SetState(MinerState.Approach, L.T("MiningDrone/Approaching"));
                    Log.Info($"[{Id}] reconcile: adopted {matched.Count} own command(s); approach continues");
                    return;
                case MinerState.FlyToBase:
                case MinerState.Holding:
                case MinerState.Offload:
                    SetOwner(owner ?? ModuleType.Offload, "");
                    _nextActionAt = st == MinerState.Holding ? _now + 3000 : 0;
                    SetState(st, st == MinerState.Offload ? L.T("MiningDrone/Offloading") : L.T("MiningDrone/Reconciled"));
                    Log.Info($"[{Id}] reconcile: adopted {matched.Count} own command(s); {st} continues");
                    return;
                case MinerState.Trading when ReconcileTrade(game, r):
                    return;
                case MinerState.Evading when owner is ModuleType.Guard or ModuleType.Hunt && matched[0] is AttackCommand ac && ac.HasTarget:
                    // A saved fight resumes under its module, so Hunt's leash and Guard's resume delay keep applying.
                    _enemy = ac.Target;
                    _evadeBy = owner.Value;
                    _lastEnemySeen = _now;
                    SetOwner(owner, "");
                    SetState(MinerState.Evading, L.F(owner == ModuleType.Hunt ? "MiningDrone/Hunting" : "MiningDrone/Attacking", ac.Target.Metadata.PlainShipName));
                    Log.Info($"[{Id}] reconcile: adopted own attack on {ac.Target.Metadata.PlainShipName}; {owner} continues");
                    return;
            }
            Log.Info($"[{Id}] reconcile: adopted {matched.Count} own command(s); re-planning from {st}");
            SetState(MinerState.FindTarget, L.T("MiningDrone/Reconciled"));
            return;
        }
        if (st == MinerState.Trading && ReconcileTrade(game, r))
        {
            return;
        }
        if (st == MinerState.Offload)
        {
            SetOwner(ModuleType.Offload, "");
            _nextActionAt = 0;
            SetState(MinerState.Offload, L.T("MiningDrone/Offloading"));
            Log.Info($"[{Id}] reconcile: offload was in progress; resuming it (pending transfers are counted, not re-queued)");
            return;
        }
        Log.Info($"[{Id}] reconcile: no own command from the save is active ({st}); planning fresh");
    }
}
