using System;
using System.Collections.Generic;
using Cosmoteer;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Parts.Weapons;
using Cosmoteer.Simulation;
using Halfling.Geometry;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>Activity of the movement owner (also drives icons, list and status).</summary>
internal enum MinerState
{
    FindTarget,
    Mining,
    FlyToBase,
    Offload,
    Waiting,
    Evading,
    CollectLoose,
    Approach,
    Holding,
    Manual,
    Paused,
    Stuck,
    Trading,
}

/// <summary>
/// Per-ship executor and arbiter. Module handlers (Mine, Loot, Offload, Anchor-link, Avoidance, Retreat, Guard)
/// propose intents; exactly one module owns movement at a time (<see cref="Owner"/>). Arbitration order: manual
/// orders / user pause, then emergency policies (Avoidance escape, Retreat), then temporary intents (unload now,
/// return home), then task modules by priority (ties: lower cost, then stable instance order), then idle intents.
/// Every action goes through NetManager.EnqueueInput (MP lockstep safe); only the owner's client runs this.
/// </summary>
internal sealed partial class ShipAgent
{
    private const long IssueGraceMs = 1500;
    private const long NoProgressTimeoutMs = 120_000;
    private const long BlacklistMs = 90_000;
    private const long WaitRetryMs = 5000;
    private const long OffloadRequeueMs = 4000;
    private const long OffloadStallMs = 60_000;
    private const long EnemyCheckMs = 1000;
    private const long OwnCommandTimeoutMs = 6000;

    private readonly Dictionary<Ship, long> _blacklist = new();
    private readonly HashSet<Ship> _exclude = new();
    private ulong _fitMask; // selected types that still have room (Arbitrate)
    private readonly List<Ship> _expired = new();
    private readonly Dictionary<Nugget, long> _nuggetBlacklist = new();
    private readonly List<Nugget> _expiredNuggets = new();
    private Nugget? _nugget;
    private int _lastSig = -1;
    private long _baseRetryAt;
    private Vector2 _approachDest;
    private Vector2? _stasisPick;
    private World.JunkKind _stasisKind;
    private string? _lastDiag;
    private string _noSelection = ""; // why Arbitrate skipped a harvest search (empty collect selection)
    private long _lastDiagAt;
    private long _manualQuietSince;

    private readonly HashSet<CommandID> _ownIds = new();
    private long _now; // simulation logical time (ms) of the current tick; stops while the game is paused

    private Command? _lastCmd;
    private CommandID _lastCmdId;
    private Type? _expectType;
    private Ship? _expectTarget;
    private bool _ownSeen;
    private long _lastIssueAt;
    private long _nextActionAt;
    private int _lastStored;
    private long _lastProgressAt;
    private long _nextEnemyCheck;
    private long _lastEnemySeen;
    private Ship? _enemy;
    private Ship? _base;
    private string? _baseResolvedFor;
    private ulong _mask;
    private ulong _offMask;
    private ModuleType _evadeBy;
    private RuntimeRecord? _reconcile;
    private GameRoot? _game;

    public ShipAgent(Ship ship, string id, ShipAssignment assignment)
    {
        Ship = ship;
        Id = id;
        Assignment = assignment;
        Resolver.Refresh(Cfg, assignment);
        _reconcile = assignment.Runtime;
        if (assignment.UserPaused)
        {
            SetState(MinerState.Paused, L.T("MiningDrone/PausedUser"));
        }
        else
        {
            SetState(MinerState.FindTarget, L.T("MiningDrone/Starting"));
        }
    }

    public Ship Ship { get; }
    public string Id { get; }
    public ShipAssignment Assignment { get; }
    public AgentConfig Cfg { get; } = new();
    public MinerState State { get; private set; }
    public string Status { get; private set; } = "";
    public Ship? Target { get; private set; }

    /// <summary>Hold fill used for the threshold and shown everywhere: cell fill, or 100% when the hold physically
    /// accepts no more units, so the trigger and the displayed % always agree.</summary>
    public float Fill { get; private set; }
    public int UsedCells { get; private set; }
    public int AvailCells { get; private set; }

    /// <summary>The module that owns movement now, or null while re-planning / idle.</summary>
    public ModuleType? Owner { get; private set; }
    private long _ownerSince;

    /// <summary>Why the current owner took over from the previous one (empty if nothing was preempted).</summary>
    public string Preempt { get; private set; } = "";

    /// <summary>Runtime blocker (missing anchor, stuck...), in addition to the config validation blockers.</summary>
    public string RuntimeBlocker { get; private set; } = "";

    /// <summary>True while holding/waiting because no target qualifies (drives the world "!" marker and the notice).</summary>
    public bool NoTargets { get; private set; }
    public string NoTargetsReason { get; private set; } = "";
    private int _cellSig = -1;

    /// <summary>Anchor ship (any valid own or allied ship), or null.</summary>
    public Ship? AnchorShip { get; private set; }

    // ------------------------------------------------------------------ external controls

    public void Resume()
    {
        ForgetCommand();
        if (Assignment.UserPaused)
        {
            Assignment.UserPaused = false;
            Store.Touch(Assignment);
        }
        SetOwner(null, "");
        SetState(MinerState.FindTarget, L.T("MiningDrone/Resumed"));
    }

    /// <summary>Explicit user pause (distinct from the manual-order auto-pause): stop the ship and wait for Resume.</summary>
    public void PauseByUser(GameRoot game)
    {
        if (Ship.ExistsInSim(game.Sim))
        {
            Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), Ship.WorldCenter, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true);
        }
        ForgetCommand();
        Assignment.UserPaused = true;
        Store.Touch(Assignment);
        SetOwner(null, "");
        SetState(MinerState.Paused, L.T("MiningDrone/PausedUser"));
    }

    /// <summary>Temporary intent: abort the current job, fly to the offload target, unload fully, then carry on.
    /// Survives interrupts (enemy, manual control) and save/load until the offload completes.</summary>
    public void UnloadNow(GameRoot game)
    {
        Assignment.Temp = TempIntent.UnloadNow;
        Store.Touch(Assignment);
        Resolver.Refresh(Cfg, Assignment);
        _lastSig = PlanSig();
        if (State == MinerState.Paused || State == MinerState.Manual)
        {
            return; // picked up by arbitration on resume
        }
        ForgetCommand();
        Target = null;
        GoToBase(game, _now, L.T("MiningDrone/UnloadNowReason"));
    }

    private void ForgetCommand()
    {
        _lastCmd = null;
        _lastCmdId = default;
        _expectType = null;
        _expectTarget = null;
        _ownSeen = false;
        _ownIds.Clear();
    }

    private static string TargetName(Command c) =>
        c is BaseFollowCommand f && f.HasTarget ? f.Target.Metadata.PlainShipName : "-";

    /// <summary>Called when the user unassigns: drop our command so the ship stops.</summary>
    public void Stop(GameRoot game)
    {
        if (Ship.ExistsInSim(game.Sim) && IsOurCommand(Ship.Commands.CurDisplayedCommand))
        {
            Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), Ship.WorldCenter, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true);
        }
    }

    /// <summary>Plan-relevant shape of the config: a change re-plans (like the old task switch).</summary>
    private int PlanSig()
    {
        int s = (int)Cfg.Temp;
        // Full scope (zone / nearest / anchor): a live profile edit of where to work re-plans at once.
        if (Cfg.Mine.Active)
        {
            s |= (int)Cfg.MineScope << 12;
        }
        if (Cfg.Loot.Active)
        {
            s |= (int)Cfg.LootScope << 14;
        }
        if (Cfg.Anchored)
        {
            s |= 16;
        }
        // Module activity: switching a module off (or a newer-version blocker) must stop it, not let it run on.
        if (Cfg.Mine.Active) s |= 32;
        if (Cfg.Loot.Active) s |= 64;
        if (Cfg.Offload.Active) s |= 128;
        if (Cfg.Anchor.Active) s |= 256;
        if (Cfg.Guard.Active) s |= 512;
        if (Cfg.Retreat.Active) s |= 1024;
        if (Cfg.Avoid.Active) s |= 2048;
        if (Cfg.FollowIdle) s |= 1 << 16; // bits 12-15 hold the scopes
        if (Cfg.Defend.Active) s |= 1 << 17;
        if (Cfg.DefendEvade) s |= 1 << 18;
        if (Cfg.Hunt.Active) s |= 1 << 19;
        if (Cfg.RetreatTrigger == 1) s |= 1 << 20;
        if (Cfg.Trade.Active) s |= 1 << 21;
        if (Cfg.TradeTravel) s |= 1 << 22;
        return s;
    }

    /// <summary>Any displayed command in the queue that is neither ours nor game-generated (a player order).</summary>
    private bool HasForeignQueued()
    {
        List<Command> q = Ship.Commands._commands;
        for (int i = 0; i < q.Count; i++)
        {
            if (q[i].IsDisplayed && !_ownIds.Contains(q[i].UniqueID) && q[i].UniqueID.PlayerID != null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The current movement owner is no longer an active module (temp intents keep Offload alive).</summary>
    private bool OwnerInactive() => Owner switch
    {
        ModuleType.Mine => !Cfg.Mine.Active,
        ModuleType.Loot => !Cfg.Loot.Active,
        ModuleType.Offload => !Cfg.Offload.Active && Cfg.Temp == TempIntent.None,
        ModuleType.AnchorLink => !Cfg.Anchor.Active || !Cfg.FollowIdle, // it owns movement only for the idle escort
        ModuleType.Guard => !Cfg.Guard.Active,
        ModuleType.Retreat => !Cfg.Retreat.Active,
        ModuleType.Avoidance => !Cfg.Avoid.Active,
        ModuleType.SelfDefence => !DefendEvadeOn,
        ModuleType.Hunt => !Cfg.Hunt.Active,
        ModuleType.Trade => !Cfg.Trade.Active,
        _ => false,
    };

    // ------------------------------------------------------------------ tick

    public void Tick(GameRoot game, long now)
    {
        _now = now;
        _game = game;
        SimRoot sim = game.Sim;
        Resolver.Refresh(Cfg, Assignment);
        if (ReferenceEquals(Assignment.Profile, Store.Missing))
        {
            // Unknown profile (old sidecar / deleted while this ship was elsewhere): no logic at all, temp intents
            // and bindings included. Stop our own command once and hold until a profile is picked.
            if (!_noProfile)
            {
                _noProfile = true;
                if (_reconcile != null)
                {
                    RuntimeRecord r0 = _reconcile;
                    _reconcile = null;
                    Reconcile(game, r0); // adopt our saved command ids first, so Stop recognises them
                }
                if (Ship.ExistsInSim(sim) && !HasForeignQueued())
                {
                    Stop(game);
                }
                ForgetCommand();
                Target = null;
                SetOwner(null, "");
                SetState(MinerState.Paused, L.T("MiningDrone/BlockNoProfile"));
            }
            return;
        }
        if (_noProfile)
        {
            _noProfile = false;
            if (!Assignment.UserPaused)
            {
                Resume();
            }
        }
        RefreshMask();
        _maxMask = Logistics.AtMaxMask(Ship, Assignment.Cargo, _mask);
        // Cargo = offloadable types above their keep-on-board amounts (vanilla resource desire).
        _offMask = World.OffloadMask(Cfg.KeepAll);
        World.CargoFill(Ship, _offMask, out int stored, out int remaining);
        // Fill shown and used for the threshold = cargo-bay cells (what the player sees); a hold that physically
        // takes no more units counts as full too, so the trigger and the displayed % always agree.
        World.CargoCells(Ship, _offMask, out int usedCells, out int totalCells, out int reservedCells);
        int avail = totalCells - reservedCells;
        float cellFill = avail > 0 ? Math.Min(1f, (float)usedCells / avail) : (totalCells > 0 ? 1f : 0f);
        Fill = stored > 0 && remaining <= 0 ? 1f : cellFill;
        UsedCells = usedCells;
        AvailCells = avail;
        int sig = usedCells * 1_000_000 + totalCells * 1000 + reservedCells;
        if (sig != _cellSig)
        {
            _cellSig = sig;
            Log.Debug(Id, $"cargo cells: used {usedCells} / available {avail} (total {totalCells}, reserved for keep {reservedCells}); offloadable units above keep {stored}, unit space left {remaining}; fill {Fill * 100f:0}%");
        }
        if (stored > _lastStored)
        {
            _lastProgressAt = now;
        }
        _lastStored = stored;

        if (_reconcile != null)
        {
            RuntimeRecord r = _reconcile;
            _reconcile = null;
            Reconcile(game, r);
            // The restored owner came from the save, the config is the live profile: if that module is off now,
            // force the re-plan below so its command is stopped instead of resumed.
            _lastSig = OwnerInactive() ? int.MinValue : PlanSig();
            _planMask = OwnSalvageStale() ? ~Harvest : Harvest; // saved with another Collect filter: re-plan below
        }

        // Collect filter changed (live profile edit): the running salvage keeps its old auto-collect set, so a
        // harvesting owner stops its own command and re-plans with the new one.
        bool collectChanged = _planMask is ulong pm && pm != Harvest;
        _planMask = Harvest;
        int plan = PlanSig();
        if (plan != _lastSig || collectChanged)
        {
            // Work scope changed (bits 12-15): the current target may no longer qualify, so harvesting is dropped too.
            bool scopeChanged = _lastSig != int.MinValue && ((plan ^ _lastSig) & 0xF000) != 0;
            _lastSig = plan;
            bool ownerGone = OwnerInactive() || ((collectChanged || scopeChanged) && Owner is ModuleType.Mine or ModuleType.Loot);
            if (State != MinerState.Paused && State != MinerState.Manual && ((State != MinerState.Offload && State != MinerState.Trading) || ownerGone))
            {
                if (ownerGone)
                {
                    // Cancel only our own command of the module that was switched off; queued transfers finish in the game.
                    Log.Info($"[{Id}] module {Owner} is no longer active{(collectChanged ? " or its collect filter changed" : "")}; stopping its command and re-planning");
                    EndStuck();
                    if (!HasForeignQueued())
                    {
                        Stop(game); // append:false would also drop Shift-queued player orders: then manual detection decides
                    }
                }
                Target = null;
                SetOwner(null, "");
                SetState(MinerState.FindTarget, L.T("MiningDrone/Replanning"));
            }
        }

        if ((State == MinerState.Paused || State == MinerState.Manual) && _defIds.Count > 0)
        {
            ReleaseDefence(game); // user pause / manual control: our weapon targets go too
        }
        if (State == MinerState.Paused)
        {
            return;
        }

        // Manual order detection. Only meaningful once one of OUR commands has actually shown up as the
        // ship's displayed command: whatever the ship was doing before (pre-existing/default command) is
        // not a "manual order". After that, any displayed command we cannot attribute to ourselves
        // (same object/ID, or a vanilla salvage re-target) is the player's.
        //
        // Ownership is by exact CommandID only (confirmation keeps the UniqueID: MPEnqueueCommandsInput.cs:158-172).
        // Commands the game generates itself use AllocGlobalCommandID -> PlayerID == null (CommandManager.cs:998):
        // salvage re-target on asteroid split (SalvageCommand.cs:238), follow -> move when the target is lost
        // (BaseFollowCommand.cs:485). Those are adopted. Any other displayed command, anywhere in the queue
        // (Shift-appended orders included), carries the local player's ID and is a manual order.
        if (State == MinerState.Manual)
        {
            // Hand control back once no player-authored command has been displayed for 5 s of game time.
            List<Command> q = Ship.Commands._commands;
            for (int i = 0; i < q.Count; i++)
            {
                Command c = q[i];
                // A finished MoveCommand stays displayed until another command follows (CommandManager.cs:1151), so a
                // satisfied move counts as done; follow/attack/salvage stay active until they end or are replaced.
                if (c.IsDisplayed && !_ownIds.Contains(c.UniqueID) && !(c is MoveCommand && c.IsSatisfied()))
                {
                    _manualQuietSince = now;
                    break;
                }
            }
            if (now - _manualQuietSince >= 5000)
            {
                Log.Info($"[{Id}] manual orders finished; resuming automation");
                ForgetCommand();
                Target = null;
                SetOwner(null, "");
                SetState(MinerState.FindTarget, L.T("MiningDrone/ManualDone"));
            }
            return;
        }

        if (_lastCmd != null)
        {
            List<Command> cmds = Ship.Commands._commands;
            Command? foreign = null;
            bool ownShown = false;
            for (int i = 0; i < cmds.Count; i++)
            {
                Command c = cmds[i];
                if (!c.IsDisplayed)
                {
                    continue;
                }
                if (_ownIds.Contains(c.UniqueID))
                {
                    ownShown = true;
                }
                else if (c.UniqueID.PlayerID == null)
                {
                    _ownIds.Add(c.UniqueID);
                    ownShown = true;
                    Log.Debug(Id, $"adopt game-generated {c.GetType().Name} id {c.UniqueID} target {TargetName(c)}");
                }
                else
                {
                    foreign ??= c;
                }
            }
            if (ownShown && !_ownSeen)
            {
                _ownSeen = true;
                Log.Debug(Id, $"own {_expectType?.Name} id {_lastCmdId} now displayed");
            }
            if (foreign != null && _ownSeen)
            {
                Log.Info($"[{Id}] pause: displayed {foreign.GetType().Name} id {foreign.UniqueID} target {TargetName(foreign)} is not ours (last own {_expectType?.Name} id {_lastCmdId}, state {State}, owner {Owner?.ToString() ?? "-"})");
                EndStuck();
                SetOwner(null, L.T("MiningDrone/PreemptManual"));
                if (Cfg.ResumeAfterManual)
                {
                    _manualQuietSince = now;
                    SetState(MinerState.Manual, L.T("MiningDrone/ManualControl"));
                }
                else
                {
                    SetState(MinerState.Paused, L.T("MiningDrone/PausedManual"));
                }
                return;
            }
            if (!_ownSeen && now - _lastIssueAt > OwnCommandTimeoutMs)
            {
                // Our input never became a displayed command (dropped / replaced before display): re-plan, don't pause.
                Log.Info($"[{Id}] own {_expectType?.Name} not displayed after {OwnCommandTimeoutMs} ms (first {Ship.Commands.CurDisplayedCommand?.GetType().Name ?? "none"}); re-planning");
                ForgetCommand();
                SetOwner(null, "");
                SetState(MinerState.FindTarget, L.T("MiningDrone/Replanning"));
            }
        }

        // Self-defence fire: weapon targets only, never movement; runs under every movement owner.
        DefendFireTick(game, now);
        LogisticsTick(game, now); // background modules (Supply, Crew), likewise

        // Damage latch before the stuck check below, so crossing the threshold while stuck interrupts recovery.
        if (Cfg.Retreat.Active)
        {
            UpdateDamage(now);
        }

        // Emergencies beat stuck recovery (help mode included): drop recovery so Avoidance/Retreat/Guard below take over.
        // An escape that got stuck keeps its own recovery (the emergency is the reason it moves at all).
        if (_stuckActive && _stuckFrom != MinerState.Evading && EmergencyPending(game, now))
        {
            Log.Info($"[{Id}] stuck recovery interrupted by an emergency (enemy)");
            EndStuck();
        }

        // Shared movement layer: stuck detection + recovery for every module's travel legs.
        if (StuckTick(game, now))
        {
            return;
        }

        // Emergency layer 1: Avoidance policy (route around enemy ranges, escape when caught inside).
        if (Cfg.Avoid.Active)
        {
            if (AvoidTick(game, now))
            {
                return;
            }
        }
        else if (DefendEvadeOn)
        {
            // Self-defence evade: the Avoidance escape episode only (no routing around ranges).
            World.BuildThreatZones(sim, Ship, now);
            if (EscapeEpisode(game, now, ModuleType.SelfDefence))
            {
                return;
            }
        }
        else
        {
            World.ClearThreats();
        }

        // Emergency layer 2 (Retreat), reactive Guard, then Hunt (a task module: seeks enemies inside its area).
        if ((Cfg.Retreat.Active || Cfg.Guard.Active || Cfg.Hunt.Active) && now >= _nextEnemyCheck)
        {
            _nextEnemyCheck = now + EnemyCheckMs;
            Ship? enemy = Cfg.Retreat.Active || Cfg.Guard.Active ? World.FindEnemy(sim, Ship) : null;
            if (enemy != null)
            {
                _lastEnemySeen = now;
            }
            if (Cfg.Retreat.Active)
            {
                RepairTick(game, now, enemy);
                if (DamageTick(game, enemy))
                {
                    return;
                }
            }
            if (enemy != null)
            {
                if (Cfg.Retreat.Active && Cfg.RetreatTrigger == 0)
                {
                    // Retreat preempts Guard and Hunt; an Avoidance escape in progress keeps the movement.
                    if (State != MinerState.Evading || _evadeBy is ModuleType.Guard or ModuleType.Hunt)
                    {
                        Flee(game, enemy);
                        return;
                    }
                }
                // A Hunt fight is a task owner, not an escape: Guard takes it over by priority (MayPreempt).
                else if (Cfg.Guard.Active && GuardRetarget(State == MinerState.Evading && _evadeBy != ModuleType.Hunt, _evadeBy == ModuleType.Guard, StillHostile(sim, _enemy))
                    && GuardMayPreempt(now))
                {
                    Attack(game, enemy, ModuleType.Guard);
                    return;
                }
            }
            if (HuntAcquire(game, now))
            {
                return;
            }
        }

        switch (State)
        {
            case MinerState.FindTarget:
                Arbitrate(game, now, stored, remaining);
                break;
            case MinerState.Waiting:
                if (now >= _nextActionAt)
                {
                    // Re-arbitrate in this tick: no transient "searching" state between two waits (list vs detail).
                    SetState(MinerState.FindTarget, Status);
                    Arbitrate(game, now, stored, remaining);
                }
                break;
            case MinerState.Mining:
                DoMining(game, now, stored, remaining);
                break;
            case MinerState.FlyToBase:
                DoFlyToBase(game, now);
                break;
            case MinerState.Offload:
                DoOffload(game, now, stored);
                break;
            case MinerState.Approach:
                if (OffloadTriggered(stored, remaining, out string why0))
                {
                    GoToBase(game, now, why0);
                    break;
                }
                if (Owner == ModuleType.Loot && LootSourceOff(Cfg.LootKinds, _stasisKind))
                {
                    // Live edit switched the source of this stasis pick off: stop flying to it.
                    if (!HasForeignQueued())
                    {
                        Stop(game);
                    }
                    SetState(MinerState.FindTarget, L.T("MiningDrone/Replanning"));
                    break;
                }
                // Re-plan once close (the asteroid has spawned live) or when the move ended / timed out.
                if (Ship.WorldCenter.DistanceTo(_approachDest) < 1500f || (Ship.Commands.CurDisplayedCommand == null && now - _lastIssueAt > IssueGraceMs) || now - _lastProgressAt > 180_000)
                {
                    SetState(MinerState.FindTarget, L.T("MiningDrone/Approaching"));
                }
                break;
            case MinerState.Holding:
                if (now >= _nextActionAt)
                {
                    SetState(MinerState.FindTarget, Status);
                    Arbitrate(game, now, stored, remaining);
                }
                break;
            case MinerState.CollectLoose:
                DoCollectLoose(game, now, stored, remaining);
                break;
            case MinerState.Trading:
                DoTrade(game, now);
                break;
            case MinerState.Evading:
                if (_evadeBy == ModuleType.Hunt)
                {
                    HuntTick(game);
                    break;
                }
                if (_evadeBy == ModuleType.Retreat && Cfg.Retreat.Active)
                {
                    RetreatTick(game, now);
                }
                if (_evadeBy == ModuleType.Avoidance || _evadeBy == ModuleType.SelfDefence)
                {
                    if (!(_evadeBy == ModuleType.Avoidance ? Cfg.Avoid.Active : DefendEvadeOn))
                    {
                        SetOwner(null, "");
                        SetState(MinerState.FindTarget, L.T("MiningDrone/ThreatGone"));
                    }
                }
                else
                {
                    if (_evadeBy == ModuleType.Guard && _enemy != null && !StillHostile(sim, _enemy))
                    {
                        // Target destroyed (wreck turns junk but stays in sim) or lost: drop it and re-scan next tick.
                        Log.Info($"[{Id}] guard: target {_enemy.Metadata.PlainShipName} down or lost; scanning for the next enemy");
                        _enemy = null;
                        _nextEnemyCheck = now;
                        SetState(MinerState.Evading, L.T("MiningDrone/GuardTargetDown"));
                    }
                    long clear = _evadeBy == ModuleType.Guard ? Cfg.GuardResumeMs : Cfg.RetreatResumeMs;
                    bool still = _evadeBy == ModuleType.Guard ? Cfg.Guard.Active : Cfg.Retreat.Active;
                    // Damage trigger: the episode lasts until the hull recovers (DamageTick), not until radar is clear.
                    bool over = _evadeBy == ModuleType.Retreat && Cfg.RetreatTrigger == 1 ? !_damaged : now - _lastEnemySeen > clear;
                    if (!still || over)
                    {
                        _enemy = null;
                        SetOwner(null, "");
                        SetState(MinerState.FindTarget, L.T("MiningDrone/ThreatGone"));
                    }
                }
                break;
        }
    }

    // ------------------------------------------------------------------ arbitration

    /// <summary>Would an emergency policy act now? (In danger per the escape trigger, InDanger/Escape.Danger, with Avoidance;
    /// an enemy seen with Retreat, or with Guard when it may preempt.) Does not consume the 1 s enemy-check cadence.</summary>
    private bool EmergencyPending(GameRoot game, long now)
    {
        if (Cfg.Avoid.Active || DefendEvadeOn)
        {
            World.BuildThreatZones(game.Sim, Ship, now);
            if (InDanger(Ship.WorldCenter, out _))
            {
                return true;
            }
        }
        if (Cfg.Retreat.Active && Cfg.RetreatTrigger == 1 && _damaged)
        {
            return true; // damage retreat is due with or without an enemy
        }
        if (((Cfg.Retreat.Active && Cfg.RetreatTrigger == 0) || (Cfg.Guard.Active && GuardMayPreempt(now))) && now >= _nextEnemyCheck)
        {
            return World.FindEnemy(game.Sim, Ship) != null;
        }
        return false;
    }

    private int PriorityOf(ModuleType? m) => m switch
    {
        ModuleType.Mine => Cfg.Mine.Priority,
        ModuleType.Loot => Cfg.Loot.Priority,
        ModuleType.Offload => Cfg.Offload.Priority,
        ModuleType.AnchorLink => Cfg.Anchor.Priority,
        ModuleType.Guard => Cfg.Guard.Priority,
        ModuleType.Hunt => Cfg.Hunt.Priority,
        ModuleType.Trade => Cfg.Trade.Priority,
        ModuleType.Avoidance or ModuleType.Retreat or ModuleType.SelfDefence => int.MaxValue, // emergency
        _ => int.MinValue,
    };

    private bool GuardMayPreempt(long now) => MayPreempt(ModuleType.Guard, Cfg.Guard.Priority, now);

    /// <summary>A reactive task module (Guard, Hunt) preempts the owner only with a strictly higher priority, after the
    /// owner's minimum commitment time; emergency owners are never preempted by it.</summary>
    private bool MayPreempt(ModuleType m, int priority, long now)
    {
        if (Owner == null || State == MinerState.FindTarget || State == MinerState.Waiting || State == MinerState.Holding)
        {
            return true;
        }
        if (Owner == m)
        {
            return true;
        }
        return priority > PriorityOf(Owner) && now - _ownerSince >= Cfg.MinCommitMs;
    }

    /// <summary>Offload's cheap trigger while a harvest module owns movement (old "cargo full" check).</summary>
    private bool OffloadTriggered(int stored, int remaining, out string why)
    {
        why = "";
        if (!Cfg.Offload.Active || stored <= 0)
        {
            return false;
        }
        // Below the threshold a rock whose ore has no room is skipped (DoMining), not a reason to return; a hold that
        // takes no unit at all shows 100% (Fill), so it always lands here.
        if (Fill >= Cfg.Threshold)
        {
            why = remaining <= 0 || NoRoomForTarget(out _) ? HoldFullReason() : L.T("MiningDrone/CargoFull");
            return true;
        }
        return false;
    }

    /// <summary>"Трюм полон: нет места под X" for the first wanted type of the current target that no slot accepts.</summary>
    private string HoldFullReason()
    {
        if (NoRoomForTarget(out ID<ResourceRules> type) && World.IndexOf(type) is int i && i >= 0)
        {
            return L.F("MiningDrone/HoldFullNoRoom", World.Resources[i].NameKey.Text);
        }
        return L.T("MiningDrone/CargoFull");
    }

    private bool NoRoomForTarget(out ID<ResourceRules> type)
    {
        type = default;
        Ship? t = Target;
        if (t == null || State != MinerState.Mining)
        {
            return false;
        }
        ulong want = (t.Rules.IsAsteroid ? (_mask & World.AsteroidGroupMask) : _mask) & World.YieldOf(t);
        if (want == 0 || (want & ~(World.NoRoomMask(Ship, want) | _maxMask)) != 0)
        {
            return false;
        }
        int i = System.Numerics.BitOperations.TrailingZeroCount(want);
        type = i < World.Resources.Length ? World.Resources[i].ID : default;
        return true;
    }

    private enum PKind
    {
        Live,
        Stasis,
        Nugget,
    }

    private struct Proposal
    {
        public ModuleType Module;
        public int Priority;
        public int Index;
        public PKind Kind;
        public double Cost; // squared distance from the ship; with a collect priority: World.PriorityCost (stasis bias included)
        public Ship? Ship;
        public Nugget? Nugget;
        public Vector2 Loc;
        public World.JunkKind Junk; // Loot stasis pick: its source kind (dropped if that source is switched off en route)
        public bool Valid;
    }

    /// <summary>
    /// Deterministic pairwise order: higher priority; then lower cost, where a stasis rock competing with a live
    /// target pays the old 1 km bias (live targets win unless a stasis rock is clearly closer); then the stable
    /// instance order.
    /// </summary>
    private static bool Better(in Proposal a, in Proposal b, bool ranked)
    {
        if (!b.Valid)
        {
            return a.Valid;
        }
        if (!a.Valid)
        {
            return false;
        }
        if (a.Priority != b.Priority)
        {
            return a.Priority > b.Priority;
        }
        // With a collect priority the stasis bias is already inside each cost (it must shape the candidate pick too).
        // Any live target (ship or nugget) is a known haul: the stasis side pays the bias against both.
        double ca = a.Cost + (!ranked && a.Kind == PKind.Stasis && b.Kind != PKind.Stasis ? World.StasisBias : 0f);
        double cb = b.Cost + (!ranked && b.Kind == PKind.Stasis && a.Kind != PKind.Stasis ? World.StasisBias : 0f);
        if (ca != cb)
        {
            return ca < cb;
        }
        return a.Index < b.Index;
    }

    private void Arbitrate(GameRoot game, long now, int stored, int remaining)
    {
        SimRoot sim = game.Sim;
        // Temporary intents (assignment level, not profiles).
        if (Cfg.Temp == TempIntent.UnloadNow)
        {
            GoToBase(game, now, L.T("MiningDrone/UnloadNowReason"));
            return;
        }
        if (Cfg.Offload.Active && Fill >= Cfg.Threshold && stored > 0 && !HarvestOutranksOffload(remaining))
        {
            GoToBase(game, now, remaining <= 0 ? L.T("MiningDrone/HoldFull") : L.T("MiningDrone/CargoFull"));
            return;
        }
        PruneBlacklist(now);
        if (Cfg.Temp == TempIntent.ReturnHome)
        {
            DoReturnHome(game, now, stored);
            return;
        }

        RuntimeBlocker = "";
        // Trade (sell-only): before the harvest search when it outranks every active harvest module, else when idle.
        bool canTrade = TradeCandidate(game, now, out Cosmoteer.Ships.Special.MetaSpaceStation tradeAt);
        if (canTrade && TradeOutranksHarvest())
        {
            StartTrade(now, tradeAt);
            return;
        }
        Ship? anchor = Cfg.Anchored ? ResolveAnchor(game) : (AnchorShip = null);
        HarvestScope mineScope = Cfg.MineScope, lootScope = Cfg.LootScope;
        bool needAnchor = (Cfg.Mine.Active && mineScope == HarvestScope.Anchor) || (Cfg.Loot.Active && lootScope == HarvestScope.Anchor);
        if (needAnchor && anchor == null)
        {
            RuntimeBlocker = Cfg.AnchorId == null ? L.T("MiningDrone/NoEscort") : L.T("MiningDrone/AnchorMissing");
            switch (Cfg.AnchorLink.MissingPolicy)
            {
                case MissingAnchorPolicy.Rally:
                    if (HoldAt(game, now, ResolveBase(game), RuntimeBlocker))
                    {
                        return;
                    }
                    break;
                case MissingAnchorPolicy.WorkWithout:
                    mineScope = mineScope == HarvestScope.Anchor ? HarvestScope.Nearest : mineScope;
                    lootScope = lootScope == HarvestScope.Anchor ? HarvestScope.Nearest : lootScope;
                    needAnchor = false;
                    break;
            }
            if (needAnchor)
            {
                // Never assume the anchor was destroyed and never pick another one: wait and retry.
                SetOwner(null, "");
                SetState(MinerState.Waiting, RuntimeBlocker);
                _nextActionAt = now + WaitRetryMs;
                return;
            }
        }

        Vector2 origin = Ship.WorldCenter;
        World.Diag.Reset();
        Proposal best = default;
        bool full = remaining <= 0 && stored > 0;

        // An empty collect selection skips the search entirely: say so instead of reporting all-zero counters.
        _noSelection = "";
        if (Cfg.Mine.Active && (_mask & World.AsteroidGroupMask) == 0)
        {
            _noSelection = L.T("MiningDrone/NoOreSelected");
        }
        if (Cfg.Loot.Active && _mask == 0 && Cfg.LootAny)
        {
            string loot = L.T("MiningDrone/NoLootSelected");
            _noSelection = _noSelection.Length == 0 ? loot : _noSelection + "; " + loot;
        }
        if (_noSelection.Length > 0)
        {
            RuntimeBlocker = RuntimeBlocker.Length == 0 ? _noSelection : RuntimeBlocker + "; " + _noSelection;
        }
        if (Logistics.MaxConflict(Ship, Assignment.Cargo) is string maxConflict)
        {
            RuntimeBlocker = RuntimeBlocker.Length == 0 ? maxConflict : RuntimeBlocker + "; " + maxConflict; // keep-max below vanilla desire
        }

        // Only selected types that still have room are searched for (live capacity: freed space re-admits rocks).
        ulong wanted = (Cfg.Mine.Active ? _mask & World.AsteroidGroupMask : 0) | (Cfg.Loot.Active && Cfg.LootAny ? _mask : 0);
        ulong noRoom = World.NoRoomMask(Ship, wanted) | (wanted & _maxMask); // at keep-max counts as no room
        _fitMask = _mask & ~noRoom;
        if (wanted != 0 && (wanted & ~noRoom) == 0)
        {
            string none = L.T("MiningDrone/NoFitTargets");
            _noSelection = _noSelection.Length == 0 ? none : _noSelection + "; " + none; // nothing is searched: say why
        }

        // Mine: live asteroid vs stasis asteroid (stasis search is skipped for anchor scope: work stays near the anchor).
        if (Cfg.Mine.Active && !full)
        {
            ulong astMask = _fitMask & World.AsteroidGroupMask;
            if (astMask != 0)
            {
                bool area = WorkArea(game, mineScope, out Circle areaCircle);
                float maxDist = AgentConfig.EffectiveSearch(Cfg.MineMaxSearch);
                Ship? live = World.FindTarget(sim, Ship, origin, area, areaCircle, maxDist, astMask, 0, _exclude, _ranks, out double liveCost);
                var pl = new Proposal { Module = ModuleType.Mine, Priority = Cfg.Mine.Priority, Index = Cfg.Mine.Index, Kind = PKind.Live, Ship = live, Valid = live != null };
                if (live != null)
                {
                    pl.Cost = liveCost;
                }
                Proposal ps = default;
                _stasisPick = null;
                if (mineScope != HarvestScope.Anchor)
                {
                    // Rocks given up recently (enemy in the way) are filtered inside the search, so farther ones stay eligible.
                    Vector2? farLoc = World.FindStasisAsteroid(sim, origin, area, areaCircle, maxDist, astMask, _stasisSkip, now, _ranks, out double stasisCost, out string source);
                    if (farLoc is Vector2 fl)
                    {
                        ps = new Proposal { Module = ModuleType.Mine, Priority = Cfg.Mine.Priority, Index = Cfg.Mine.Index, Kind = PKind.Stasis, Loc = fl, Cost = stasisCost, Valid = true };
                        if (Better(ps, pl, _ranks != null))
                        {
                            Log.Debug(Id, $"choose stasis rock at {fl.DistanceTo(origin):0} [{source}] (nearest live {(live == null ? "none" : live.Metadata.PlainShipName + " at " + live.WorldBoundingCircle.Center.DistanceTo(origin).ToString("0"))}); {World.StasisCandidates()}");
                            pl = ps;
                        }
                    }
                }
                best = pl;
            }
        }

        // Loot: live wreck vs loose nugget.
        if (Cfg.Loot.Active && !full && _mask != 0 && Cfg.LootAny)
        {
            bool area = WorkArea(game, lootScope, out Circle areaCircle);
            float maxDist = AgentConfig.EffectiveSearch(Cfg.LootMaxSearch);
            Proposal lw = default;
            if (Cfg.LootKinds != 0)
            {
                World.DiagSnap snap = World.Diag.Save();
                Ship? wreck = World.FindTarget(sim, Ship, origin, area, areaCircle, maxDist, 0, _fitMask, _exclude, _ranks, out double wreckCost, Cfg.LootKinds);
                World.Diag.Add(snap);
                if (wreck != null)
                {
                    lw = new Proposal { Module = ModuleType.Loot, Priority = Cfg.Loot.Priority, Index = Cfg.Loot.Index, Kind = PKind.Live, Ship = wreck, Cost = wreckCost, Valid = true };
                }
            }
            if (Cfg.LootLoose)
            {
                Nugget? nugget = World.FindNugget(sim, Ship, origin, area, areaCircle, maxDist, _fitMask, _nuggetBlacklist, _ranks, out double nuggetCost);
                if (nugget != null)
                {
                    var nuggetProp = new Proposal { Module = ModuleType.Loot, Priority = Cfg.Loot.Priority, Index = Cfg.Loot.Index, Kind = PKind.Nugget, Nugget = nugget, Loc = nugget.WorldLocation, Cost = nuggetCost, Valid = true };
                    if (!lw.Valid || nuggetProp.Cost < lw.Cost)
                    {
                        lw = nuggetProp;
                    }
                }
            }
            // Junk ships beyond the live range (as Mine: not for anchor scope). An unknown-yield record ("maybe") is
            // visited only when Loot has nothing confirmed; once live, the re-pick checks its real yield.
            if (Cfg.LootKinds != 0 && lootScope != HarvestScope.Anchor)
            {
                Vector2? far = World.FindStasisJunk(sim, origin, area, areaCircle, maxDist, _fitMask, Cfg.LootKinds, _stasisSkip, now, _ranks, out double farCost, out bool maybe, out World.JunkKind farKind);
                if (far is Vector2 fl)
                {
                    var ps = new Proposal { Module = ModuleType.Loot, Priority = Cfg.Loot.Priority, Index = Cfg.Loot.Index, Kind = PKind.Stasis, Loc = fl, Cost = farCost, Junk = farKind, Valid = true };
                    if (maybe ? !lw.Valid : Better(ps, lw, _ranks != null))
                    {
                        Log.Debug(Id, $"choose stasis {(maybe ? "unknown " : "")}junk ship at {fl.DistanceTo(origin):0}");
                        lw = ps;
                    }
                }
            }
            if (Better(lw, best, _ranks != null))
            {
                best = lw;
            }
        }

        if (best.Valid)
        {
            Start(game, now, best);
            return;
        }
        if ((wanted & noRoom) != 0 && Cfg.Offload.Active && stored > 0 && ResolveBase(game) != null)
        {
            // Nothing found that fits while a selected type has no room: unload (before any anchor idle-follow).
            GoToBase(game, now, L.T("MiningDrone/NoFitTargets"));
            return;
        }
        if (canTrade)
        {
            StartTrade(now, tradeAt);
            return;
        }
        Idle(game, now, stored, anchor);
    }

    /// <summary>True when an active harvest module is configured above Offload and the hold still takes cargo.</summary>
    private bool HarvestOutranksOffload(int remaining)
    {
        if (remaining <= 0)
        {
            return false;
        }
        return (Cfg.Mine.Active && Cfg.Mine.Priority > Cfg.Offload.Priority) || (Cfg.Loot.Active && Cfg.Loot.Priority > Cfg.Offload.Priority);
    }

    private void Start(GameRoot game, long now, in Proposal p)
    {
        Ship origin = Ship;
        switch (p.Kind)
        {
            case PKind.Nugget:
                {
                    // Loose resources: fly next to the nugget (vanilla MoveCommand pathing), then crew pick-up inputs.
                    Nugget nugget = p.Nugget!;
                    Target = null;
                    _nugget = nugget;
                    Vector2 dest = nugget.WorldLocation;
                    if (!IssueTravel(game, dest, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), dest, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: false, null))
                    {
                        _nuggetBlacklist[nugget] = now + AvoidBlacklistMs;
                        SetState(MinerState.FindTarget, L.T("MiningDrone/RouteBlocked"));
                        return;
                    }
                    _lastProgressAt = now;
                    _nextActionAt = 0;
                    NoTargets = false;
                    SetOwner(ModuleType.Loot, "");
                    SetState(MinerState.CollectLoose, L.T("MiningDrone/CollectingLoose"));
                    return;
                }
            case PKind.Stasis:
                {
                    // Asteroids beyond StasisLiveRange of every player ship exist only as stasis spawners: fly toward the
                    // nearest matching one; it spawns as a real asteroid when we get close.
                    Target = null;
                    Vector2 dest = p.Loc;
                    _stasisPick = dest;
                    _stasisKind = p.Junk;
                    if (!IssueTravel(game, dest, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), dest, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true, null))
                    {
                        _stasisSkip[dest] = now + AvoidBlacklistMs;
                        SetState(MinerState.Waiting, L.T("MiningDrone/RouteBlocked"));
                        _nextActionAt = now + WaitRetryMs;
                        return;
                    }
                    _approachDest = dest;
                    _lastProgressAt = now;
                    NoTargets = false;
                    SetOwner(p.Module, "");
                    SetState(MinerState.Approach, L.T("MiningDrone/Approaching"));
                    return;
                }
            default:
                {
                    Ship target = p.Ship!;
                    NoTargets = false;
                    Target = target;
                    _resalvageRounds = 0;
                    _resalvageAfterMove = false;
                    Log.Debug(Id, $"{p.Module} target {target.Metadata.PlainShipName} at {target.WorldBoundingCircle.Center.DistanceTo(origin.WorldCenter):0}");
                    SetOwner(p.Module, "");
                    IssueSalvage(game, target, now);
                    if (Target != null)
                    {
                        SetState(MinerState.Mining, L.T("MiningDrone/Mining"));
                    }
                    return;
                }
        }
    }

    /// <summary>Player-facing "no targets" reason: the dominant rejection only (ties: first listed).</summary>
    private static string NoTargetsReadable(int empty, int sun, int filter, int outOfArea, int skipped)
    {
        (int n, string key) best = (0, "MiningDrone/NoTargetsNone");
        foreach (var c in new[] { (filter, "MiningDrone/NoTargetsFilter"), (empty, "MiningDrone/NoTargetsEmpty"), (sun, "MiningDrone/NoTargetsSun"), (outOfArea, "MiningDrone/NoTargetsOut"), (skipped, "MiningDrone/NoTargetsSkipped") })
        {
            if (c.Item1 > best.n)
            {
                best = c;
            }
        }
        return best.n == 0 ? L.T(best.key) : L.F(best.key, best.n);
    }

    /// <summary>No task module proposed anything: anchor follow, else offload leftovers / park at the offload target.</summary>
    private void Idle(GameRoot game, long now, int stored, Ship? anchor)
    {
        bool harvesting = Cfg.Mine.Active || Cfg.Loot.Active;
        bool searched = (Cfg.Mine.Active && (_fitMask & World.AsteroidGroupMask) != 0) || (Cfg.Loot.Active && _fitMask != 0 && Cfg.LootAny);
        string diag = NoTargetsReadable(World.Diag.Empty, World.Diag.Sun, World.Diag.Filter, World.Diag.OutOfArea, World.Diag.Excluded); // raw counters: log below
        string why = !harvesting ? L.T("MiningDrone/NoTask")
            : _noSelection.Length == 0 ? diag
            : searched ? _noSelection + "; " + diag
            : _noSelection; // nothing was searched: all-zero counters would mislead
        if (harvesting)
        {
            // Nothing at all: say why (counts per rejection reason, or the empty selection that skipped the search).
            string summary = World.Diag.Summary();
            if (summary != _lastDiag || now - _lastDiagAt > 60_000)
            {
                _lastDiag = summary;
                _lastDiagAt = now;
                Log.Info($"[{Id}] no targets (mine {(Cfg.Mine.Active ? Cfg.MineScope.ToString() : "off")}, loot {(Cfg.Loot.Active ? Cfg.LootScope + (Cfg.LootWrecks ? "+wrecks" : "") + ((Cfg.LootKinds & World.KindCache) != 0 ? "+caches" : "") + ((Cfg.LootKinds & World.KindDerelict) != 0 ? "+derelicts" : "") + (Cfg.LootLoose ? "+loose" : "") : "off")}, anchor {(anchor != null ? anchor.Metadata.PlainShipName : "-")}){(_noSelection.Length > 0 ? " [" + _noSelection + "]" : "")}: {summary}");
            }
        }
        if (anchor != null && Cfg.Anchor.Active && Cfg.FollowIdle)
        {
            // Nothing around the anchor: stay with it (reuse our follow if it is already running).
            Target = null;
            Command? cur = Ship.Commands.CurDisplayedCommand;
            // Formation: a distinct slot (angle relative to the anchor's heading, spaced edge distance) per ship.
            float dist = Cfg.AnchorLink.FollowDistance, angle = float.NaN;
            if (Cfg.Formation)
            {
                FormationSlot(out int k, out int n);
                angle = FormationAngle(k, n);
                dist = FormationEdge(n, anchor.WorldBoundingRadius, Ship.WorldBoundingRadius, dist);
            }
            // Reuse only while distance and slot are the ones it was issued with (a live edit or a new sibling re-issues it).
            if (float.IsNaN(_escortDist))
            {
                // Escort that predates this agent (restore): adopt it only when it provably has the live distance and
                // no slot, otherwise re-issue once (the vanilla default distance can't be compared).
                _escortDist = dist >= 0f && float.IsNaN(angle) && cur is FollowCommand fc && fc.EdgeToEdgeFollowRadius == dist && !fc.FollowAngle.HasValue ? dist : float.NegativeInfinity;
            }
            if (_escortDist != dist || !(_escortAngle == angle || (float.IsNaN(_escortAngle) && float.IsNaN(angle)))
                || !(IsOurCommand(cur) && ((cur is FollowCommand f && f.HasTarget && f.Target == anchor) || (_routed && ReferenceEquals(_followShip, anchor)))))
            {
                var follow = new FollowCommand(Ship, Ship.Commands.AllocLocalCommandID(), anchor, CollisionAvoidanceFlags.All, isTentative: false, waitForCrew: true);
                if (dist >= 0f)
                {
                    follow.EdgeToEdgeFollowRadius = dist;
                }
                if (!float.IsNaN(angle))
                {
                    // Anchor-relative angle (BaseFollowCommand.GetWorldFollowAngle); Rotation stays null.
                    follow.FollowAngle = (Direction)angle;
                    follow.UseWorldRelativeFollowAngle = false;
                }
                _escortDist = dist;
                _escortAngle = angle;
                IssueFollow(game, anchor, follow, "escort");
            }
            SetOwner(ModuleType.AnchorLink, "");
            string escort = L.F("MiningDrone/Escorting", anchor.Metadata.PlainShipName);
            SetState(MinerState.Holding, harvesting ? escort + " · " + why : escort);
            _nextActionAt = now + 3000;
            return;
        }
        Target = null;
        if (harvesting)
        {
            // Don't idle in place silently.
            if (!NoTargets)
            {
                Notice.NoTargets(game, Ship, why);
            }
            NoTargets = true;
            NoTargetsReason = why;
        }
        Ship? home = Cfg.Offload.Active ? ResolveBase(game) : null;
        if (stored > 0 && home != null)
        {
            GoToBase(game, now, why); // offload what we have, then hold at base and re-search
            return;
        }
        if (home != null && Cfg.ParkIdle && HoldAt(game, now, home, why))
        {
            return;
        }
        SetOwner(null, "");
        SetState(MinerState.Waiting, why);
        _nextActionAt = now + WaitRetryMs;
    }

    private bool HoldAt(GameRoot game, long now, Ship? home, string why)
    {
        if (home == null)
        {
            return false;
        }
        Command? cur = Ship.Commands.CurDisplayedCommand;
        if (!(IsOurCommand(cur) && ((cur is FollowCommand hf && hf.HasTarget && hf.Target == home) || (_routed && ReferenceEquals(_followShip, home)))))
        {
            IssueFollow(game, home, new FollowCommand(Ship, Ship.Commands.AllocLocalCommandID(), home, CollisionAvoidanceFlags.All, isTentative: false, waitForCrew: true) { EdgeToEdgeFollowRadius = 60f }, "hold at base");
        }
        SetOwner(ModuleType.Offload, "");
        SetState(MinerState.Holding, why);
        _nextActionAt = now + 10_000;
        return true;
    }

    private void SetOwner(ModuleType? m, string reason)
    {
        if (m != Owner)
        {
            if (Owner != null && m != null && reason.Length > 0)
            {
                Preempt = reason;
            }
            else if (m == null && reason.Length > 0)
            {
                Preempt = reason;
            }
            else if (m != null)
            {
                Preempt = "";
            }
            Log.Debug(Id, $"owner {Owner?.ToString() ?? "-"} -> {m?.ToString() ?? "-"}{(reason.Length > 0 ? " (" + reason + ")" : "")}");
            Owner = m;
            _ownerSince = _now;
        }
    }

    // ------------------------------------------------------------------ Mine / Loot

    private int _resalvageRounds;
    private bool _resalvageAfterMove;

    private void IssueSalvage(GameRoot game, Ship target, long now)
    {
        var cmd = new SalvageCommand(Ship, Ship.Commands.AllocLocalCommandID(), target, BuildAutoCollect(), CollisionAvoidanceFlags.All, isTentative: false, waitForCrew: true);
        if (!IssueTravel(game, target.WorldCenter, cmd, autoCancelSalvage: false, target))
        {
            Blacklist(target, now, AvoidBlacklistMs);
            Target = null;
            SetState(MinerState.FindTarget, L.T("MiningDrone/RouteBlocked"));
            return;
        }
        _lastProgressAt = now;
    }

    private void DoMining(GameRoot game, long now, int stored, int remaining)
    {
        SimRoot sim = game.Sim;
        if (OffloadTriggered(stored, remaining, out string why) && !HarvestOutranksOffload(remaining))
        {
            GoToBase(game, now, why);
            return;
        }
        if (NoRoomForTarget(out ID<ResourceRules> noRoom))
        {
            // Nothing this rock yields that we collect fits any more: skip it; the search only offers rocks with a
            // type that still has room (live capacity, so freed space makes it eligible again).
            string res = World.IndexOf(noRoom) is int ni && ni >= 0 ? World.Resources[ni].NameKey.Text : noRoom.ToString();
            Log.Info($"[{Id}] no room for {noRoom} (or banned) from {Target!.Metadata.PlainShipName}; skipping it (fill {Fill * 100f:0}%)");
            if (!HasForeignQueued())
            {
                Stop(game);
            }
            Target = null;
            SetState(MinerState.FindTarget, L.F("MiningDrone/SkipNoRoom", res));
            return;
        }
        HarvestScope scope = Owner == ModuleType.Loot ? Cfg.LootScope : Cfg.MineScope;
        // The target must stay inside the live work area (anchor moved, profile area edited, or a target restored
        // from a save made under another area): otherwise drop our salvage and pick again.
        if (Target != null && WorkArea(game, scope, out Circle area) && OutsideArea(Target.WorldCenter, area, Cfg.AnchorLink.Hysteresis))
        {
            Log.Info($"[{Id}] target {Target.Metadata.PlainShipName} is outside the {scope} work area; re-planning");
            if (!HasForeignQueued())
            {
                Stop(game);
            }
            Target = null;
            SetState(MinerState.FindTarget, L.T(scope == HarvestScope.Anchor ? "MiningDrone/EscortMoved" : "MiningDrone/Replanning"));
            return;
        }
        if (Cfg.Temp == TempIntent.ReturnHome)
        {
            SetState(MinerState.FindTarget, L.T("MiningDrone/Returning"));
            return;
        }
        if (Target == null || !Target.ExistsInSim(sim))
        {
            SetState(MinerState.FindTarget, L.T("MiningDrone/TargetDepleted"));
            return;
        }
        if (Owner == ModuleType.Loot && LootSourceOff(Cfg.LootKinds, World.Classify(Target)))
        {
            // Live profile edit switched this kind of junk ship off: drop our salvage, not the next pick.
            Log.Info($"[{Id}] {World.Classify(Target)} {Target.Metadata.PlainShipName} is no longer a Loot source; re-planning");
            if (!HasForeignQueued())
            {
                Stop(game);
            }
            Target = null;
            _resalvageAfterMove = false;
            SetState(MinerState.FindTarget, L.T("MiningDrone/Replanning"));
            return;
        }
        ulong want = Target.Rules.IsAsteroid ? (Harvest & World.AsteroidGroupMask) : Harvest;
        // Re-validated every tick en route (mask cached by part count: cheap), not only on arrival: others may loot the
        // wreck/rock empty meanwhile. The search rejects the same zero mask, so the re-pick cannot choose it again.
        if (EmptiedEnRoute(World.YieldOf(Target), want, Target.WorldBoundingCircle.DistanceTo(Ship.WorldCenter), GameApp.Rules.Crew.LoseHomeShipWarningDistance))
        {
            Log.Info($"[{Id}] target {Target.Metadata.PlainShipName} has no wanted yield left (looted by others?); re-planning en route");
            if (!HasForeignQueued())
            {
                Stop(game);
            }
            Target = null;
            _resalvageAfterMove = false;
            SetState(MinerState.FindTarget, L.T("MiningDrone/TargetDepleted"));
            return;
        }
        if (_resalvageAfterMove)
        {
            Command? rc = Ship.Commands.CurDisplayedCommand;
            bool arrived = rc == null || (rc is MoveCommand && rc.IsSatisfied()) || Ship.WorldBoundingCircle.DistanceTo(_approachDest) < 50f;
            bool stuck = now - _lastIssueAt > 45_000; // bounded: don't sit on a blocked reposition forever
            if ((arrived && now - _lastIssueAt > IssueGraceMs) || stuck)
            {
                _resalvageAfterMove = false;
                IssueSalvage(game, Target, now);
            }
            return;
        }
        if (Ship.Commands.CurDisplayedCommand == null && now - _lastIssueAt > IssueGraceMs)
        {
            // SalvageCommand auto-cancels when the salvage jobs it queued are done. It only queues parts within crew
            // reach of where the ship stands (SalvageCommand._CanSalvage: LoseHomeShipWarningDistance), so a big rock
            // can end with ore left. If wanted ore remains: move next to the nearest remaining ore part and salvage
            // again. Only after several fruitless rounds is the rock skipped for a while.
            if ((World.YieldOf(Target) & want) != 0 && _resalvageRounds < 4)
            {
                _resalvageRounds++;
                Vector2? part = World.NearestYieldPart(Target, want, Ship.WorldCenter);
                float dist = part is Vector2 pp ? pp.DistanceTo(Ship.WorldCenter) : 0f;
                Log.Debug(Id, $"ore left on {Target.Metadata.PlainShipName}; round {_resalvageRounds}, nearest ore part {dist:0} away");
                if (part is Vector2 dest && dist > GameApp.Rules.Crew.LoseHomeShipWarningDistance * 0.5f)
                {
                    IssueTravel(game, dest, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), dest, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: false, Target);
                    _approachDest = dest;
                    _resalvageAfterMove = true;
                    _lastProgressAt = now;
                    return;
                }
                IssueSalvage(game, Target, now);
                return;
            }
            if (_resalvageRounds >= 4)
            {
                Blacklist(Target, now);
            }
            SetState(MinerState.FindTarget, L.T("MiningDrone/TargetFinished"));
            return;
        }
        if (now - _lastProgressAt > NoProgressTimeoutMs)
        {
            Blacklist(Target, now);
            SetState(MinerState.FindTarget, L.T("MiningDrone/NoProgress"));
        }
    }

    private void DoCollectLoose(GameRoot game, long now, int stored, int remaining)
    {
        if (OffloadTriggered(stored, remaining, out string why))
        {
            GoToBase(game, now, why);
            return;
        }
        if (!Cfg.LootLoose)
        {
            // Loose source switched off by a live edit (spill of our own salvage is collected by the salvage itself).
            if (!HasForeignQueued())
            {
                Stop(game);
            }
            _nugget = null;
            SetState(MinerState.FindTarget, L.T("MiningDrone/Replanning"));
            return;
        }
        Nugget? n = _nugget;
        bool alive = n != null && game.Sim.Nuggets[n.UniqueID] != null;
        bool pending = Ship.Resources.HasManualExternalTransferJobs(Cosmoteer.Game.Multiplayer.MPValueType.Displayed, pendingPickupOnly: true);
        float reach = GameApp.Rules.Crew.LoseHomeShipWarningDistance * 0.6f;
        if (alive && Ship.WorldBoundingCircle.DistanceTo(n!.WorldLocation) < reach && now >= _nextActionAt)
        {
            _nextActionAt = now + 2000;
            bool inArea = WorkArea(game, Cfg.LootScope, out Circle work);
            int queued = World.CollectNuggetsNear(game, Ship, reach, Harvest, inArea, work, Assignment.Cargo);
            if (queued > 0)
            {
                _lastProgressAt = now;
                Status = L.T("MiningDrone/CollectingLoose");
            }
            else if (!pending)
            {
                // In reach but nothing could be queued: no free cargo for it / no crew path.
                _nuggetBlacklist[n] = now + BlacklistMs;
                SetState(MinerState.FindTarget, L.T("MiningDrone/LooseUnreachable"));
                return;
            }
        }
        if (alive && Ship.WorldBoundingCircle.DistanceTo(n!.WorldLocation) >= reach && Ship.Commands.CurDisplayedCommand == null && now - _lastIssueAt > IssueGraceMs)
        {
            // Nugget drifted after we arrived: follow it.
            IssueTravel(game, n.WorldLocation, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), n.WorldLocation, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: false, null);
        }
        if (!alive && !pending)
        {
            SetState(MinerState.FindTarget, L.T("MiningDrone/LooseDone"));
            return;
        }
        if (now - _lastProgressAt > 60_000)
        {
            if (n != null)
            {
                _nuggetBlacklist[n] = now + BlacklistMs;
            }
            SetState(MinerState.FindTarget, L.T("MiningDrone/LooseTimeout"));
        }
    }

    /// <summary>
    /// The work area used by both target search and loose pickup: Anchor = WorkRadius around the anchor (moves with
    /// it); Zone = the fixed work-zone binding; Nearest = no area (returns false).
    /// </summary>
    private bool WorkArea(GameRoot game, HarvestScope scope, out Circle area)
    {
        if (scope == HarvestScope.Anchor && ResolveAnchor(game) is Ship es)
        {
            area = new Circle(es.WorldCenter, Cfg.WorkRadius);
            return true;
        }
        area = new Circle(new Vector2(Cfg.ZoneX, Cfg.ZoneY), Cfg.ZoneR);
        return scope == HarvestScope.Zone;
    }

    /// <summary>Outside the work area, with hysteresis (a target on the edge is not dropped and re-picked).</summary>
    internal static bool OutsideArea(Vector2 target, Circle area, float hysteresis) =>
        target.DistanceTo(area.Center) > area.Radius * Math.Max(1f, hysteresis);

    // ------------------------------------------------------------------ Offload

    private void GoToBase(GameRoot game, long now, string why)
    {
        Ship? b = ResolveBase(game);
        if (b == null)
        {
            SetOwner(null, "");
            SetState(MinerState.Waiting, L.F("MiningDrone/NoBase", why));
            _nextActionAt = now + WaitRetryMs;
            return;
        }
        // waitForCrew: don't leave crew stranded on the rock / collecting nuggets (BaseFollowCommand waits for off-ship crew).
        var follow = new FollowCommand(Ship, Ship.Commands.AllocLocalCommandID(), b, CollisionAvoidanceFlags.All, isTentative: false, waitForCrew: true);
        _parkPlanned = World.ParkNear(b, Ship, follow);
        IssueFollow(game, b, follow, "to base");
        Target = null;
        SetOwner(ModuleType.Offload, Owner is ModuleType.Mine or ModuleType.Loot ? why : "");
        SetState(MinerState.FlyToBase, L.F("MiningDrone/FlyingTo", why, b.Metadata.PlainShipName));
    }

    private void DoFlyToBase(GameRoot game, long now)
    {
        Ship? b = ResolveBase(game);
        if (b == null)
        {
            SetState(MinerState.Waiting, L.T("MiningDrone/BaseLost"));
            _nextActionAt = now + WaitRetryMs;
            return;
        }
        float gap = Ship.WorldBoundingCircle.DistanceTo(b.WorldBoundingCircle);
        float reach = GameApp.Rules.Crew.LoseHomeShipWarningDistance * 0.5f;
        if (gap <= reach)
        {
            _nextActionAt = 0;
            _lastProgressAt = now;
            _parkLogPending = true;
            SetState(MinerState.Offload, L.T("MiningDrone/Offloading"));
        }
        else if ((Ship.Commands.CurDisplayedCommand == null || FollowingOtherThan(b)) && now - _lastIssueAt > IssueGraceMs)
        {
            // No command, or the offload target was changed mid-flight: (re)issue the follow to the current base.
            GoToBase(game, now, L.T("MiningDrone/Returning"));
        }
    }

    private bool FollowingOtherThan(Ship b)
    {
        Command? cur = Ship.Commands.CurDisplayedCommand;
        return cur is FollowCommand f && IsOurCommand(cur) && (!f.HasTarget || f.Target != b);
    }

    private float _parkPlanned;
    private bool _parkLogPending;
    private int _lastStoredAtOffloadCheck = int.MaxValue;

    private void DoOffload(GameRoot game, long now, int stored)
    {
        Ship? b = ResolveBase(game);
        if (b == null)
        {
            SetState(MinerState.Waiting, L.T("MiningDrone/BaseLost"));
            _nextActionAt = now + WaitRetryMs;
            return;
        }
        if (_parkLogPending && (Ship.Physics.Body?.LinearVelocity.LengthSquared() ?? 0f) < 4f)
        {
            _parkLogPending = false;
            Log.Info($"[{Id}] parked at base {b.Metadata.PlainShipName}: center distance {Ship.WorldCenter.DistanceTo(b.WorldCenter):0.0} (planned {_parkPlanned:0.0}), bounding-circle gap {Ship.WorldBoundingCircle.DistanceTo(b.WorldBoundingCircle):0.0}");
        }
        if (FollowingOtherThan(b) && now - _lastIssueAt > IssueGraceMs)
        {
            GoToBase(game, now, L.T("MiningDrone/Returning")); // offload target changed
            return;
        }
        // Only our own miner->base jobs count; unrelated transfers on the base must not block completion.
        bool pending = World.HasPendingOffload(game, Ship, b);
        if (stored <= 0 && !pending)
        {
            ClearTemp(TempIntent.UnloadNow);
            SetOwner(null, "");
            SetState(MinerState.FindTarget, L.T("MiningDrone/OffloadDone"));
            return;
        }
        if (stored < _lastStoredAtOffloadCheck)
        {
            _lastProgressAt = now;
        }
        _lastStoredAtOffloadCheck = stored;
        if (now >= _nextActionAt)
        {
            _nextActionAt = now + OffloadRequeueMs;
            int queued = World.QueueTransfer(game, Ship, b, _offMask, out bool anySink, Assignment.Cargo);
            if (!anySink && !pending)
            {
                Status = L.T("MiningDrone/BaseFull");
            }
            else if (queued > 0)
            {
                Status = L.F("MiningDrone/OffloadLeft", stored);
            }
        }
        if (now - _lastProgressAt > OffloadStallMs)
        {
            if (Fill < Cfg.Threshold)
            {
                ClearTemp(TempIntent.UnloadNow);
                SetOwner(null, "");
                SetState(MinerState.FindTarget, L.T("MiningDrone/OffloadStalledMine"));
            }
            else
            {
                Status = L.T("MiningDrone/OffloadStalledWait");
                _lastProgressAt = now;
            }
        }
    }

    private void ClearTemp(TempIntent t)
    {
        if (Assignment.Temp == t)
        {
            Assignment.Temp = TempIntent.None;
            Store.Touch(Assignment);
            Resolver.Refresh(Cfg, Assignment);
            _lastSig = PlanSig();
        }
    }

    private void DoReturnHome(GameRoot game, long now, int stored)
    {
        Ship? b = ResolveBase(game);
        if (b == null)
        {
            SetOwner(null, "");
            SetState(MinerState.Waiting, L.F("MiningDrone/NoBase", L.T("MiningDrone/Returning")));
            _nextActionAt = now + WaitRetryMs;
            return;
        }
        if (stored > 0)
        {
            GoToBase(game, now, L.T("MiningDrone/Returning"));
            return;
        }
        Command? cur = Ship.Commands.CurDisplayedCommand;
        if (!(IsOurCommand(cur) && ((cur is FollowCommand f && f.HasTarget && f.Target == b) || (_routed && ReferenceEquals(_followShip, b)))))
        {
            IssueFollow(game, b, new FollowCommand(Ship, Ship.Commands.AllocLocalCommandID(), b, CollisionAvoidanceFlags.All, isTentative: false, waitForCrew: true) { EdgeToEdgeFollowRadius = 60f }, "return home");
        }
        SetOwner(ModuleType.Offload, "");
        SetState(MinerState.Holding, L.F("MiningDrone/AtBase", b.Metadata.PlainShipName));
        _nextActionAt = now + 3000;
    }

    // ------------------------------------------------------------------ Retreat / Guard

    private void Attack(GameRoot game, Ship enemy, ModuleType by)
    {
        _enemy = enemy;
        EndStuck();
        Issue(game, new AttackCommand(Ship, Ship.Commands.AllocLocalCommandID(), enemy, CollisionAvoidanceFlags.All, isTentative: false, isConfirmed: true), autoCancelSalvage: true);
        _evadeBy = by;
        SetOwner(by, L.F("MiningDrone/PreemptEnemy", enemy.Metadata.PlainShipName));
        SetState(MinerState.Evading, L.F(by == ModuleType.Hunt ? "MiningDrone/Hunting" : "MiningDrone/Attacking", enemy.Metadata.PlainShipName));
    }

    // ------------------------------------------------------------------ Hunt

    /// <summary>Hunting area: the work zone, or the range around the resolved anchor; false without one.</summary>
    private bool HuntArea(GameRoot game, out Circle area)
    {
        if (Cfg.HuntAnchor)
        {
            Ship? a = Cfg.Anchored ? ResolveAnchor(game) : null;
            area = a != null ? new Circle(a.WorldCenter, Cfg.HuntRange) : default;
            return a != null;
        }
        area = new Circle(new Vector2(Cfg.ZoneX, Cfg.ZoneY), Cfg.ZoneR);
        return Cfg.ZoneR > 0f;
    }

    private const float HuntLeash = 1.5f;

    /// <summary>Hunt leash: the ship, or the target it chases, left the area by more than <see cref="HuntLeash"/>.</summary>
    internal static bool HuntBeyondLeash(Vector2 ship, Vector2? target, Circle area) =>
        OutsideArea(ship, area, HuntLeash) || (target is Vector2 t && OutsideArea(t, area, HuntLeash));

    /// <summary>Hunt's trigger (1 s cadence): an enemy inside the area while Hunt may take movement. Not during temp
    /// intents or emergency escapes, nor while Hunt flies back after a leash breach.</summary>
    private bool HuntAcquire(GameRoot game, long now)
    {
        // Temp intents outrank task modules; a Guard fight is taken over only by a higher-priority Hunt (MayPreempt).
        if (!Cfg.Hunt.Active || Cfg.Temp != TempIntent.None || (State == MinerState.Evading && _evadeBy != ModuleType.Guard)
            || (Owner == ModuleType.Hunt && State == MinerState.Approach)
            || !MayPreempt(ModuleType.Hunt, Cfg.Hunt.Priority, now) || !HuntArea(game, out Circle area))
        {
            return false;
        }
        Ship? e = World.FindEnemyIn(game.Sim, Ship, area);
        if (e == null)
        {
            return false;
        }
        Log.Info($"[{Id}] hunt: enemy {e.Metadata.PlainShipName} inside the area; attacking");
        Attack(game, e, ModuleType.Hunt);
        return true;
    }

    /// <summary>Hunt owns movement and fights: next target when this one is down, back to the area past the leash.</summary>
    private void HuntTick(GameRoot game)
    {
        SimRoot sim = game.Sim;
        bool alive = StillHostile(sim, _enemy);
        if (!Cfg.Hunt.Active || !HuntArea(game, out Circle area))
        {
            HuntEnd(game);
            return;
        }
        if (HuntBeyondLeash(Ship.WorldCenter, alive ? _enemy!.WorldCenter : null, area))
        {
            // Cancel our attack (unless the player queued orders: manual detection decides) and fly back first.
            Log.Info($"[{Id}] hunt: past the leash ({HuntLeash}x area radius); returning to the area");
            _enemy = null;
            Target = null;
            _approachDest = area.Center;
            if (!HasForeignQueued())
            {
                Issue(game, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), area.Center, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true);
            }
            _lastProgressAt = _now;
            SetState(MinerState.Approach, L.T("MiningDrone/HuntReturn"));
            return;
        }
        if (!alive)
        {
            Ship? next = World.FindEnemyIn(sim, Ship, area);
            if (next != null)
            {
                Attack(game, next, ModuleType.Hunt);
                return;
            }
            HuntEnd(game);
        }
    }

    /// <summary>Ends the hunt: our attack is stopped (player-queued orders are left to manual detection), then re-plan.</summary>
    private void HuntEnd(GameRoot game)
    {
        _enemy = null;
        if (!HasForeignQueued())
        {
            Stop(game);
        }
        SetOwner(null, "");
        SetState(MinerState.FindTarget, L.T("MiningDrone/ThreatGone"));
    }

    /// <summary>A destroyed ship becomes junk but keeps its parts, so ExistsInSim alone stays true (Ship.cs:1387, AttackCommand.IsValid:197).</summary>
    private bool StillHostile(SimRoot sim, Ship? e) => e != null && e.ExistsInSim(sim) && !e.IsJunk && Ship.IsEnemiesWith(e);

    /// <summary>Offline invariant, run by tools/jitcheck: a killed target frees Guard for the next enemy.</summary>
    internal static string? SelfCheck()
    {
        if (ProductionSelfCheck() is string prod) return prod;
        if (RulesSelfCheck() is string rules) return rules;
        // Restored/old target vs the live work area (zone moved by a profile edit): far outside -> dropped; on the
        // edge (within hysteresis) -> kept.
        var zone = new Circle(new Vector2(0f, 0f), 3000f);
        if (!OutsideArea(new Vector2(20000f, 0f), zone, 1.5f)) return "target outside the live work area is kept";
        if (OutsideArea(new Vector2(4000f, 0f), zone, 1.5f)) return "target on the area edge is dropped (no hysteresis)";
        // Hunt leash: a chase just past the edge goes on; the ship or its target past 1.5x the radius ends it.
        if (HuntBeyondLeash(new Vector2(4000f, 0f), new Vector2(4400f, 0f), zone)) return "hunt breaks off inside the leash";
        if (!HuntBeyondLeash(new Vector2(5000f, 0f), null, zone)) return "hunt ship chases past the leash";
        if (!HuntBeyondLeash(new Vector2(0f, 0f), new Vector2(0f, 4600f), zone)) return "hunt follows a target past the leash";
        // Route: an enemy clear of the leg now but crossing it along its velocity (8 s) forces a detour that clears its
        // whole predicted path; the same enemy standing still does not.
        var wp = new List<Vector2>();
        World.ClearThreats();
        World.ThreatZones.Add(new Circle(new Vector2(5000f, 3000f), 1000f));
        World.ThreatVels.Add(new Vector2(0f, -400f));
        bool routed = World.Route(new Vector2(0f, 0f), new Vector2(10000f, 0f), wp);
        int detour = wp.Count;
        World.ThreatVels[0] = default;
        wp.Clear();
        bool still = World.Route(new Vector2(0f, 0f), new Vector2(10000f, 0f), wp);
        World.ClearThreats();
        if (!routed || detour == 0) return "route ignores a pursuer's predicted path";
        if (!still || wp.Count != 0) return "route detours around a static zone that is clear of the leg";
        return null;
    }

    /// <summary>Retreat episode start: predictive escape arc first, then home once clear (RetreatTick).</summary>
    private void Flee(GameRoot game, Ship? enemy)
    {
        _enemy = enemy;
        EndStuck();
        _evadeBy = ModuleType.Retreat;
        SetOwner(ModuleType.Retreat, enemy != null ? L.F("MiningDrone/PreemptEnemy", enemy.Metadata.PlainShipName) : L.F("MiningDrone/PreemptDamage", Pct(Hull)));
        Log.Info($"[{Id}] retreat: {(enemy != null ? "enemy " + enemy.Metadata.PlainShipName + " on radar" : "damaged")} (hull value {Hull * 100f:0}%); escaping (speed {World.MinerSpeed(Ship):0}/s)");
        _esc = default;
        _escPlanAt = 0;
        // Nothing on radar (damage retreat): no escape arc to fly, head home at once.
        _escSafeSince = enemy != null ? _now : _now - EscapeSafeMs - 1;
        _retreatHome = false;
        SetState(MinerState.Evading, RetreatStatus(home: false));
        RetreatTick(game, _now);
    }

    private static string Pct(float f) => (f * 100f).ToString("0");

    /// <summary>Retreat status: the pursuer (radar trigger) or the hull value (damage trigger).</summary>
    private string RetreatStatus(bool home) => Cfg.RetreatTrigger == 1 && _damaged
        ? L.F(home ? "MiningDrone/RetreatDamagedHome" : "MiningDrone/RetreatDamaged", Pct(Hull))
        : L.F(home ? "MiningDrone/FleeingBase" : "MiningDrone/Fleeing", _enemy?.Metadata.PlainShipName ?? "?");

    // ------------------------------------------------------------------ Retreat on damage / repair

    private const long RepairRetryMs = 30_000;
    private static Ship? s_repairShip; // ship of the last auto-repair input sent from this client

    /// <summary>
    /// One auto-repair input in flight across all ships: the input reserves no money locally (MPUpdateShipInput.cs:120-130),
    /// so the next affordability check must wait until the previous one executed. Its optimistic copy is queued on send
    /// (ExecuteLocalImmediate) and dequeued at execution whether applied or rejected (Execute:158-167), so the gate
    /// opens only on that; no timeout. A ship gone from this sim (another save / sim) releases it.
    /// </summary>
    private static bool RepairInFlight(SimRoot sim)
    {
        Ship? s = s_repairShip;
        if (s != null && s.ExistsInSim(sim, falseIfNoParts: false) && MPUpdateShipInput.HasPendingLocalOptimisticUpdates(s))
        {
            return true;
        }
        s_repairShip = null;
        return false;
    }
    private bool _damaged; // damage-retreat latch (persisted: RuntimeRecord.DamageLatch)
    private long _nextRepairAt;
    private readonly List<ID<ResourceRules>> _repairShort = new();

    /// <summary>Hull value integrity (0..1) at the last 1 s check; drives the damage trigger.</summary>
    public float Hull { get; private set; } = 1f;

    /// <summary>Damage trigger, every 1 s: hull value, latch below the threshold, release at <see cref="DamageRecover"/>.
    /// No movement here (runs before stuck recovery).</summary>
    private void UpdateDamage(long now)
    {
        if (now < _nextHullCheck)
        {
            return;
        }
        _nextHullCheck = now + EnemyCheckMs;
        Hull = HullIntegrity(Ship.Parts.TotalMoneyValue, Ship.Parts.RepairableParts.GetTotalCostToRepairDamagedParts(), Ship.DestroyedParts.TotalMoneyValue);
        if (Cfg.RetreatTrigger != 1)
        {
            _damaged = false;
        }
        else if (!_damaged && Hull < Cfg.RetreatHull)
        {
            _damaged = true;
            Log.Info($"[{Id}] retreat: hull value {Hull * 100f:0}% below {Cfg.RetreatHull * 100f:0}%; retreating until {DamageRecover(Cfg.RetreatHull) * 100f:0}%");
        }
        else if (_damaged && Hull >= DamageRecover(Cfg.RetreatHull))
        {
            _damaged = false;
            Log.Info($"[{Id}] retreat: hull value back to {Hull * 100f:0}%; resuming");
        }
    }

    private long _nextHullCheck;

    /// <summary>While the damage latch holds, Retreat owns movement (home, on the escape arc while enemies are near);
    /// it takes over a self-defence evade too. True when it took over.</summary>
    private bool DamageTick(GameRoot game, Ship? enemy)
    {
        // An escape already in progress (ours or Avoidance's) keeps the movement; the Evading state runs RetreatTick.
        if (!_damaged || (State == MinerState.Evading && _evadeBy is ModuleType.Retreat or ModuleType.Avoidance))
        {
            return false;
        }
        Flee(game, enemy);
        return true;
    }

    /// <summary>
    /// Opt-in repair, like the vanilla "repair all" button (BuildToolbox.OnRepairAllClicked): ShipUpdateInfo with
    /// RepairExisting | RebuildDestroyed, then MPUpdateShipInput(Repair | DestroyBlocking [| AllowPartialConstruction])
    /// (whitelisted in Career: CareerGameModeManager.cs:951). Only when no enemy has been on radar for the resume delay,
    /// the game allows construction (Career: no enemy fire nearby, CareerSimModeManager.cs:689-719), nothing is queued
    /// yet and the cost is affordable without debt; retried every 30 s.
    /// </summary>
    private void RepairTick(GameRoot game, long now, Ship? enemy)
    {
        if (!Cfg.RetreatRepair || !Ship.NeedsRepairs)
        {
            _repairHold = "";
        }
        if (!Cfg.RetreatRepair || enemy != null || now - _lastEnemySeen <= Cfg.RetreatResumeMs || now < _nextRepairAt
            || RepairInFlight(game.Sim) || Ledger.Any(Ledger.Kind.Money) || !Ship.NeedsRepairs || !Ship.IsConstructionLegal || Ship.OptionalConstructionManager?.HasAnyQueued == true)
        {
            return;
        }
        _nextRepairAt = now + RepairRetryMs;
        if (RepairDonorHold(game) is { } hold)
        {
            if (hold != _repairHold)
            {
                Log.Info($"[{Id}] repair: waiting ({hold}); retrying in {RepairRetryMs / 1000} s");
            }
            _repairHold = hold;
            return;
        }
        _repairHold = "";
        using ShipUpdateInfo sui = ShipUpdateInfo.Alloc(Ship);
        if (!sui.TryConstruct(ConstructionFlags.RepairExisting | ConstructionFlags.RebuildDestroyed, 0))
        {
            Log.Info($"[{Id}] repair: construction blocked; retrying in {RepairRetryMs / 1000} s");
            return;
        }
        // Partial construction (crew fetch resources) waives missing resources, never credits: buying resources still
        // costs money (MPUpdateShipInput.cs:213-222), and this never goes into debt.
        bool partial = game.Sim.Mode.AllowInsufficientResources(Ship);
        _repairShort.Clear();
        using (ShipCost cost = sui.GetTotalPhysicalCost(MPValueType.Displayed, null, null, returnFinalCost: true, isBuild: false))
        {
            if (!cost.CanLocalPlayerAfford(Ship, out bool noCredits, _repairShort, MPValueType.Displayed) && (noCredits || !partial))
            {
                Log.Info($"[{Id}] repair: not affordable without debt{(noCredits ? " (credits)" : " (resources)")}; retrying in {RepairRetryMs / 1000} s");
                return;
            }
        }
        Log.Info($"[{Id}] repair: queuing repair-all (hull value {Hull * 100f:0}%)");
        s_repairShip = Ship;
        game.NetManager.EnqueueInput(new MPUpdateShipInput(sui, UpdateShipFlags.Repair | UpdateShipFlags.DestroyBlocking | (partial ? UpdateShipFlags.AllowPartialConstruction : default)));
    }

    // ------------------------------------------------------------------ Self-defence

    private readonly Dictionary<Weapon, CommandID> _defIds = new();
    private Ship? _defTarget;
    private long _defLastHot, _nextDefendCheck;
    private string _defend = "";

    /// <summary>Status for the UI: the module status plus the self-defence note while it fires.</summary>
    public string StatusText => (_defend.Length == 0 ? Status : Status + " · " + _defend) + (_logi.Length == 0 ? "" : " · " + _logi)
        + (_repairHold.Length == 0 ? "" : " · " + _repairHold);

    // Guard fights itself; a hunting attack picks its own target.
    private bool DefendOn => Cfg.Defend.Active && !Cfg.Guard.Active && !(State == MinerState.Evading && _evadeBy == ModuleType.Hunt);
    // Avoidance and radar-trigger Retreat already escape; a damage Retreat covers threats only while its latch holds.
    private bool DefendEvadeOn => DefendOn && Cfg.DefendEvade && !Cfg.Avoid.Active && !(Cfg.Retreat.Active && (Cfg.RetreatTrigger == 0 || _damaged));

    /// <summary>
    /// Return fire without taking movement: the nearest visible enemy within weapons range (its or ours) becomes the
    /// explicit target of every operational weapon without a player-given one (MPEnqueueTargetsInput, whitelisted in
    /// Career: GameModeManager.cs:602; it changes only Weapon.ExplicitTargets, MPEnqueueTargetsInput.cs:109-135). Only
    /// changes are sent; our targets are removed (MPRemoveTargetsInput) after the resume delay without an enemy in range.
    /// </summary>
    private void DefendFireTick(GameRoot game, long now)
    {
        if (now < _nextDefendCheck)
        {
            return;
        }
        _nextDefendCheck = now + EnemyCheckMs;
        SimRoot sim = game.Sim;
        Ship? e = DefendOn && !Cfg.DefendEvade ? World.FindEnemyInRange(sim, Ship, now) : null;
        if (e != null)
        {
            _defLastHot = now;
            var input = new MPEnqueueTargetsInput(append: false, playLocalSound: false);
            var weapons = Ship.Weapons.OperationalWeapons;
            for (int i = 0; i < weapons.Count; i++)
            {
                Weapon w = weapons[i];
                // append:false replaces the weapon's whole queue: only weapons holding nothing but our own target.
                bool hasId = _defIds.TryGetValue(w, out CommandID id), foreign = false, onIt = false;
                foreach (WeaponTarget t in w.ExplicitTargets)
                {
                    if (hasId && t.UniqueID == id)
                    {
                        onIt |= ReferenceEquals(t.Target, e);
                    }
                    else
                    {
                        foreign = true;
                    }
                }
                if (foreign || onIt)
                {
                    continue; // a player-given target (queued ones too), or already on this enemy
                }
                var wt = new WeaponTarget(w, e, Ship.Weapons.AllocLocalTargetID());
                _defIds[w] = wt.UniqueID;
                input.Targets.Add((w, wt));
            }
            if (input.Targets.Count > 0)
            {
                Log.Info($"[{Id}] self-defence: {input.Targets.Count} weapon(s) on {e.Metadata.PlainShipName}");
                game.NetManager.EnqueueInput(input);
            }
            _defTarget = e;
            _defend = L.F("MiningDrone/DefendFiring", e.Metadata.PlainShipName);
            return;
        }
        if (_defIds.Count > 0 && (!DefendOn || Cfg.DefendEvade || now - _defLastHot > Cfg.DefendResumeMs))
        {
            ReleaseDefence(game);
        }
    }

    /// <summary>Removes our self-defence weapon targets (player-given ones are never touched). Also on unassign.</summary>
    public void ReleaseDefence(GameRoot game)
    {
        var input = new MPRemoveTargetsInput();
        foreach (KeyValuePair<Weapon, CommandID> kv in _defIds)
        {
            if (kv.Key.ExplicitTargets[kv.Value] != null)
            {
                input.Targets.Add((kv.Key, kv.Value));
            }
        }
        if (input.Targets.Count > 0)
        {
            Log.Info($"[{Id}] self-defence: releasing {input.Targets.Count} weapon target(s){(_defTarget != null ? " on " + _defTarget.Metadata.PlainShipName : "")}");
            game.NetManager.EnqueueInput(input);
        }
        _defIds.Clear();
        _defTarget = null;
        _defend = "";
    }
    // ------------------------------------------------------------------ command issue / ownership

    private void Issue(GameRoot game, Command cmd, bool autoCancelSalvage)
    {
        _lastCmd = cmd;
        _lastCmdId = cmd.UniqueID;
        _expectType = cmd.GetType();
        _expectTarget = cmd is BaseFollowCommand f && f.HasTarget ? f.Target : null;
        _ownSeen = false;
        _ownIds.Clear(); // append:false replaces the whole queue
        _ownIds.Add(_lastCmdId);
        _lastIssueAt = _now;
        Log.Debug(Id, $"issue {_expectType.Name} id {_lastCmdId} target {_expectTarget?.Metadata.PlainShipName ?? "-"} (state {State}, owner {Owner?.ToString() ?? "-"})");
        game.NetManager.EnqueueInput(new MPEnqueueCommandsInput(cmd, append: false, playLocalSound: false)
        {
            AutoCancelSalvageJobs = autoCancelSalvage,
        });
    }

    private void Append(GameRoot game, Command cmd)
    {
        _ownIds.Add(cmd.UniqueID);
        _lastCmd = cmd;
        _lastCmdId = cmd.UniqueID;
        game.NetManager.EnqueueInput(new MPEnqueueCommandsInput(cmd, append: true, playLocalSound: false) { AutoCancelSalvageJobs = false });
    }

    private bool IsOurCommand(Command? cmd) => cmd != null && _ownIds.Contains(cmd.UniqueID);

    // ------------------------------------------------------------------ bindings

    private Ship? _anchorCache;
    private string? _anchorResolvedFor;
    private long _anchorRetryAt;

    /// <summary>Anchor with a 2 s retry cache. A missing anchor is never replaced by another ship.</summary>
    private Ship? ResolveAnchor(GameRoot game)
    {
        string? id = Cfg.Anchored ? Cfg.AnchorId : null;
        if (id == null)
        {
            AnchorShip = _anchorCache = null;
            return null;
        }
        if (_anchorCache != null && _anchorResolvedFor == id && _anchorCache.ExistsInSim(game.Sim))
        {
            return AnchorShip = _anchorCache;
        }
        if (_anchorResolvedFor == id && _now < _anchorRetryAt)
        {
            return AnchorShip = null;
        }
        _anchorResolvedFor = id;
        _anchorCache = World.FindFriendlyShip(game, id, Ship);
        _anchorRetryAt = _now + 2000;
        return AnchorShip = _anchorCache;
    }

    /// <summary>Offload target binding (explicit, or the anchor when OffloadToAnchor is on).</summary>
    private Ship? ResolveBase(GameRoot game)
    {
        string? id = Cfg.OffloadId;
        if (string.IsNullOrEmpty(id))
        {
            _base = null;
            return null;
        }
        SimRoot sim = game.Sim;
        if (_base != null && _baseResolvedFor == id && _base.ExistsInSim(sim))
        {
            return _base;
        }
        if (_baseResolvedFor == id && _now < _baseRetryAt)
        {
            return null; // recently not found; don't rescan every tick
        }
        _baseResolvedFor = id;
        _base = World.FindFriendlyShip(game, id, Ship);
        _baseRetryAt = _now + 2000;
        return _base;
    }

    // ------------------------------------------------------------------ filters / blacklists

    private List<string>? _maskFor;
    private int _maskVersion = int.MinValue;
    private float _escortDist = float.NaN;
    private float _escortAngle = float.NaN;

    /// <summary>This ship's formation slot: index <paramref name="k"/> of <paramref name="n"/> among the local automated
    /// ships with formation on that follow the same anchor, by ordinal ship id (stable across ticks, reserved while a
    /// sibling works elsewhere).</summary>
    private void FormationSlot(out int k, out int n)
    {
        k = 0;
        n = 0;
        IReadOnlyList<ShipAgent> all = MinerManager.All;
        for (int i = 0; i < all.Count; i++)
        {
            AgentConfig c = all[i].Cfg;
            if (c.Formation && c.Anchored && c.AnchorId == Cfg.AnchorId)
            {
                n++;
                if (string.CompareOrdinal(all[i].Id, Id) < 0)
                {
                    k++;
                }
            }
        }
        n = Math.Max(n, 1);
    }

    private bool _noProfile;
    private ulong? _planMask;
    private ulong _maxMask; // collected types at/above keep-max (Logistics.AtMaxMask), refreshed every tick

    /// <summary>Types automatic harvesting takes now: the Collect filter minus types at keep-max. A change re-plans a
    /// running harvest (its salvage auto-collect set is stale).</summary>
    private ulong Harvest => _mask & ~_maxMask;

    /// <summary>Hash of the effective keep-on-board amounts last seen / last pushed to the game (MinerManager.SyncKeep).</summary>
    internal int KeepSeen, KeepPushed = int.MinValue;
    internal long KeepSeenAt;

    private byte[]? _ranks; // collect priority per resource (World.RankTable); null = nearest target
    private int _ranksVersion = int.MinValue;

    private void RefreshMask()
    {
        // Priority ranks follow the live profile; a reorder applies from the next target pick (no forced re-plan, D18).
        if (_ranksVersion != Cfg.Compiles)
        {
            _ranksVersion = Cfg.Compiles;
            _ranks = World.RankTable(Cfg.CollectPriority, Cfg.Collect == null ? World.StackableMask : World.MaskFromIds(Cfg.Collect));
        }
        // No allocation per tick: recomputed only when the cargo policy object or the config changes.
        if (Cfg.Collect == null)
        {
            _mask = World.StackableMask; // default: everything selected
            _maskFor = null;
            return;
        }
        // Profile edits change the Collect list in place: key on every recompile, not only the list identity.
        if (!ReferenceEquals(_maskFor, Cfg.Collect) || _maskVersion != Cfg.Compiles)
        {
            _mask = World.MaskFromIds(Cfg.Collect);
            _maskFor = Cfg.Collect;
            _maskVersion = Cfg.Compiles;
        }
    }

    /// <summary>One of our queued salvage commands auto-collects a different set than the live Collect filter.</summary>
    private bool OwnSalvageStale()
    {
        HashSet<ID<ResourceRules>>? want = null;
        List<Command> q = Ship.Commands._commands;
        for (int i = 0; i < q.Count; i++)
        {
            if (q[i] is SalvageCommand s && _ownIds.Contains(q[i].UniqueID))
            {
                want ??= BuildAutoCollect();
                if (s.AutoCollectResourceTypes == null || !s.AutoCollectResourceTypes.SetEquals(want))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private HashSet<ID<ResourceRules>> BuildAutoCollect()
    {
        var set = new HashSet<ID<ResourceRules>>();
        ResourceRules[] all = World.Resources;
        for (int i = 0; i < all.Length; i++)
        {
            ResourceRules r = all[i];
            if (!r.IsStackable || Ship.Metadata.IsResourceTypeBanned(r.ID))
            {
                continue;
            }
            if (i < 64 && (Harvest & (1UL << i)) != 0)
            {
                set.Add(r.ID);
            }
        }
        return set;
    }

    private void Blacklist(Ship target, long now) => Blacklist(target, now, BlacklistMs);

    private void Blacklist(Ship target, long now, long ms)
    {
        _blacklist[target] = now + ms;
        _exclude.Add(target);
    }

    private void PruneBlacklist(long now)
    {
        if (_nuggetBlacklist.Count > 0)
        {
            _expiredNuggets.Clear();
            foreach (var kv in _nuggetBlacklist)
            {
                if (kv.Value <= now)
                {
                    _expiredNuggets.Add(kv.Key);
                }
            }
            foreach (Nugget x in _expiredNuggets)
            {
                _nuggetBlacklist.Remove(x);
            }
        }
        if (_blacklist.Count == 0)
        {
            return;
        }
        _expired.Clear();
        foreach (var kv in _blacklist)
        {
            if (kv.Value <= now)
            {
                _expired.Add(kv.Key);
            }
        }
        foreach (Ship s in _expired)
        {
            _blacklist.Remove(s);
            _exclude.Remove(s);
        }
    }

    public void ExcludeClaimed(HashSet<Ship> claimedByOthers)
    {
        // Rebuilt by the manager each tick: avoid two miners piling onto the same rock.
        _exclude.Clear();
        foreach (var kv in _blacklist)
        {
            _exclude.Add(kv.Key);
        }
        foreach (Ship s in claimedByOthers)
        {
            if (s != Target)
            {
                _exclude.Add(s);
            }
        }
    }

    private void SetState(MinerState state, string status)
    {
        if (state == MinerState.Paused || state == MinerState.Manual || state == MinerState.Mining || state == MinerState.Approach
            || state == MinerState.CollectLoose || state == MinerState.Evading)
        {
            NoTargets = false; // "!" only while actually out of targets (also cleared by pause/manual/new work)
        }
        // Holding/Waiting -> FindTarget with the same text is the routine re-check: not worth a log line.
        if ((state != State || status != Status) && !(state == MinerState.FindTarget && status == Status))
        {
            Log.Debug(Id, "state " + state + ": " + status);
        }
        State = state;
        Status = status;
        if (state == MinerState.Offload)
        {
            _lastStoredAtOffloadCheck = int.MaxValue;
        }
    }
}
