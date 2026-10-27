using System;
using System.Collections.Generic;
using Cosmoteer;
using Cosmoteer.Crew;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Modes.Career;
using Cosmoteer.Modes.Career.Comms;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.AI;
using Cosmoteer.Ships.AI.CommsModules;
using Cosmoteer.Ships.Crew;
using Cosmoteer.Ships.Parts.Crew;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>Background logistics modules (Supply; Crew; Production): no movement, they run next to whichever module owns it.</summary>
internal sealed partial class ShipAgent
{
    private const long LogisticsEveryMs = 4000;
    private long _nextLogisticsAt;
    private string _logi = "";

    /// <summary>Within crew transfer range (MPMoveResourcesInput / MPSetCrewSourceInput / MPHireCrewInput all check
    /// LoseHomeShipWarningDistance between bounding circles).</summary>
    private bool InTransferRange(Ship other) =>
        Ship.DetWorldBoundingCircle.DistanceTo(other.DetWorldBoundingCircle) <= GameApp.Rules.Crew.LoseHomeShipWarningDistance;

    private void LogisticsTick(GameRoot game, long now)
    {
        if (now < _nextLogisticsAt)
        {
            return;
        }
        _nextLogisticsAt = now + LogisticsEveryMs;
        _logi = "";
        if (Cfg.Supply.Active)
        {
            SupplyTick(game);
        }
        if (Cfg.Crew.Active && Cfg.CrewRedistribute)
        {
            RedistributeTick(game);
        }
        if (Cfg.Crew.Active && Cfg.CrewHire && Cfg.CrewTarget > 0)
        {
            HireTick(game);
        }
        if (Cfg.Production.Active)
        {
            ProductionTick(game);
        }
    }

    private string _repairHold = "";

    /// <summary>
    /// Donor guard for the opt-in auto-repair (D11): vanilla construction draws materials from every same-player ship
    /// within LoseHomeShipWarningDistance (ResourceManager.GetUsableConstructionResourceShips :3868) without looking at
    /// resource desire (:2617), and ships assisting construction (default Owned, ShipMetadata.cs:400) lend crew. So our
    /// repair waits while such a ship holds a protected type (keep-all, desire or effective keep-min, Logistics.Floor) or
    /// keeps a Crew minimum aboard and assists. Returns the reason, or null.
    /// </summary>
    private string? RepairDonorHold(GameRoot game)
    {
        _near.Clear();
        Halfling.Geometry.Circle me = Ship.DetWorldBoundingCircle;
        game.Sim.Ships.DetFindIntersectingWith(new Halfling.Geometry.Circle(me.Center, me.Radius + GameApp.Rules.Crew.LoseHomeShipWarningDistance),
            _near, approximate: false, junk: false, s => s != Ship && s.IsSamePlayerAs(Ship));
        World.EnsureResources();
        foreach (Ship o in _near)
        {
            ShipAgent? oa = MinerManager.Get(o);
            CargoPolicy? cargo = oa?.Assignment.Cargo;
            foreach (ResourceRules r in World.Resources)
            {
                if (Logistics.Floor(o, r.ID, cargo) > 0) // empty now still counts: deliveries arrive during the repair
                {
                    return L.F("MiningDrone/RepairHoldStock", o.Metadata.PlainShipName);
                }
            }
            if (oa != null && oa.Cfg.Crew.Active && oa.Cfg.CrewKeep > 0 && o.Metadata.ConstructionAssistance.Displayed != ConstructionAssistMode.Off)
            {
                return L.F("MiningDrone/RepairHoldCrew", o.Metadata.PlainShipName);
            }
        }
        return null;
    }

    private readonly List<Ship> _near = new();
    private readonly List<(CrewSource Source, int Free)> _beds = new();

    /// <summary>
    /// Capped hiring: tops this ship up to its crewTarget (beds, incoming crew counted) from a crew provider within
    /// LoseHomeShipWarningDistance, never travelling to one. Mirrors the vanilla hire tab (CommHireTab: auto-hire
    /// :687-720, MPHireCrewInput.Execute :337-397, whitelisted in Career, CareerGameModeManager.cs:951): provider willing
    /// (WillProvideCrew), fame prerequisite and cost per extra crew, a reachable bed by role priority. Spends only
    /// credits above the `credits` floor, never debt. One hire input in flight per client and none while an auto-repair
    /// input is (both spend the same credits): automatic top-up to crewTarget, not a total spending cap.
    /// </summary>
    private void HireTick(GameRoot game)
    {
        if (game.Mode is not CareerGameModeManager career || Ledger.Any(Ledger.Kind.Money) || RepairInFlight(game.Sim))
        {
            return;
        }
        int want = Math.Min(CrewWanted(Cfg.CrewTarget, Ship.Crew.TotalCrewCapacity, Ship.Crew.Assigned.Count, Ledger.Sum(Ledger.Kind.ShipIn, Ship)),
            Ship.Crew.RemainingCrewCapacity - Ledger.Sum(Ledger.Kind.ShipIn, Ship));
        if (want <= 0)
        {
            return;
        }
        _beds.Clear();
        foreach (CrewSource s in CrewSource.GetAllOnShip(Ship))
        {
            int free = s.RemainingCapacity - Ledger.Sum(Ledger.Kind.Bed, s);
            if (free > 0)
            {
                _beds.Add((s, free));
            }
        }
        _beds.Sort(static (a, b) => b.Source.Role.AssignmentPriority.CompareTo(a.Source.Role.AssignmentPriority));
        if (_beds.Count == 0)
        {
            return;
        }
        _near.Clear();
        float range = GameApp.Rules.Crew.LoseHomeShipWarningDistance;
        Halfling.Geometry.Circle me = Ship.DetWorldBoundingCircle;
        game.Sim.Ships.DetFindIntersectingWith(new Halfling.Geometry.Circle(me.Center, me.Radius + range), _near, approximate: false, junk: false,
            s => s.AI != null && !s.IsEnemiesWith(Ship));
        _near.Sort(static (a, b) => a.UniqueID.CompareTo(b.UniqueID));
        int budget = HireBudget(game.Mode.GetSpendableMoney(game.LocalPlayerIndex), Cfg.CrewCredits, Ledger.Sum(Ledger.Kind.Money, null));
        int total = career.GetTotalPlayerCrew(game.LocalPlayerIndex);
        int maxCrew = career.GetCurrentMaxCrew();
        foreach (Ship from in _near)
        {
            if (from.DetWorldBoundingCircle.DistanceTo(me) > range)
            {
                continue;
            }
            foreach (AICommsModule m in from.AI!.CommsModules)
            {
                if (m is not AICrewProviderModule p || p.WillProvideCrew(Ship, from) != true || p.CrewForHire.Count == 0)
                {
                    continue;
                }
                var input = new CommHireTab.MPHireCrewInput(from, Ship);
                int cost = 0;
                foreach (CrewSoul c in p.CrewForHire)
                {
                    if (input.Crew.Count >= want)
                    {
                        break;
                    }
                    if (c.HomeShip != from)
                    {
                        continue; // Execute rejects the whole input otherwise (CommHireTab.cs:357)
                    }
                    int n = total + input.Crew.Count + 1;
                    int? fame = career.GetFamePrereq(n);
                    if (!fame.HasValue || fame > career.PlayerFame)
                    {
                        break; // later crew only cost more fame
                    }
                    int price = p.GetHireCost(game, from, n, maxCrew);
                    if (cost + price > budget)
                    {
                        break;
                    }
                    int bed = _beds.FindIndex(b => b.Free > 0 && c.HasPathTo(b.Source));
                    if (bed < 0)
                    {
                        continue;
                    }
                    _beds[bed] = (_beds[bed].Source, _beds[bed].Free - 1);
                    input.Crew.Add((c, _beds[bed].Source));
                    cost += price;
                }
                if (input.Crew.Count == 0)
                {
                    continue;
                }
                Func<bool> done = Ledger.Barrier(game);
                Ledger.Reserve(Ledger.Kind.Money, null, cost, done);
                Ledger.Reserve(Ledger.Kind.ShipIn, Ship, input.Crew.Count, done);
                foreach ((CrewSoul? c, CrewSource? s) in input.Crew)
                {
                    Ledger.Reserve(Ledger.Kind.Soul, c, 1, done);
                    Ledger.Reserve(Ledger.Kind.Bed, s, 1, done);
                }
                Log.Info($"[{Id}] crew: hiring {input.Crew.Count} from {from.Metadata.PlainShipName} for {cost} credits");
                _logi = L.F("MiningDrone/CrewHiring", input.Crew.Count, from.Metadata.PlainShipName);
                game.NetManager.EnqueueInput(input);
                return; // one hire input in flight per client
            }
        }
    }

    private readonly List<CrewSoul> _crewPick = new();

    /// <summary>
    /// Crew redistribution: gives crew above our `keep` (counted aboard) to the anchor while the anchor's own Crew module
    /// wants more (its crewTarget, capped by beds), like the vanilla transfer window: one MPSetCrewHomeShipInput. Crew and
    /// per-ship in/out counts are held in the Ledger until the input has executed.
    /// </summary>
    private void RedistributeTick(GameRoot game)
    {
        Ship? r = ResolveAnchor(game);
        ShipAgent? ra = r != null ? MinerManager.Get(r) : null;
        if (r == null || ra == null || !r.IsSamePlayerAs(Ship) || !ra.Cfg.Crew.Active || ra.Cfg.CrewTarget <= 0 || !InTransferRange(r))
        {
            return;
        }
        int n = Math.Min(
            CrewWanted(ra.Cfg.CrewTarget, r.Crew.TotalCrewCapacity, r.Crew.Assigned.Count, Ledger.Sum(Ledger.Kind.ShipIn, r)),
            CrewSpare(Ship.Crew.Assigned.Count - Ship.Crew.OffShipAssigned.Count, Ledger.Sum(Ledger.Kind.ShipOut, Ship), Cfg.CrewKeep));
        // Mirrors the vanilla transfer window (CrewAndResourceTransferWindow.DoCrewTransfer): both ships need an airlock.
        if (n <= 0 || !Airlock.HasOnShip(Ship) || !Airlock.HasOnShip(r))
        {
            return;
        }
        _crewPick.Clear();
        foreach (CrewSoul c in Ship.Crew.Assigned)
        {
            // Aboard, next to an airlock, carrying nothing, not already on the move (window's _CanTransfer).
            if (c.Form is ShipCrew sc && sc.Ship == Ship && sc.CurrentPart != null && Airlock.HasContiguousWith(sc.CurrentPart, sc.CurrentCell)
                && c.CarriedResource.Quantity <= 0 && !Ledger.Has(Ledger.Kind.Soul, c))
            {
                _crewPick.Add(c);
            }
        }
        // Least important role first, then crew id (stable).
        _crewPick.Sort(static (a, b) =>
        {
            int c = (a.Role?.AssignmentPriority ?? int.MinValue).CompareTo(b.Role?.AssignmentPriority ?? int.MinValue);
            return c != 0 ? c : a.UniqueID.CompareTo(b.UniqueID);
        });
        if (_crewPick.Count > n)
        {
            _crewPick.RemoveRange(n, _crewPick.Count - n);
        }
        if (_crewPick.Count == 0)
        {
            return;
        }
        // MPSetCrewHomeShipInput (whitelisted, GameModeManager.cs:602; checks range and command rights, :80-98) moves the
        // home ship only, never swaps anyone back (unlike MPSetCrewSourceInput.cs:95-110 when a bed got taken meanwhile);
        // receiver room is ours to keep: CrewWanted stays within its beds, incoming counted.
        Func<bool> done = Ledger.Barrier(game);
        foreach (CrewSoul c in _crewPick)
        {
            Ledger.Reserve(Ledger.Kind.Soul, c, 1, done);
        }
        Ledger.Reserve(Ledger.Kind.ShipIn, r, _crewPick.Count, done);
        Ledger.Reserve(Ledger.Kind.ShipOut, Ship, _crewPick.Count, done);
        game.NetManager.EnqueueInput(new MPSetCrewHomeShipInput(_crewPick, r));
        Log.Info($"[{Id}] crew: sending {_crewPick.Count} crew to {r.Metadata.PlainShipName}");
        _logi = L.F("MiningDrone/CrewSending", _crewPick.Count, r.Metadata.PlainShipName);
    }

    /// <summary>
    /// Supply: tops the anchor up to its keep-on-board amounts (vanilla desire or its profile keep-min) from our surplus
    /// above our own floor, through the same crew transfer path as Offload (MPMoveResourcesInput, TradeType.None,
    /// whitelisted: GameModeManager.cs:602). Receiver deficit counts anticipated deliveries, so several donors and
    /// re-queues never fill the same shortage twice.
    /// </summary>
    private void SupplyTick(GameRoot game)
    {
        Ship? r = ResolveAnchor(game);
        // Same owner only: another client's optimistic jobs on an allied anchor are invisible here (double fill).
        if (r == null || !r.IsSamePlayerAs(Ship) || !InTransferRange(r))
        {
            return;
        }
        ShipAssignment? ra = Store.Find(r.UniqueID.ToString());
        CargoPolicy? rc = ra != null && ra.Enabled ? ra.Cargo : null;
        int queued = World.QueueTransfer(game, Ship, r, World.OffloadMask(Cfg.KeepAll), out _, Assignment.Cargo, rc, fillToTarget: true);
        if (queued > 0)
        {
            Log.Info($"[{Id}] supply: {queued} unit(s) queued to {r.Metadata.PlainShipName}");
            _logi = L.F("MiningDrone/Supplying", r.Metadata.PlainShipName);
        }
    }
}
