using System;
using System.Collections.Generic;
using Cosmoteer.Game;
using Cosmoteer.Ships;
using Cosmoteer.Simulation;
using Cosmoteer.Simulation.Overlays;
using Halfling.Geometry;
using Halfling.Graphics;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>
/// Owns all ship agents for the current GameRoot/SimRoot, binds the fleet state to the loaded save, captures it when
/// the game saves, and draws area circles + state icons. Lifetime is bound to object identity (GameRoot and SimRoot
/// references), not to "which app state is on top", so menus/dialogs over the game do not tear it down.
/// </summary>
internal static class MinerManager
{
    private const long TickIntervalMs = 333; // ~3 Hz logic tick

    private static readonly Dictionary<Ship, ShipAgent> AgentsByShip = new();
    private static readonly List<ShipAgent> Agents = new();
    private static readonly List<ShipAgent> Removed = new();
    private static readonly HashSet<Ship> Claimed = new();

    private static GameRoot? _game;
    private static SimRoot? _sim;
    private static long _nextTick;
    private static bool _drawFailed;
    private static bool _boundToSave;

    // save hook state
    private static object? _lastSaveFileRef;
    private static string? _captured;

    public static GameRoot? Game => _game;
    public static IReadOnlyList<ShipAgent> All => Agents;

    /// <summary>Area being edited in the UI for a not-yet-assigned ship (drawn as a preview).</summary>
    public static Circle? PreviewArea { get; set; }

    public static ShipAgent? Get(Ship ship) => AgentsByShip.TryGetValue(ship, out var c) ? c : null;

    public static void OnGameAttached(GameRoot game)
    {
        _game = game;
        _sim = null;
        _boundToSave = false;
        _lastSaveFileRef = game.LastSaveFile;
        _captured = null;
        _wasSaving = false;
        Clear();
    }

    public static void OnGameDetached()
    {
        UnhookOverlay();
        Store.FlushIfDue(force: true);
        _game = null;
        _sim = null;
        Clear();
    }

    /// <summary>Called every frame; does only reference compares unless the 3 Hz tick is due.</summary>
    public static void Frame(long now)
    {
        GameRoot? game = _game;
        if (game == null)
        {
            return;
        }
        SaveHook(game);
        SimRoot? sim = game.Sim;
        if (sim == null)
        {
            return;
        }
        if (!ReferenceEquals(sim, _sim))
        {
            OnSimChanged(game, sim);
        }
        if (!Saving(game))
        {
            FleetJump.Frame(game);
        }
        World.DecodeStasis(game); // save-derived stasis index, decoded on the game's worker queue
        if (now < _nextTick || Saving(game))
        {
            return;
        }
        _nextTick = now + TickIntervalMs;
        Tick(game, sim, now);
    }

    /// <summary>
    /// Exact-save binding. GameSaveHandler.SaveAs sets LastSaveTime when a save starts and assigns a NEW LastSaveFile
    /// object when it has finished writing (GameSaveHandler.cs:412-425). The fleet state (with runtime provenance) is
    /// captured on the main thread when the save starts and written as that save's snapshot once the file exists.
    /// </summary>
    private static void SaveHook(GameRoot game)
    {
        GameSaveHandler h = game._saveHandler;
        // TrySaveAsAsync sets IsSavingAsync on the main thread before the worker serializes the game
        // (GameSaveHandler.cs:233-265). Capture exactly once at that rising edge; automation is frozen while it is set
        // (see Saving), so no own command or assignment change happens between the capture and the serialization.
        // LastSaveTime is NOT a capture trigger: OnGameRootPushed (GameSaveHandler.cs:430) also bumps it.
        if (h.IsSavingAsync && _captured == null)
        {
            Capture();
        }
        object? cur = game.LastSaveFile;
        if (!ReferenceEquals(cur, _lastSaveFileRef))
        {
            _lastSaveFileRef = cur;
            if (game.LastSaveFile is { } path)
            {
                if (_captured == null)
                {
                    Capture(); // synchronous SaveAs on the main thread: nothing ran since it serialized
                }
                Store.WriteSaveSnapshot(path.ToString(), _captured!);
            }
            _captured = null;
        }
        if (!h.IsSavingAsync && _wasSaving)
        {
            _captured = null; // save failed or was cancelled: drop the capture, never bind it to a later save
        }
        _wasSaving = h.IsSavingAsync;
    }

    private static bool _wasSaving;

    /// <summary>True while the game serializes a save on its worker: agents must not tick.</summary>
    private static bool Saving(GameRoot game) => game._saveHandler.IsSavingAsync;

    private static void Capture()
    {
        CaptureRuntime();
        _captured = Store.CaptureState();
        Store.ClearRuntime();
    }

    private static void CaptureRuntime()
    {
        for (int i = 0; i < Agents.Count; i++)
        {
            Agents[i].Assignment.Runtime = Agents[i].Capture();
        }
    }

    public static ShipAgent Assign(Ship ship, ShipAssignment a)
    {
        bool wasEnabled = a.Enabled;
        a.Enabled = true;
        a.OwnerIndex = ship.Metadata.PlayerIndex;
        Store.Touch(a);
        if (!wasEnabled && _game != null)
        {
            ApplyKeepMin(_game, ship, a);
        }
        if (AgentsByShip.TryGetValue(ship, out var existing))
        {
            existing.Resume();
            return existing;
        }
        var c = new ShipAgent(ship, ship.UniqueID.ToString(), a);
        if (a.UserPaused)
        {
            c.Resume();
        }
        AgentsByShip[ship] = c;
        Agents.Add(c);
        return c;
    }

    /// <summary>
    /// Live profile -> game: once the ship's effective keep-on-board amounts have been stable for 0.5 s (no input
    /// spam while the player clicks/drags) and differ from what was last pushed, push them. Runs from the owner's
    /// tick only; the game state changes only through NetManager inputs (lockstep-safe).
    /// </summary>
    private static void SyncKeep(GameRoot game, ShipAgent c, long now)
    {
        int sig = 17;
        if (c.Assignment.Cargo.KeepMin is { } keep)
        {
            foreach (var kv in keep)
            {
                sig += HashCode.Combine(kv.Key, kv.Value); // order-independent; per-process hash is enough (not stored)
            }
        }
        if (sig != c.KeepSeen)
        {
            c.KeepSeen = sig;
            c.KeepSeenAt = now;
        }
        else if (sig != c.KeepPushed && now - c.KeepSeenAt >= 500)
        {
            c.KeepPushed = sig;
            ApplyKeepMin(game, c.Ship, c.Assignment);
        }
    }

    /// <summary>Pushes the profile's keep-on-board amounts as vanilla resource desire (owner client only). Only types
    /// the policy lists are touched (absent = not managed) and only when they differ from the ship's current value.</summary>
    public static void ApplyKeepMin(GameRoot game, Ship ship, ShipAssignment a)
    {
        var keep = a.Cargo.KeepMin;
        if (keep == null || keep.Count == 0 || !World.Drives(game, ship))
        {
            return;
        }
        World.EnsureResources();
        foreach (var r in World.Resources)
        {
            if (keep.TryGetValue(r.ID.ToString(), out int units) && World.Keep(ship, r.ID) != units)
            {
                game.NetManager.EnqueueInput(new Cosmoteer.Game.Gui.Resources.ResourceLimitsWindow.MPSetResourceDesireInput(ship, r.ID, Math.Max(0, units)));
            }
        }
    }

    /// <summary>Profile collect template edit -> every commandable ship on the profile (automated or not): only the
    /// <paramref name="toggled"/> types, as vanilla bans, so the ships' other ban tweaks survive.</summary>
    public static void PushTemplate(GameRoot game, Profile p, ulong toggled)
    {
        if (toggled == 0 || game.Sim is not { } sim)
        {
            return;
        }
        ulong target = World.StackableMask & ~World.CollectMask(p.Cargo.Collect);
        foreach (ShipAssignment a in Store.Assignments.Values)
        {
            if (a.ProfileId == p.Id && World.FindShipById(sim, a.ShipId) is Ship s)
            {
                World.SetBans(game, s, toggled, target);
            }
        }
    }

    /// <summary>Explicit assignment to <paramref name="p"/>: one-time copy of its template into the ship's vanilla bans
    /// (only differing types are sent). Never on load/reconcile: existing bans stay until the player assigns.</summary>
    public static void CopyTemplate(GameRoot game, Ship ship, Profile p) =>
        World.SetBans(game, ship, World.StackableMask, World.StackableMask & ~World.CollectMask(p.Cargo.Collect));

    public static void Unassign(Ship ship)
    {
        if (AgentsByShip.TryGetValue(ship, out var c))
        {
            c.Assignment.Disable();
            if (_game != null)
            {
                c.Stop(_game);
                c.ReleaseDefence(_game);
            }
            AgentsByShip.Remove(ship);
            Agents.Remove(c);
            Store.Touch(c.Assignment);
        }
    }

    private static void OnSimChanged(GameRoot game, SimRoot sim)
    {
        UnhookOverlay();
        _sim = sim;
        Clear();
        Autofleet.Ledger.Clear(); // reservations belong to the old sim; live state of the new one is authoritative
        if (!_boundToSave)
        {
            _boundToSave = true;
            Store.BindToSave(game.LastSaveFile?.ToString());
        }
        World.EnsureResources();
        World.PrimeAsteroidMasks(sim);
        World.StartStasisIndex(sim);
        sim.Overlays.DrawingCustomOverlays += OnDrawOverlays;
        sim.Doodads.AddMinimapDoodad(Icons.Minimap);
        _drawFailed = false;

        // Restore automation for the ships this player owns that were enabled when saved (SharedShip adds teammates'
        // shared ships to LocalPlayerShips; those are not ours to drive).
        var own = sim.Ships.LocalPlayerShips;
        for (int i = 0; i < own.Count; i++)
        {
            Ship s = own[i];
            ShipAssignment? a = Store.Find(s.UniqueID.ToString());
            if (a != null && a.Enabled && World.Drives(game, s))
            {
                var c = new ShipAgent(s, s.UniqueID.ToString(), a);
                AgentsByShip[s] = c;
                Agents.Add(c);
            }
        }
        Log.Info($"Sim attached: {Agents.Count} automated ship(s) restored (state source: {Store.StateSource})");
    }

    private static void Tick(GameRoot game, SimRoot sim, long now)
    {
        // Game timeouts run on simulation time, so pausing the game cannot trigger false stalls/blacklists.
        long logicalMs = (long)((double)sim.LogicalTime * 1000.0); // implicit Time -> double = total seconds

        Removed.Clear();
        Claimed.Clear();
        for (int i = 0; i < Agents.Count; i++)
        {
            Ship? t = Agents[i].Target;
            if (t != null)
            {
                Claimed.Add(t);
            }
        }
        for (int i = 0; i < Agents.Count; i++)
        {
            ShipAgent c = Agents[i];
            try
            {
                if (!c.Ship.ExistsInSim(sim))
                {
                    Removed.Add(c);
                    continue;
                }
                // Only the owner's client drives a ship (lockstep MP: others just receive the inputs). Ownership moved
                // (transfer / SharedShip take): the assignment is dropped quietly, no Stop/Release inputs for a ship
                // that is no longer ours.
                if (!World.Drives(game, c.Ship))
                {
                    c.Assignment.Disable();
                    Store.Touch(c.Assignment);
                    Removed.Add(c);
                    continue;
                }
                c.ExcludeClaimed(Claimed);
                c.Tick(game, logicalMs);
                SyncKeep(game, c, now);

                if (c.Target != null)
                {
                    Claimed.Add(c.Target); // later agents in this same tick must not pick it too
                }
            }
            catch (Exception ex)
            {
                Log.Error("tick:" + c.Id, ex);
            }
        }
        for (int i = 0; i < Removed.Count; i++)
        {
            AgentsByShip.Remove(Removed[i].Ship);
            Agents.Remove(Removed[i]);
        }
        Store.FlushIfDue();
    }

    /// <summary>
    /// SimOverlayRenderer.DrawingCustomOverlays: raised in world space after vanilla overlays
    /// (SimOverlayRenderer.OnDrawOverlays). Uses the vanilla salvage circle renderer and the
    /// 0.30.5 CircleRendererCache.Draw signature.
    /// </summary>
    private static void OnDrawOverlays(object? sender, EventArgs e)
    {
        if (_drawFailed)
        {
            return;
        }
        SimRoot? sim = _sim;
        if (sim == null)
        {
            return;
        }
        try
        {
            CircleRenderer cr = sim.Overlays.Rules.TentativeSalvageCommandCircle;
            float scale = sim.Camera.UniformScale;
            if (Store.Prefs.ShowCircles)
            {
                for (int i = 0; i < Agents.Count; i++)
                {
                    ShipAgent a = Agents[i];
                    AgentConfig c = a.Cfg;
                    if (c.Temp == TempIntent.ReturnHome || !(c.Mine.Active || c.Loot.Active))
                    {
                        continue;
                    }
                    HarvestScope scope = c.Mine.Active ? c.MineScope : c.LootScope;
                    float maxSearch = c.Mine.Active ? c.MineMaxSearch : c.LootMaxSearch;
                    if (scope == HarvestScope.Anchor)
                    {
                        if (a.AnchorShip is Ship es)
                        {
                            cr.Draw(new Circle(es.WorldCenter, c.WorkRadius), scale, null);
                        }
                    }
                    else if (scope == HarvestScope.Zone)
                    {
                        cr.Draw(new Circle(c.ZoneX, c.ZoneY, c.ZoneR), scale, null);
                    }
                    else if (maxSearch < Registry.UnlimitedSearch)
                    {
                        // Nearest mode: search radius around the ship (none drawn when unlimited).
                        cr.Draw(new Circle(a.Ship.WorldCenter, maxSearch), scale, null);
                    }
                }
                if (PreviewArea is Circle preview)
                {
                    cr.Draw(preview, scale, null);
                }
            }
            if (Store.Prefs.ShowStateIcons && _game != null)
            {
                Icons.DrawAll(_game, sim, Agents);
            }
        }
        catch (Exception ex)
        {
            // Never retry a failing draw every frame: a per-frame exception storm kills the frame rate.
            _drawFailed = true;
            Log.Error("draw", ex);
        }
    }

    private static void UnhookOverlay()
    {
        if (_sim != null)
        {
            try
            {
                _sim.Overlays.DrawingCustomOverlays -= OnDrawOverlays;
                _sim.Doodads.RemoveMinimapDoodad(Icons.Minimap);
            }
            catch
            {
            }
        }
    }

    private static void Clear()
    {
        AgentsByShip.Clear();
        Agents.Clear();
        PreviewArea = null;
    }
}
