using System;
using System.Collections.Generic;
using Cosmoteer.GalaxyMap;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Special;
using Halfling.Geometry;
using Halfling.Gui;
using Halfling.Gui.Layout;
using Halfling.Gui.Quick;
using Halfling.Gui.Quick.WidgetModifiers;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>Флот / Назначения: fleet command for the live selection — profile assignment, anchor/target bindings, work
/// circle placement. No logic parameters: those live only in Профили (no per-ship overrides).</summary>
internal sealed partial class MinerWindow
{
    // ship picker choices (refreshed 1 Hz)
    private readonly List<string> _pickNames = new();
    private readonly List<string?> _pickIds = new();
    private readonly List<string> _pickNamesMixed = new();
    private long _nextPickRefresh;

    // profile picker
    private readonly List<string> _profNames = new();
    private readonly List<string> _profIds = new();
    private readonly List<string> _profNamesMixed = new();
    private int _profFor = -1;

    private static readonly string[] MissingKeys = { "MiningDrone/MissWait", "MiningDrone/MissRally", "MiningDrone/MissWork" };

    private void DrawAssignTab(QuickGui gui, Cosmoteer.Simulation.SimRoot sim, float editorW, float restH)
    {
        if (_ships.Count == 0)
        {
            gui.Label(L.T("MiningDrone/QuickStartTitle"), H28, LeftAlign, Bold);
            gui.Label(L.T("MiningDrone/AssignHint"), AutoH, LeftAlign, Wrap);
            return;
        }
        // Fleet command only (who flies which profile, with whom, where); every logic parameter lives in Профили.
        using (gui.ScrollBox(LayoutAlgorithms.StretchTopToBottom, ref _colScroll1, Hgt(restH), VScrollOnly, _probeAssignOne.Mod))
        {
            float colW = _probeAssignOne.Width(editorW - 48f);
            DrawQuickStart(gui);
            gui.MenuSeparator();
            DrawProfilePick(gui, colW);
            gui.MenuSeparator();
            DrawShipCollect(gui, colW);
            gui.MenuSeparator();
            DrawBindings(gui, sim, colW);
            gui.MenuSeparator();
            DrawFleetJump(gui);
        }
    }

    // ------------------------------------------------------------------ fleet jump

    private readonly List<string> _jumpProblems = new();
    private MapNode? _jumpFor;

    /// <summary>Orders the checked ships to the plotted course's gate (<see cref="FleetJump"/>); the jump itself is the
    /// vanilla Jump button once their drives have charged.</summary>
    private void DrawFleetJump(QuickGui gui)
    {
        gui.Label(L.T("MiningDrone/SecFleetJump"), H28, LeftAlign, Bold);
        MapNode? dest = FleetJump.Destination(_game, out MetaFtlGate? gate);
        if (!ReferenceEquals(dest, _jumpFor))
        {
            _jumpFor = dest;
            _jumpProblems.Clear();
        }
        if (dest == null)
        {
            gui.Label(L.T("MiningDrone/JumpNoCourse"), AutoH, LeftAlign, Wrap);
            return;
        }
        if (gui.Button(L.F("MiningDrone/JumpSend", _ships.Count, dest.Name), H40, gate == null ? Warn : Lit))
        {
            int n = FleetJump.Send(_game, _ships, _jumpProblems);
            if (n > 0)
            {
                Notice.Info(L.F("MiningDrone/JumpSent", n));
            }
        }
        for (int i = 0; i < _jumpProblems.Count; i++)
        {
            gui.Label(_jumpProblems[i], AutoH, LeftAlign, Wrap, Warn);
        }
        gui.Label(L.T("MiningDrone/JumpHint"), AutoH, LeftAlign, Wrap);
    }

    // ------------------------------------------------------------------ quick start

    /// <summary>The four setup steps for the selection; done steps are green, a required missing one is orange.</summary>
    private void DrawQuickStart(QuickGui gui)
    {
        gui.Label(L.T("MiningDrone/QuickStartTitle"), H28, LeftAlign, Bold);
        string ships = _ships.Count == 1 ? _ships[0].Metadata.PlainShipName : L.F("MiningDrone/SelectedCount", _ships.Count);
        gui.Label(L.F("MiningDrone/QS1", ships), AutoH, LeftAlign, Wrap, Chosen);
        gui.Label(L.F("MiningDrone/QS2", Same(static a => a.ProfileId) ? ProfileName(_asg[0]) : L.T("MiningDrone/DifferentValues")), AutoH, LeftAlign, Wrap, Chosen);

        RefreshPicks();
        int bound = 0;
        bool needAnchor = false;
        for (int i = 0; i < _asg.Count; i++)
        {
            ShipAssignment a = _asg[i];
            bound += a.Bindings.Anchor.TargetShipId != null ? 1 : 0;
            needAnchor |= a.Profile.Defaults.UseAnchor || Resolver.Param(a, Registry.Mine, Registry.PScope) == (double)HarvestScope.Anchor;
        }
        string anchor;
        if (bound == 0)
        {
            anchor = L.T(needAnchor ? "MiningDrone/QSAnchorNeeded" : "MiningDrone/QSAnchorOptional");
        }
        else if (Same(static a => a.Bindings.Anchor.TargetShipId))
        {
            int k = _pickIds.IndexOf(_asg[0].Bindings.Anchor.TargetShipId);
            anchor = k >= 0 ? _pickNames[k] : L.T("MiningDrone/BoundMissing");
        }
        else
        {
            anchor = L.F("MiningDrone/QSAnchorSome", bound, _asg.Count);
        }
        gui.Label(L.F("MiningDrone/QS3", anchor), AutoH, LeftAlign, Wrap, bound == _asg.Count ? Chosen : needAnchor ? Warn : Lit);

        int running = 0;
        for (int i = 0; i < _ships.Count; i++)
        {
            running += MinerManager.Get(_ships[i]) != null ? 1 : 0;
        }
        string start = running == _ships.Count ? L.T("MiningDrone/QSRunning")
            : running == 0 ? L.T("MiningDrone/QSPressStart")
            : L.F("MiningDrone/QSSomeRunning", running, _ships.Count);
        gui.Label(L.F("MiningDrone/QS4", start), AutoH, LeftAlign, Wrap, running == _ships.Count ? Chosen : Lit);
        if (_ships.Count > 1)
        {
            gui.Label(L.F("MiningDrone/QSMulti", _ships.Count), AutoH, LeftAlign, Wrap);
        }
    }

    // ------------------------------------------------------------------ profile

    private void RefreshProfiles()
    {
        if (_profFor == Store.LibraryVersion)
        {
            return;
        }
        _profFor = Store.LibraryVersion;
        _profNames.Clear();
        _profIds.Clear();
        foreach (Profile p in Store.Profiles)
        {
            _profNames.Add(Registry.DisplayName(p.Name, p.Id));
            _profIds.Add(p.Id);
        }
    }

    private void DrawProfilePick(QuickGui gui, float colW)
    {
        gui.Label(L.T("MiningDrone/SecProfile"), H28, LeftAlign, Bold);
        RefreshProfiles();
        bool same = Same(static a => a.ProfileId);
        List<string> items = _profNames;
        int sel;
        if (same)
        {
            sel = _profIds.IndexOf(_asg[0].ProfileId);
            if (sel < 0)
            {
                // Unknown profile id (e.g. an old save sidecar): shown as the first (inert) entry; the ship idles.
                _profNamesMixed.Clear();
                _profNamesMixed.Add(ProfileName(_asg[0]) + "  " + L.T("MiningDrone/ProfileDeleted"));
                _profNamesMixed.AddRange(_profNames);
                items = _profNamesMixed;
                sel = 0;
            }
        }
        else
        {
            _profNamesMixed.Clear();
            _profNamesMixed.Add(L.T("MiningDrone/DifferentValues"));
            _profNamesMixed.AddRange(_profNames);
            items = _profNamesMixed;
            sel = 0;
        }
        int before = sel;
        gui.DropList(items, ref sel, H56);
        if (sel != before)
        {
            int idx = ReferenceEquals(items, _profNames) ? sel : sel - 1;
            if (idx >= 0 && idx < _profIds.Count && Store.FindProfile(_profIds[idx]) is Profile p)
            {
                for (int i = 0; i < _asg.Count; i++)
                {
                    Store.SetProfile(_asg[i], p); // keep-on-board follows via MinerManager.SyncKeep
                    MinerManager.CopyTemplate(_game, _ships[i], p); // collect template -> vanilla bans, once
                }
                Store.Prefs.LastProfileId = p.Id;
                Store.MarkPrefs();
                _profFor = -1;
            }
        }
        gui.Label(L.T("MiningDrone/ProfileEditWhere"), AutoH, LeftAlign, Wrap);
    }

    // ------------------------------------------------------------------ bindings

    private void DrawBindings(QuickGui gui, Cosmoteer.Simulation.SimRoot sim, float colW)
    {
        gui.Label(L.T("MiningDrone/SecBindings"), H28, LeftAlign, Bold);
        RefreshPicks();
        DrawPicker(gui, "MiningDrone/EscortTarget", static a => a.Bindings.Anchor.TargetShipId, SetAnchor);
        gui.Label(L.T("MiningDrone/AnchorPickHint"), AutoH, LeftAlign, Wrap);
        DrawPicker(gui, "MiningDrone/BaseShip", static a => a.Bindings.OffloadTarget, static (a, v) => a.Bindings.OffloadTarget = v);
        // Supply / protect targets feed modules of later phases: shown only when a selected ship already has one,
        // so they can still be cleared, and kept out of the main flow otherwise.
        bool anySupply = false, anyProtect = false;
        for (int i = 0; i < _asg.Count; i++)
        {
            anySupply |= _asg[i].Bindings.SupplyTarget != null;
            anyProtect |= _asg[i].Bindings.ProtectedTarget != null;
        }
        if (anySupply)
        {
            DrawPicker(gui, "MiningDrone/SupplyTarget", static a => a.Bindings.SupplyTarget, static (a, v) => a.Bindings.SupplyTarget = v);
        }
        if (anyProtect)
        {
            DrawPicker(gui, "MiningDrone/ProtectedTarget", static a => a.Bindings.ProtectedTarget, static (a, v) => a.Bindings.ProtectedTarget = v);
        }
        gui.Label(L.T("MiningDrone/BindingsHint"), AutoH, LeftAlign, Wrap);

        // The work circle is a place, not logic: shown when a selected ship's profile works "in a circle".
        bool zone = false;
        for (int i = 0; i < _asg.Count; i++)
        {
            zone |= Store.WorksInZone(_asg[i]);
        }
        if (zone)
        {
            gui.MenuSeparator();
            gui.Label(L.T("MiningDrone/SecZone"), H28, LeftAlign, Bold);
            DrawZone(gui, sim, colW);
        }
    }

    /// <summary>Anchor setter with cycle rejection: a ship that would end up (indirectly) following itself keeps its old anchor.</summary>
    private static void SetAnchor(ShipAssignment a, string? id)
    {
        if (id != null && Anchors.WouldCycle(a.ShipId, id))
        {
            Log.Info($"anchor {a.ShipId} -> {id} rejected: cyclic follow");
            Notice.Info(L.T("MiningDrone/AnchorCycleRejected"));
            return;
        }
        a.Bindings.Anchor.TargetShipId = id;
    }

    // ------------------------------------------------------------------ work circle (per-ship place)

    private void DrawZone(QuickGui gui, Cosmoteer.Simulation.SimRoot sim, float colW)
    {
        DrawSlider(gui, "MiningDrone/Radius", static a => a.Bindings.Zone.R, static (a, v) =>
        {
            if (Store.WorksInZone(a))
            {
                a.Bindings.Zone.R = v; // other ships keep their saved circle for when their profile works in one again
            }
        }, RadiusMin, RadiusMax, null, static v => v.ToString("0"));
        // Interactive placement on the map (vanilla-style selection state), with on-screen radius buttons.
        var b3p = W((colW - 2f * Gap) / 3f);
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            if (gui.Button(Ui.Picking ? L.F("MiningDrone/PickingRadius", AreaPickState.Radius.ToString("0")) : L.T("MiningDrone/PickArea"), b3p, Ui.Picking ? Chosen : Lit, Shrink))
            {
                AreaPickState.Radius = Math.Clamp(_asg[0].Bindings.Zone.R, AreaPickState.MinRadius, AreaPickState.MaxRadius);
                Ui.StartAreaPick();
            }
            if (gui.Button("−", b3p, Shrink))
            {
                AreaPickState.Grow(1f / 1.15f);
            }
            if (gui.Button("+", b3p, Shrink))
            {
                AreaPickState.Grow(1.15f);
            }
        }
        var b2 = W((colW - Gap) / 2f);
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            if (gui.Button(L.T("MiningDrone/CenterShip"), b2, Shrink))
            {
                // Each ship centers on itself.
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (!Store.WorksInZone(_asg[i]))
                    {
                        continue;
                    }
                    Vector2 p = _ships[i].WorldCenter;
                    _asg[i].Bindings.Zone.X = p.X;
                    _asg[i].Bindings.Zone.Y = p.Y;
                    Store.Touch(_asg[i]);
                }
            }
            if (gui.Button(L.T("MiningDrone/CenterView"), b2, Shrink))
            {
                Vector2 p = sim.Camera.WorldLocation;
                ForAll(a =>
                {
                    if (Store.WorksInZone(a))
                    {
                        a.Bindings.Zone.X = p.X;
                        a.Bindings.Zone.Y = p.Y;
                    }
                });
            }
        }
        bool centerSame = Same(static a => a.Bindings.Zone.X) && Same(static a => a.Bindings.Zone.Y);
        gui.Label(centerSame ? L.F("MiningDrone/CenterLine", _asg[0].Bindings.Zone.X.ToString("0"), _asg[0].Bindings.Zone.Y.ToString("0")) : L.F("MiningDrone/CenterLine", "—", "—"), H24, LeftAlign);
    }

    /// <summary>Slider; mixed values show "—" and the slider is only written on a real user move.</summary>
    private void DrawSlider(QuickGui gui, string labelKey, Func<ShipAssignment, float> get, Action<ShipAssignment, float> set,
        IQuickWidgetModifier<ScrollBar> min, IQuickWidgetModifier<ScrollBar> max, IQuickWidgetModifier<ScrollBar>? step, Func<float, string> fmt)
    {
        bool same = Same(get);
        float v = get(_asg[0]);
        gui.Label(L.F(labelKey, same ? fmt(v) : "—"), H24, LeftAlign);
        float nv = v;
        if (step != null)
        {
            gui.HorizontalSlider(ref nv, H40, min, max, step);
        }
        else
        {
            gui.HorizontalSlider(ref nv, H40, min, max);
        }
        if (nv != v)
        {
            ForAll(a => set(a, nv));
        }
    }

    // ------------------------------------------------------------------ ship pickers (own + allied)

    private void RefreshPicks()
    {
        long now = Environment.TickCount64;
        if (now < _nextPickRefresh)
        {
            return;
        }
        _nextPickRefresh = now + 1000;
        _pickNames.Clear();
        _pickIds.Clear();
        _pickNames.Add(L.T("MiningDrone/None"));
        _pickIds.Add(null);
        var sim = _game.Sim;
        int local = _game.LocalPlayerIndex;
        var ships = sim.Ships;
        for (int i = 0; i < ships.Count; i++)
        {
            Ship s = ships[i];
            if (s.IsJunk || !s.IsSignificant)
            {
                continue;
            }
            bool own = s.IsLocalPlayerShip;
            // Vanilla accepts an allied sink: MPMoveResourcesInput.cs:122 (CanExecuteCrewCommandsFrom || IsAlliesWith).
            if (!own && !sim.Mode.AreAllies(local, s.Metadata.PlayerIndex))
            {
                continue;
            }
            string name = s.Metadata.PlainShipName;
            if (name.Length > 40)
            {
                name = string.Concat(name.AsSpan(0, 39), "…");
            }
            _pickNames.Add(own ? name : name + "  [" + L.T("MiningDrone/Ally") + "]");
            _pickIds.Add(s.UniqueID.ToString());
        }
    }

    private void DrawPicker(QuickGui gui, string labelKey, Func<ShipAssignment, string?> get, Action<ShipAssignment, string?> set)
    {
        gui.Label(L.T(labelKey), H24, LeftAlign);
        bool same = Same(get);
        List<string> items = _pickNames;
        int sel;
        if (same)
        {
            string? cur = get(_asg[0]);
            sel = _pickIds.IndexOf(cur);
            if (sel < 0)
            {
                // Bound ship not in the sim (lost / in stasis): the binding is kept and shown, never cleared.
                _pickNamesMixed.Clear();
                _pickNamesMixed.Add(L.T("MiningDrone/BoundMissing"));
                _pickNamesMixed.AddRange(_pickNames);
                items = _pickNamesMixed;
                sel = 0;
            }
        }
        else
        {
            // Mixed: a leading "Different ships" placeholder; picking it changes nothing.
            _pickNamesMixed.Clear();
            _pickNamesMixed.Add(L.T("MiningDrone/DifferentShips"));
            _pickNamesMixed.AddRange(_pickNames);
            items = _pickNamesMixed;
            sel = 0;
        }
        int before = sel;
        gui.DropList(items, ref sel, H56);
        if (sel == before)
        {
            return;
        }
        int idx = ReferenceEquals(items, _pickNames) ? sel : sel - 1;
        if (idx >= 0 && idx < _pickIds.Count)
        {
            string? id = _pickIds[idx];
            // A ship can't follow / offload into itself: skip that ship, keep the others.
            for (int i = 0; i < _ships.Count; i++)
            {
                if (id == null || _ships[i].UniqueID.ToString() != id)
                {
                    set(_asg[i], id);
                    Store.Touch(_asg[i]);
                }
            }
        }
    }
}
