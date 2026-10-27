using System.Collections.Generic;
using Cosmoteer.GalaxyMap;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Special;

namespace MiningDrone;

/// <summary>
/// Fleet jump = the vanilla player flow for many ships at once. The course is plotted on the galaxy map
/// (MPSetDestinationMapNodeInput, MapFtlJumpState.cs:179); every checked ship gets the order a right-click on the route's
/// first gate gives (FtlGate.CreateCommand, FtlGate.cs:156; a gate in stasis: MoveCommand with TargetObjectID,
/// CommandManager.cs:860, converted by MoveCommand.ReplaceMoveCommandsWith). Drives charge only at the gate
/// (FtlManager.cs:322); the jump itself stays the vanilla Jump button (MPReadyFtlJumpInput -> GameRoot.DoHumanPlayersFtlJump).
/// Only MPEnqueueCommandsInput is sent (base whitelist), from the owner's client.
/// </summary>
internal static partial class FleetJump
{
    // Stasis-gate approaches we issued, keyed to the course they serve: vanilla would turn them into a gate order that
    // re-plots that course (FtlGateJumpCommand.cs:125), so they are stopped when the course is cancelled or changed.
    private static readonly Dictionary<Ship, CommandID> Approaches = new();
    private static readonly HashSet<Ship> Removing = new();
    private static readonly List<Ship> Done = new();
    private static MapNode? _approachDest;

    internal static string Reason(JumpBlock b) => b switch
    {
        JumpBlock.NoFtl => L.T("MiningDrone/JumpNoFtl"),
        JumpBlock.NoControl => L.T("MiningDrone/JumpNoControl"),
        _ => L.T("MiningDrone/JumpNoFuel"),
    };

    /// <summary>The plotted destination and the gate of its first hop (null gate: none in this sector).</summary>
    internal static MapNode? Destination(GameRoot game, out MetaFtlGate? gate)
    {
        gate = null;
        MapNode? dest = game.DestinationMapNode.Confirmed;
        if (dest == null || game.IsDoingNodeJump)
        {
            return null;
        }
        MapNode? first = game.Map.Nodes.GetFirstNodeInPathTo(dest, requireJumpable: true);
        gate = first == null ? null : FtlGate.GetGateForDestination(game.Sim, first);
        return dest;
    }

    /// <summary>
    /// Orders every ship to the route's first gate in one input, or none when any ship cannot make the whole route
    /// (problems then lists "name: reason"). Returns the number of ships ordered.
    /// </summary>
    internal static int Send(GameRoot game, List<Ship> ships, List<string> problems)
    {
        problems.Clear();
        if (game._saveHandler.IsSavingAsync)
        {
            problems.Add(L.T("MiningDrone/JumpSaving")); // no queue change while the worker serializes the sim
            return 0;
        }
        MapNode? dest = Destination(game, out MetaFtlGate? gate);
        if (dest == null || gate is not MetaFtlGate g)
        {
            problems.Add(L.T(dest == null ? "MiningDrone/JumpNoCourse" : "MiningDrone/JumpNoGate"));
            return 0;
        }
        var path = new List<MapNode>();
        game.Map.Nodes.FindPathTo(dest, path, requireJumpable: true);
        for (int i = 0; i < ships.Count; i++)
        {
            Ship s = ships[i];
            bool ftl = s.Ftl.HasFtlDrives;
            // Fuel for the whole route, as the vanilla status (FtlJumpGui.cs:107) and the jump (GameRoot.cs:773) count it.
            int cost = ftl && path.Count >= 2 ? s.Ftl.GetTotalFtlMapJumpFuelCost(path) : 0;
            JumpBlock b = Block(ftl, s.IsLocalPlayerShip && s.Commands.CanExecuteCommands, cost, ftl ? s.Ftl.GetTotalFuel() : 0);
            if (b != JumpBlock.None)
            {
                problems.Add(s.Metadata.PlainShipName + ": " + Reason(b));
            }
        }
        if (problems.Count > 0)
        {
            return 0;
        }
        long now = (long)((double)game.Sim.LogicalTime * 1000.0); // agent time base (MinerManager.Tick)
        var cmds = new List<Command>(ships.Count);
        if (!ReferenceEquals(dest, _approachDest))
        {
            Approaches.Clear();
            Removing.Clear();
            _approachDest = dest;
        }
        for (int i = 0; i < ships.Count; i++)
        {
            Ship s = ships[i];
            Command? c = g.Gate?.CreateCommand(s, isTentative: false);
            Removing.Remove(s); // a new order: a pending removal belongs to the replaced approach
            if (c == null)
            {
                c = new MoveCommand(s, s.Commands.AllocLocalCommandID(), g.DetWorldCenter, null, CollisionAvoidanceFlags.All, isTentative: false)
                {
                    TargetObjectID = g.GateID,
                };
                Approaches[s] = c.UniqueID;
            }
            else
            {
                Approaches.Remove(s);
            }
            cmds.Add(c);
            MinerManager.Get(s)?.HoldForJump(now);
        }
        Log.Info($"fleet jump: {cmds.Count} ship(s) ordered to the gate to {g.DestinationNode?.Name} (course {dest.Name}, {(g.Gate != null ? "physical" : "stasis")} gate)");
        game.NetManager.EnqueueInput(new MPEnqueueCommandsInput(cmds, append: false, playLocalSound: true));
        return cmds.Count;
    }

    /// <summary>
    /// Per frame (owner client, not while saving): remove our stasis-gate approaches once their course was cancelled or
    /// changed — only that command (MPRemoveCommandInput), the rest of the player's queue stays.
    /// shortcut: a gate that materialises in the input-latency window before the removal runs still converts the approach and
    /// re-plots its first hop (FtlGateJumpCommand.cs:125) — the same race as a vanilla right-click on a stasis gate; upgrade
    /// only if players hit it (needs the removal and the course change in one MPMultiInput issued at cancel time).
    /// </summary>
    internal static void Frame(GameRoot game)
    {
        if (Approaches.Count == 0)
        {
            return;
        }
        bool courseGone = !ReferenceEquals(game.DestinationMapNode.Confirmed, _approachDest);
        Done.Clear();
        foreach (var (s, id) in Approaches)
        {
            Command? c = s.ExistsInSim(game.Sim) ? Find(s, id) : null;
            if (c == null)
            {
                Done.Add(s); // converted to the gate order, removed, replaced by the player, or gone with the old sim
            }
            else if (courseGone && Removing.Add(s))
            {
                Log.Info($"fleet jump: course changed; removing {s.Metadata.PlainShipName}'s gate approach");
                game.NetManager.EnqueueInput(new MPRemoveCommandInput(s, id, autoCancelSalvageJobs: false));
            }
        }
        for (int i = 0; i < Done.Count; i++)
        {
            Approaches.Remove(Done[i]);
            Removing.Remove(Done[i]);
        }
    }

    private static Command? Find(Ship s, CommandID id)
    {
        List<Command> q = s.Commands._commands;
        for (int i = 0; i < q.Count; i++)
        {
            if (q[i].UniqueID == id)
            {
                return q[i];
            }
        }
        return null;
    }
}
