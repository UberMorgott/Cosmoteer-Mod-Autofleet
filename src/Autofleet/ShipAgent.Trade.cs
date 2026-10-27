using System.Collections.Generic;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Special;
using Cosmoteer.Simulation;
using Halfling.Geometry;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>
/// Trade module, sell-only (D18): surplus the cargo Sell policy lets go (Logistics.Sellable) is sold to a discovered,
/// operating, non-hostile station of this sector through the vanilla trade transfer (MPMoveResourcesInput TradeType.Sell
/// at the current refund, whitelisted: GameModeManager.cs:602). Travel off = warehouse: only a station already in
/// transfer range. A station that refuses (AllowResourceTrade), takes nothing or stalls cools down and another one is
/// tried; cargo never leaves except as a sale. Sale money is never counted before the game pays it.
/// </summary>
internal sealed partial class ShipAgent
{
    private const long TradeStallMs = 120_000;

    private ObjectID? _station;
    private string _stationName = "";
    private readonly Dictionary<ObjectID, long> _stationSkip = new();
    private long _tradeProgressAt;
    private float _tradeBestDist;
    private int _tradeLeft;
    private long _tradeLegAt = -1; // _lastIssueAt of our station move leg

    /// <summary>Types the Sell policy may sell (any non-Never code; unknown codes sell nothing via Logistics.Code).</summary>
    private ulong SellMask()
    {
        ulong mask = 0;
        Dictionary<string, int>? sell = Assignment.Cargo.Sell;
        if (sell == null)
        {
            return 0;
        }
        foreach (string id in sell.Keys)
        {
            int i = World.IndexOf(new ID<ResourceRules>(id));
            if (i >= 0 && i < 64)
            {
                mask |= 1UL << i;
            }
        }
        return mask;
    }

    private int SellableTotal(ulong mask)
    {
        int n = 0;
        ResourceRules[] all = World.Resources;
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            if ((mask & (1UL << i)) != 0)
            {
                n += Logistics.Sellable(Ship, all[i].ID, Assignment.Cargo);
            }
        }
        return n;
    }

    private static bool Discovered(in MetaSpaceStation m) => m.Station?.IsDiscovered ?? m.StasisStation?.IsDiscovered ?? false;

    /// <summary>Nearest eligible station: one in transfer range first, else (travel on) the nearest discovered one.</summary>
    private bool PickStation(GameRoot game, long now, out MetaSpaceStation pick)
    {
        pick = default;
        bool found = false;
        float best = float.MaxValue;
        foreach (MetaSpaceStation m in SpaceStation.GetStations(game.Sim, onlyOperating: true))
        {
            if (!Discovered(m) || (_stationSkip.TryGetValue(m.StationID, out long until) && now < until))
            {
                continue;
            }
            Ship? s = m.PhysicalShip;
            if (s != null && s.IsEnemiesWith(Ship))
            {
                continue;
            }
            bool inRange = s != null && InTransferRange(s);
            if (!inRange && !Cfg.TradeTravel)
            {
                continue;
            }
            float d = inRange ? -1f : m.DetWorldCenter.DistanceSquaredTo(Ship.WorldCenter);
            if (d < best)
            {
                best = d;
                pick = m;
                found = true;
            }
        }
        return found;
    }

    private bool FindStation(GameRoot game, ObjectID id, out MetaSpaceStation found)
    {
        foreach (MetaSpaceStation m in SpaceStation.GetStations(game.Sim, onlyOperating: false))
        {
            if (m.StationID == id)
            {
                found = m;
                return true;
            }
        }
        found = default;
        return false;
    }

    /// <summary>Trade's proposal: active, something sellable, and an eligible station. Without one the reason is a blocker.</summary>
    private bool TradeCandidate(GameRoot game, long now, out MetaSpaceStation pick)
    {
        pick = default;
        if (!Cfg.Trade.Active || SellableTotal(SellMask()) <= 0)
        {
            return false;
        }
        if (PickStation(game, now, out pick))
        {
            return true;
        }
        string why = L.T(Cfg.TradeTravel ? "MiningDrone/TradeNoStation" : "MiningDrone/TradeNoStationNear");
        RuntimeBlocker = RuntimeBlocker.Length == 0 ? why : RuntimeBlocker + "; " + why;
        return false;
    }

    /// <summary>Trade outranks every active harvest module (equal priority: harvest keeps working).</summary>
    private bool TradeOutranksHarvest() =>
        !(Cfg.Mine.Active && Cfg.Mine.Priority >= Cfg.Trade.Priority) && !(Cfg.Loot.Active && Cfg.Loot.Priority >= Cfg.Trade.Priority);

    private void StartTrade(long now, in MetaSpaceStation m)
    {
        _station = m.StationID;
        _stationName = m.PhysicalShip?.Metadata.PlainShipName ?? L.T("MiningDrone/TradeStation");
        _tradeProgressAt = now;
        _tradeBestDist = float.MaxValue;
        _tradeLeft = int.MaxValue;
        _nextActionAt = 0;
        Target = null;
        if (!ReferenceEquals(_followShip, m.PhysicalShip))
        {
            _followShip = null; // an earlier follow (anchor, base) must not steer the trade leg checks (Routing); a reconciled station follow stays
        }
        if (_travelDest is Vector2 old && old.DistanceTo(m.DetWorldCenter) >= 300f)
        {
            _travelDest = null; // an earlier leg is not the trade leg
            _travelShip = null;
        }
        Log.Info($"[{Id}] trade: selling surplus at {_stationName} ({m.StationID}), {m.DetWorldCenter.DistanceTo(Ship.WorldCenter):0} away");
        SetOwner(ModuleType.Trade, Owner is ModuleType.Mine or ModuleType.Loot ? L.T("MiningDrone/TradeReason") : "");
        SetState(MinerState.Trading, L.F("MiningDrone/TradeFlying", _stationName));
    }

    /// <summary>Leaves the trade: optional station cooldown, our own movement to/at the station stopped (player orders
    /// left to manual detection), then re-plan.</summary>
    private void EndTrade(long now, bool cooldown, string why)
    {
        if (cooldown && _station is ObjectID id)
        {
            _stationSkip[id] = now + Cfg.TradeCooldownMs;
            Log.Info($"[{Id}] trade: {_stationName} skipped for {Cfg.TradeCooldownMs / 1000} s ({why})");
        }
        if (_game != null && Ship.ExistsInSim(_game.Sim) && !HasForeignQueued())
        {
            Stop(_game);
        }
        _travelDest = null;
        _travelShip = null;
        _station = null;
        SetOwner(null, "");
        SetState(MinerState.FindTarget, why);
    }

    private void DoTrade(GameRoot game, long now)
    {
        if (_station is not ObjectID id || !FindStation(game, id, out MetaSpaceStation m) || !m.IsOperating)
        {
            EndTrade(now, _station != null, L.T("MiningDrone/TradeStationLost"));
            return;
        }
        Ship? st = m.PhysicalShip;
        if (st != null)
        {
            _stationName = st.Metadata.PlainShipName;
        }
        ulong mask = SellMask();
        int left = SellableTotal(mask);
        bool pending = st != null && World.HasPendingOffload(game, Ship, st);
        if (left <= 0 && !pending)
        {
            Log.Info($"[{Id}] trade: done at {_stationName}");
            EndTrade(now, false, L.F("MiningDrone/TradeDone", _stationName));
            return;
        }
        if (st == null || !InTransferRange(st))
        {
            if (!Cfg.TradeTravel)
            {
                EndTrade(now, false, L.T("MiningDrone/TradeNoStationNear")); // warehouse moved away: never chase
                return;
            }
            TradeTravel(game, now, m, st);
            return;
        }
        HoldAtStation(game, now, st);
        if (now < _nextActionAt)
        {
            return;
        }
        _nextActionAt = now + OffloadRequeueMs;
        // Same gate the input checks on execution (MPMoveResourcesInput.cs:133-137): enemies near, not operating, FTL.
        if (!game.Sim.Mode.AllowResourceTrade(Ship, st))
        {
            EndTrade(now, true, L.F("MiningDrone/TradeRefused", _stationName));
            return;
        }
        int queued = left > 0 ? World.QueueTransfer(game, Ship, st, mask, out _, Assignment.Cargo, sell: true) : 0;
        if (queued > 0 || left < _tradeLeft)
        {
            _tradeProgressAt = now;
        }
        _tradeLeft = left;
        if (queued == 0 && !pending)
        {
            // In range, willing, yet nothing could be queued: no room, no refund, no crew path. Keep the cargo.
            EndTrade(now, true, L.F("MiningDrone/TradeFull", _stationName));
            return;
        }
        if (now - _tradeProgressAt > TradeStallMs)
        {
            EndTrade(now, true, L.F("MiningDrone/TradeStalled", _stationName));
            return;
        }
        Status = L.F("MiningDrone/TradeSelling", _stationName, left);
    }

    /// <summary>In range: our previous movement (anchor follow, a harvest leg) must not pull the ship away while crews
    /// carry the sale. Travel on: follow the station closely; warehouse: stop where it is. Player orders are left alone.</summary>
    private void HoldAtStation(GameRoot game, long now, Ship st)
    {
        Command? cur = Ship.Commands.CurDisplayedCommand;
        if (cur == null || !IsOurCommand(cur) || now - _lastIssueAt <= IssueGraceMs)
        {
            return;
        }
        if (Cfg.TradeTravel)
        {
            if (!OwnFollowQueued(st))
            {
                IssueFollow(game, st, new FollowCommand(Ship, Ship.Commands.AllocLocalCommandID(), st, CollisionAvoidanceFlags.All, isTentative: false, waitForCrew: true) { EdgeToEdgeFollowRadius = 60f }, "at station");
            }
        }
        else if (!(cur is MoveCommand && cur.GetDestination().Location.DistanceTo(Ship.WorldCenter) < 300f))
        {
            Stop(game); // a stop is a MoveCommand to where we stand: accepted above on the next pass
        }
    }

    /// <summary>Our own Follow of the station is queued (current, or the last leg after waypoints): route provenance, so an
    /// escape or other command that replaced the queue is never taken for the station route.</summary>
    private bool OwnFollowQueued(Ship st)
    {
        List<Command> q = Ship.Commands._commands;
        for (int i = 0; i < q.Count; i++)
        {
            if (q[i] is FollowCommand f && f.HasTarget && f.Target == st && _ownIds.Contains(q[i].UniqueID))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Re-adopts a sale in progress from the save (station by id); false = plan fresh.</summary>
    private bool ReconcileTrade(GameRoot game, RuntimeRecord r)
    {
        if (r.StationId == null || !Cfg.Trade.Active)
        {
            return false;
        }
        foreach (MetaSpaceStation m in SpaceStation.GetStations(game.Sim, onlyOperating: true))
        {
            if (m.StationID.ToString() == r.StationId)
            {
                StartTrade(_now, m);
                Log.Info($"[{Id}] reconcile: sale at {_stationName} continues (pending transfers counted, not re-queued)");
                return true;
            }
        }
        return false;
    }

    /// <summary>Flies to the station: follow the live station ship, or move to a stasis one until it spawns. No progress
    /// for <see cref="TradeStallMs"/> (or no route) cools the station down.</summary>
    private void TradeTravel(GameRoot game, long now, in MetaSpaceStation m, Ship? st)
    {
        float d = m.DetWorldCenter.DistanceTo(Ship.WorldCenter);
        if (d < _tradeBestDist - 200f)
        {
            _tradeBestDist = d;
            _tradeProgressAt = now;
        }
        if (now - _tradeProgressAt > TradeStallMs)
        {
            EndTrade(now, true, L.F("MiningDrone/TradeStalled", _stationName));
            return;
        }
        Command? cur = Ship.Commands.CurDisplayedCommand;
        bool ours = st != null
            ? IsOurCommand(cur) && OwnFollowQueued(st)
            : IsOurCommand(cur) && cur is MoveCommand && !cur.IsSatisfied() && _travelDest is Vector2 td && td.DistanceTo(m.DetWorldCenter) < 300f
                && _lastIssueAt == _tradeLegAt; // our station route only: nothing (escape, re-plan) issued since
        if (!ours && now - _lastIssueAt > IssueGraceMs)
        {
            if (st != null)
            {
                IssueFollow(game, st, new FollowCommand(Ship, Ship.Commands.AllocLocalCommandID(), st, CollisionAvoidanceFlags.All, isTentative: false, waitForCrew: true) { EdgeToEdgeFollowRadius = 60f }, "to station");
            }
            else
            {
                _followShip = null; // a move leg: no follow metadata may steer Routing's checks
                if (!IssueTravel(game, m.DetWorldCenter, new MoveCommand(Ship, Ship.Commands.AllocLocalCommandID(), m.DetWorldCenter, null, CollisionAvoidanceFlags.All, isTentative: false), autoCancelSalvage: true, null))
                {
                    EndTrade(now, true, L.T("MiningDrone/RouteBlocked"));
                    return;
                }
                _tradeLegAt = _lastIssueAt;
            }
        }
        Status = L.F("MiningDrone/TradeFlying", _stationName);
    }
}
