using System;
using System.Collections.Generic;
using Halfling.Graphics.Text;
using Halfling.Gui;
using Halfling.Gui.Components.Graphics;
using Halfling.Gui.Layout;
using Halfling.Gui.Quick;
using Halfling.Gui.Quick.WidgetModifiers;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>Профили: profile library and the module composer.</summary>
internal sealed partial class MinerWindow
{
    private string? _selProfile;
    private string? _confirmDelete;
    private Vector2Box _profScroll = new();
    private string _nameEdit = "";
    private string? _nameFor;
    private int _addIdx;
    private readonly List<string> _addNames = new();
    private readonly List<string> _addIds = new();
    private int _addFor = -1;
    private string? _addForProfile;

    // validation cache (recomputed when the library changes)
    private int _validFor = -1;
    private string? _validProfile;
    private readonly List<string> _validMsgs = new();
    private string _preview = "";

    private static readonly string[] ActivationKeys = { "MiningDrone/ActAlways", "MiningDrone/ActAnchored", "MiningDrone/ActNotAnchored" };

    /// <summary>Boxed scroll offset (ref-able field for the nested scroll box).</summary>
    private sealed class Vector2Box
    {
        public Halfling.Geometry.Vector2 V;
    }

    private void DrawProfilesTab(QuickGui gui, float editorW, float restH)
    {
        Profile? p = Store.FindProfile(_selProfile);
        if (p == null && Store.Profiles.Count > 0)
        {
            p = Store.Profiles[0];
            _selProfile = p.Id;
        }
        float listW = 220f;
        float edW = editorW - listW - Gap;
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, Hgt(restH), Fixed))
        {
            using (gui.ScrollBox(LayoutAlgorithms.StretchTopToBottom, ref _profScroll.V, W(listW), VScrollOnly, _probeProfList.Mod))
            {
                DrawProfileList(gui, p);
            }
            using (gui.ScrollBox(LayoutAlgorithms.StretchTopToBottom, ref _colScroll2, W(edW), VScrollOnly, _probeProfile.Mod))
            {
                if (p == null)
                {
                    gui.Label(L.T("MiningDrone/NoProfiles"), AutoH, LeftAlign, Wrap);
                }
                else
                {
                    DrawComposer(gui, p, _probeProfile.Width(edW - 48f));
                }
            }
        }
    }

    private void DrawProfileList(QuickGui gui, Profile? sel)
    {
        gui.Label(L.T("MiningDrone/ProfileLibrary"), H28, LeftAlign, Bold);
        _sortProfiles.Begin();
        foreach (Profile p in Store.Profiles)
        {
            string name = Registry.DisplayName(p.Name, p.Id);
            string text = name + "\n" + L.F("MiningDrone/ShipsCount", InTree(p));
            // Drag a profile to reorder the library (= fleet-tree folder order); a plain click selects it.
            if (gui.Button(text, H56, ReferenceEquals(p, sel) ? Chosen : Lit, Shrink, _sortProfiles.Block(p.Id, name), _sortProfiles.Grab(p.Id)))
            {
                _selProfile = p.Id;
                _confirmDelete = null;
            }
        }
        if (_sortProfiles.End(gui, _probeProfList.Viewport, ref _profScroll.V) is { } drop && Store.FindProfile(drop.Key) is { } dp)
        {
            Store.MoveProfile(dp, Store.FindProfile(drop.Before), Store.FindProfile(drop.After));
        }
        gui.MenuSeparator();
        int at = sel == null ? -1 : Store.Profiles.IndexOf(sel);
        if (at >= 0)
        {
            // Library order = fleet-tree folder order (saved as list order).
            using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
            {
                if (gui.Button("↑ " + L.T("MiningDrone/MoveUp"), W(96f), Shrink, at == 0 ? Disabled : null) && at > 0)
                {
                    Store.SwapProfiles(sel!, Store.Profiles[at - 1]);
                }
                if (gui.Button("↓ " + L.T("MiningDrone/MoveDown"), W(96f), Shrink, at == Store.Profiles.Count - 1 ? Disabled : null) && at < Store.Profiles.Count - 1)
                {
                    Store.SwapProfiles(sel!, Store.Profiles[at + 1]);
                }
            }
        }
        if (gui.Button(L.T("MiningDrone/ProfileNew"), H40, Shrink))
        {
            var np = new Profile { Id = Registry.NewId(), Name = L.T("MiningDrone/ProfileNewName") };
            np.Modules.Add(Registry.NewInstance(Registry.Avoidance, Registry.NewId()));
            np.Modules.Add(Registry.NewInstance(Registry.AnchorLink, Registry.NewId()));
            Store.Profiles.Add(np);
            Store.MarkLibrary();
            _selProfile = np.Id;
        }
        if (sel != null && gui.Button(L.T("MiningDrone/ProfileDuplicate"), H40, Shrink))
        {
            Profile d = Duplicate(sel);
            Store.Profiles.Add(d);
            Store.MarkLibrary();
            _selProfile = d.Id;
        }
        if (sel != null)
        {
            // Ships run their profile live: one used by a running ship can't be deleted (reassign it first), so
            // deleting never silently stops a working ship.
            int used = 0;
            foreach (ShipAgent ag in MinerManager.All)
            {
                used += ag.Assignment.ProfileId == sel.Id ? 1 : 0;
            }
            bool confirm = _confirmDelete == sel.Id;
            if (gui.Button(confirm ? L.T("MiningDrone/ProfileDeleteConfirm") : L.T("MiningDrone/ProfileDelete"), H40, Shrink, used > 0 ? Disabled : confirm ? Warn : null))
            {
                if (used == 0 && confirm)
                {
                    Store.DeleteProfile(sel);
                    _selProfile = null;
                    _confirmDelete = null;
                }
                else if (used == 0)
                {
                    _confirmDelete = sel.Id;
                }
            }
            if (used > 0)
            {
                gui.Label(L.F("MiningDrone/ProfileInUse", used), AutoH, LeftAlign, Wrap);
            }
        }
    }

    /// <summary>Running ships of <paramref name="p"/> counted like the fleet tree (Window.RefreshFleet filter), so the
    /// two counters agree; saved assignments of ships not in this game are not counted.</summary>
    private static int InTree(Profile p)
    {
        int n = 0;
        foreach (ShipAgent ag in MinerManager.All)
        {
            var s = ag.Ship;
            if (ag.Assignment.ProfileId == p.Id && s.IsLocalPlayerShip && !s.Rules.IsAsteroid && !s.IsJunk && s.IsSignificant)
            {
                n++;
            }
        }
        return n;
    }

    private static Profile Duplicate(Profile s)
    {
        var d = new Profile
        {
            Id = Registry.NewId(),
            Name = L.F("MiningDrone/CopyOf", Registry.DisplayName(s.Name, s.Id)),
            Arbitration = s.Arbitration.Clone(),
            Cargo = s.Cargo.Clone(),
            Defaults = s.Defaults.Clone(),
            BuildOrders = s.BuildOrders == null ? null : s.BuildOrders.ConvertAll(b => b.Clone()),
            Extra = s.Extra == null ? null : new Dictionary<string, System.Text.Json.JsonElement>(s.Extra),
        };
        foreach (ModuleInstance m in s.Modules)
        {
            ModuleInstance c = m.Clone();
            c.Id = Registry.NewId();
            d.Modules.Add(c);
        }
        return d;
    }

    /// <summary>Every profile edit: ships read the profile live, so this alone makes them pick it up next tick.</summary>
    private static void Edited(Profile p) => Store.MarkLibrary();

    private void DrawComposer(QuickGui gui, Profile p, float colW)
    {
        // Name (rename in place).
        if (_nameFor != p.Id)
        {
            _nameFor = p.Id;
            _nameEdit = Registry.DisplayName(p.Name, p.Id);
        }
        gui.Label(L.T("MiningDrone/ProfilesHint"), AutoH, LeftAlign, Wrap);
        gui.Label(L.T("MiningDrone/ProfileName"), H24, LeftAlign);
        string before = _nameEdit;
        gui.TextEditField(TextEditField.Style.SingleLine, ref _nameEdit, H40);
        if (_nameEdit != before && _nameEdit.Trim().Length > 0)
        {
            p.Name = _nameEdit.Trim();
            Store.MarkLibrary();
        }
        gui.Label(L.F("MiningDrone/ProfileLive", InTree(p)), AutoH, LeftAlign, Wrap);

        // Validation + resolved preview.
        Validate(p);
        gui.Label(_preview, AutoH, LeftAlign, Wrap);
        for (int i = 0; i < _validMsgs.Count; i++)
        {
            gui.Label("⚠ " + _validMsgs[i], AutoH, LeftAlign, Wrap, Warn);
        }

        gui.MenuSeparator();
        gui.Label(L.T("MiningDrone/SecModules"), H28, LeftAlign, Bold);
        DrawModules(gui, p, colW);
        DrawAddModule(gui, p, colW);

        gui.MenuSeparator();
        gui.Label(L.T("MiningDrone/SecArbitration"), H28, LeftAlign, Bold);
        bool ram = p.Arbitration.ResumeAfterManual;
        gui.ToggleButton(ToggleButton.Style.Check, L.T("MiningDrone/ResumeAfterManual"), ref ram, H40);
        if (ram != p.Arbitration.ResumeAfterManual)
        {
            p.Arbitration.ResumeAfterManual = ram;
            Edited(p);
        }
        float commit = p.Arbitration.MinCommitMs / 1000f;
        gui.Label(L.F("MiningDrone/MinCommit", commit.ToString("0.0")), H24, LeftAlign);
        float nc = commit;
        Slider(gui, ref nc, 0, 10, 0.5);
        if (nc != commit)
        {
            p.Arbitration.MinCommitMs = (int)Math.Round(nc * 1000f);
            Edited(p);
        }

        gui.MenuSeparator();
        gui.Label(L.T("MiningDrone/SecDefaults"), H28, LeftAlign, Bold);
        bool ua = p.Defaults.UseAnchor;
        gui.ToggleButton(ToggleButton.Style.Check, L.T("MiningDrone/DefUseAnchor"), ref ua, H40);
        if (ua != p.Defaults.UseAnchor)
        {
            p.Defaults.UseAnchor = ua;
            Edited(p);
        }
        bool ota = p.Defaults.OffloadToAnchor;
        gui.ToggleButton(ToggleButton.Style.Check, L.T("MiningDrone/OffloadToAnchor"), ref ota, H40);
        if (ota != p.Defaults.OffloadToAnchor)
        {
            p.Defaults.OffloadToAnchor = ota;
            Edited(p);
        }
        gui.Label(L.T("MiningDrone/MissingPolicy"), H24, LeftAlign);
        var b3 = W((colW - 2f * Gap) / 3f);
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            for (int k = 0; k < 3; k++)
            {
                var v = (MissingAnchorPolicy)k;
                if (gui.Button(L.T(MissingKeys[k]), b3, p.Defaults.Anchor.MissingPolicy == v ? Chosen : Lit, Shrink))
                {
                    p.Defaults.Anchor.MissingPolicy = v;
                    Edited(p);
                }
            }
        }
        float fd = p.Defaults.Anchor.FollowDistance;
        gui.Label(L.F("MiningDrone/FollowDistance", fd < 0 ? L.T("MiningDrone/VanillaDefault") : fd.ToString("0")), H24, LeftAlign);
        float nfd = fd;
        Slider(gui, ref nfd, -1, 3000, 1);
        if (nfd != fd)
        {
            p.Defaults.Anchor.FollowDistance = nfd < 0 ? -1f : (float)Math.Round(nfd);
            Edited(p);
        }
        gui.Label(L.T("MiningDrone/DefaultsHint"), AutoH, LeftAlign, Wrap);

        gui.MenuSeparator();
        DrawResources(gui, colW, ProfileCargo(p));

        gui.MenuSeparator();
        gui.Label(L.T("MiningDrone/SecBuildOrders"), H28, LeftAlign, Bold);
        gui.Label(L.T("MiningDrone/InDevelopment"), H24, LeftAlign);
    }

    private void Validate(Profile p)
    {
        if (_validFor == Store.LibraryVersion && _validProfile == p.Id)
        {
            return;
        }
        _validFor = Store.LibraryVersion;
        _validProfile = p.Id;
        _validMsgs.Clear();
        // Compile a throwaway assignment (no ship, no anchor) with the same resolver the ships use.
        var probe = new ShipAssignment { ShipId = "", ProfileId = p.Id };
        if (p.Defaults.UseAnchor)
        {
            probe.Bindings.Anchor.TargetShipId = "probe";
        }
        var cfg = new AgentConfig();
        Resolver.Refresh(cfg, probe);
        foreach (string b in cfg.Blockers)
        {
            if (!_validMsgs.Contains(b))
            {
                _validMsgs.Add(b);
            }
        }
        bool harvest = cfg.Mine.Active || cfg.Loot.Active;
        if (harvest && !cfg.Offload.Present)
        {
            _validMsgs.Add(L.T("MiningDrone/WarnNoOffload"));
        }

        // Resolved behaviour, in arbitration order.
        var parts = new List<(int Rank, int Prio, int Idx, string Text)>();
        for (int i = 0; i < p.Modules.Count; i++)
        {
            ModuleInstance m = p.Modules[i];
            ModuleDef? d = Registry.Get(m.Type);
            if (d == null || !ActiveIn(cfg, d.Type, m.Id))
            {
                continue;
            }
            int rank = d.Emergency ? 0 : (d.Type == ModuleType.AnchorLink ? 2 : 1);
            string tag = d.Emergency ? L.T("MiningDrone/TagEmergency") : d.Type == ModuleType.AnchorLink ? L.T("MiningDrone/TagIdle") : m.Priority.ToString();
            parts.Add((rank, m.Priority, i, L.T(d.NameKey) + " (" + tag + ")"));
        }
        parts.Sort(static (a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Prio != b.Prio ? b.Prio.CompareTo(a.Prio) : a.Idx.CompareTo(b.Idx));
        string s = L.T("MiningDrone/PreviewManual");
        for (int i = 0; i < parts.Count; i++)
        {
            bool tie = i > 0 && parts[i].Rank == 1 && parts[i - 1].Rank == 1 && parts[i].Prio == parts[i - 1].Prio;
            s += (tie ? " = " : " > ") + parts[i].Text;
        }
        _preview = L.F("MiningDrone/PreviewLine", s);
    }

    private static bool ActiveIn(AgentConfig c, ModuleType t, string id)
    {
        Slot s = t switch
        {
            ModuleType.Mine => c.Mine,
            ModuleType.Loot => c.Loot,
            ModuleType.Offload => c.Offload,
            ModuleType.AnchorLink => c.Anchor,
            ModuleType.Avoidance => c.Avoid,
            ModuleType.Retreat => c.Retreat,
            ModuleType.Guard => c.Guard,
            ModuleType.SelfDefence => c.Defend,
            ModuleType.Hunt => c.Hunt,
            ModuleType.Trade => c.Trade,
            _ => default,
        };
        return s.Active && s.InstanceId == id;
    }

    private readonly List<ModuleInstance> _ordered = new();

    private void DrawModules(QuickGui gui, Profile p, float colW)
    {
        _ordered.Clear();
        _ordered.AddRange(p.Modules);
        _ordered.Sort((a, b) =>
        {
            int c = b.Priority.CompareTo(a.Priority);
            return c != 0 ? c : p.Modules.IndexOf(a).CompareTo(p.Modules.IndexOf(b));
        });
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            var half = W((colW - Gap) / 2f);
            bool expand = gui.Button(L.T("MiningDrone/ExpandAll"), half, Shrink);
            if (gui.Button(L.T("MiningDrone/CollapseAll"), half, Shrink) || expand)
            {
                Store.Prefs.Open ??= new Dictionary<string, bool>();
                foreach (ModuleInstance m in p.Modules)
                {
                    Store.Prefs.Open[p.Id + "/" + m.Id] = expand;
                }
                Store.MarkPrefs();
            }
        }
        // Each module is one draggable block: header (grab = the name) + summary or params.
        float oneLineName = colW - (Lead + 4f * BtnW + ActW + 6f * Gap);
        bool twoLines = oneLineName < 140f;
        ModuleInstance? remove = null;
        _sortModules.Begin();
        foreach (ModuleInstance m in _ordered)
        {
            ModuleDef? d = Registry.Get(m.Type);
            string name = d == null ? m.Type : L.T(d.NameKey) + (d.Implemented ? "" : "  " + L.T("MiningDrone/NotImplementedShort"));
            using (gui.LayoutRegion(LayoutAlgorithms.StretchTopToBottom, W(colW), Section, _sortModules.Block(m.Id, name)))
            {
                if (DrawModule(gui, p, m, d, name, colW, oneLineName, twoLines))
                {
                    remove = m;
                }
            }
        }
        if (_sortModules.End(gui, _probeProfile.Viewport, ref _colScroll2) is { } drop)
        {
            ModuleInstance? Find(string? id) => id == null ? null : p.Modules.Find(x => x.Id == id);
            if (Find(drop.Key) is { } dm && Registry.MoveModule(p, dm, Find(drop.Before), Find(drop.After)))
            {
                Edited(p);
            }
        }
        if (remove != null)
        {
            p.Modules.Remove(remove);
            Edited(p);
        }
    }

    // Row: fold | check | name (fills) | − prio + | activation | ×  -> 8 controls, 7 gaps.
    // Too narrow for a readable name: name and × on line 1, the priority/activation controls on line 2.
    private const float RowCheckW = 48f, BtnW = 40f, ActW = 110f;
    private const float Lead = FoldW + Gap + RowCheckW; // fold + check, before the name

    /// <summary>One module block's content; returns whether its × was clicked.</summary>
    private bool DrawModule(QuickGui gui, Profile p, ModuleInstance m, ModuleDef? d, string name, float colW, float oneLineName, bool twoLines)
    {
        bool remove = false;
        bool hasBody = d != null && (d.Type == ModuleType.AnchorLink || (d.Implemented && d.Params.Length > 0));
        bool open = hasBody && IsOpen(p.Id + "/" + m.Id, m.Enabled); // disabled modules start folded
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            if (!hasBody)
            {
                gui.Label("", W(FoldW));
            }
            else if (gui.Button(open ? "−" : "+", W(FoldW), Shrink))
            {
                open = !open;
                SetOpen(p.Id + "/" + m.Id, open);
            }
            bool on = m.Enabled;
            gui.ToggleButton(ToggleButton.Style.Check, "", ref on, W(RowCheckW));
            if (on != m.Enabled)
            {
                if (hasBody)
                {
                    SetOpen(p.Id + "/" + m.Id, open); // pin the fold: its default follows Enabled
                }
                m.Enabled = on;
                Edited(p);
            }
            float nameW = twoLines ? colW - (Lead + BtnW + 2f * Gap) : oneLineName;
            gui.Label(name, W(nameW), LeftAlign, Ellipsis, d?.Implemented == false ? Dim : null, _sortModules.Grab(m.Id));
            if (!twoLines)
            {
                DrawModuleControls(gui, p, m, ActW);
            }
            if (gui.Button("✕", W(BtnW), Shrink, _tipRemove))
            {
                remove = true;
            }
        }
        if (twoLines)
        {
            using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
            {
                gui.Label("", W(Lead));
                DrawModuleControls(gui, p, m, Math.Max(ActW, colW - (Lead + 3f * BtnW + 4f * Gap)));
            }
        }
        if (!open)
        {
            if (hasBody && d!.Implemented && d.Params.Length > 0 && gui.Button(Summary(m, d), H28, SummaryLook))
            {
                SetOpen(p.Id + "/" + m.Id, true); // the summary row expands too
            }
            return remove;
        }
        if (d!.Type == ModuleType.AnchorLink)
        {
            // The anchor ship itself is a per-ship binding (spec: no privileged flagship), not a profile value.
            gui.Label("    " + L.T("MiningDrone/AnchorWhere"), AutoH, LeftAlign, Wrap);
        }
        if (d.Implemented)
        {
            foreach (ParamDef pd in d.Params)
            {
                DrawParamEditor(gui, p, m, pd, colW);
            }
        }
        return remove;
    }
    private static bool IsOpen(string key, bool def) => Store.Prefs.Open != null && Store.Prefs.Open.TryGetValue(key, out bool v) ? v : def;

    private static void SetOpen(string key, bool open)
    {
        (Store.Prefs.Open ??= new Dictionary<string, bool>())[key] = open;
        Store.MarkPrefs();
    }

    /// <summary>Bold section title with a fold toggle (look of the fleet tree folders); returns whether it is open.</summary>
    private static bool FoldHeader(QuickGui gui, string titleKey, float colW)
    {
        string key = "cargo/" + titleKey;
        bool open = IsOpen(key, true);
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H28, Fixed))
        {
            if (gui.Button(open ? "−" : "+", W(FoldW), Shrink))
            {
                open = !open;
                SetOpen(key, open);
            }
            gui.Label(L.T(titleKey), W(colW - FoldW - Gap), LeftAlign, Bold, ShrinkLabel);
        }
        return open;
    }

    private static readonly IQuickWidgetModifier<Button> SummaryLook = new Mod<Button>(static b =>
    {
        foreach (WidgetTextRenderer t in new[] { b.StateNormalTextRenderer, b.StateHighlightedTextRenderer, b.StatePressedTextRenderer, b.StateDisabledTextRenderer })
        {
            t.OversizeMode = OversizeMode.Ellipses;
            t.HAlignment = HAlignment.Left;
        }
    });

    private int _sumFor = -1;
    private readonly Dictionary<string, string> _sums = new();

    /// <summary>Folded module summary line, cached until the library changes.</summary>
    private string Summary(ModuleInstance m, ModuleDef d)
    {
        if (_sumFor != Store.LibraryVersion)
        {
            _sumFor = Store.LibraryVersion;
            _sums.Clear();
        }
        if (_sums.TryGetValue(m.Id, out string? s))
        {
            return s;
        }
        s = "    " + Registry.Summary(m, d, L.T);
        _sums[m.Id] = s;
        return s;
    }

    /// <summary>− priority + and the activation cycle button of one module row.</summary>
    private void DrawModuleControls(QuickGui gui, Profile p, ModuleInstance m, float actW)
    {
        if (gui.Button("−", W(40f), Shrink, _tipPrioDown))
        {
            m.Priority = Math.Max(0, m.Priority - 1);
            Edited(p);
        }
        gui.Label(m.Priority.ToString(), W(40f));
        if (gui.Button("+", W(40f), Shrink, _tipPrioUp))
        {
            m.Priority = Math.Min(200, m.Priority + 1);
            Edited(p);
        }
        if (gui.Button(L.T(ActivationKeys[(int)m.Activation]), W(actW), Shrink, _tipActivation))
        {
            m.Activation = (Activation)(((int)m.Activation + 1) % 3);
            Edited(p);
        }
    }

    private void DrawParamEditor(QuickGui gui, Profile p, ModuleInstance m, ParamDef pd, float colW)
    {
        Resolver.Materialize(m); // before any edit below: unchanged values, but no longer tied to wrecks
        double v = Resolver.ParamOf(m, pd.Key);
        switch (pd.Kind)
        {
            case ParamKind.Bool:
                {
                    bool cur = v != 0, nv = cur;
                    gui.ToggleButton(ToggleButton.Style.Check, "    " + L.T(pd.LabelKey), ref nv, H40);
                    if (nv != cur)
                    {
                        m.Params[pd.Key] = nv ? 1 : 0;
                        Edited(p);
                    }
                    return;
                }
            case ParamKind.Choice:
                {
                    string[] ch = pd.Choices!;
                    gui.Label("    " + L.T(pd.LabelKey), H24, LeftAlign);
                    var bw = W((colW - (ch.Length - 1) * Gap) / ch.Length);
                    using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
                    {
                        for (int k = 0; k < ch.Length; k++)
                        {
                            if (gui.Button(L.T(ch[k]), bw, (int)v == k ? Chosen : Lit, Shrink))
                            {
                                m.Params[pd.Key] = k;
                                Edited(p);
                            }
                        }
                    }
                    return;
                }
            default:
                {
                    string shown = Registry.NumberText(pd, v, L.T("MiningDrone/Infinite"));
                    gui.Label("    " + L.T(pd.LabelKey) + ": " + shown, H24, LeftAlign);
                    float f = (float)v, nf = f;
                    Slider(gui, ref nf, pd.Min, pd.Max, pd.Step);
                    if (nf != f)
                    {
                        m.Params[pd.Key] = pd.Clamp(pd.Step > 0 ? Math.Round(nf / pd.Step) * pd.Step : nf);
                        Edited(p);
                    }
                    return;
                }
        }
    }

    private void DrawAddModule(QuickGui gui, Profile p, float colW)
    {
        // Only module types the profile doesn't have yet (the resolver ignores a second instance of a type), and only
        // implemented ones (future modules are listed in Settings > Coming later).
        if (_addFor != Store.LibraryVersion || _addForProfile != p.Id)
        {
            _addFor = Store.LibraryVersion;
            _addForProfile = p.Id;
            _addNames.Clear();
            _addIds.Clear();
            foreach (ModuleDef d in Registry.All)
            {
                if (d.Implemented && !HasModule(p, d.TypeId))
                {
                    _addNames.Add(L.T(d.NameKey));
                    _addIds.Add(d.TypeId);
                }
            }
            _addIdx = _addIds.Count == 0 ? 0 : Math.Clamp(_addIdx, 0, _addIds.Count - 1);
        }
        if (_addIds.Count == 0)
        {
            gui.Label(L.T("MiningDrone/AllModulesAdded"), AutoH, LeftAlign, Wrap);
            return;
        }
        gui.Label(L.T("MiningDrone/AddModuleLabel"), H24, LeftAlign);
        const float AddW = 160f;
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H56, Fixed))
        {
            gui.DropList(_addNames, ref _addIdx, W(colW - AddW - Gap));
            if (gui.Button(L.T("MiningDrone/AddModule"), W(AddW), Shrink) && _addIdx >= 0 && _addIdx < _addIds.Count)
            {
                string type = _addIds[_addIdx];
                if (!HasModule(p, type))
                {
                    p.Modules.Add(Registry.NewInstance(type, Registry.NewId(), Registry.Get(type)!.Implemented));
                    Edited(p);
                }
            }
        }
        if (_addIdx >= 0 && _addIdx < _addIds.Count)
        {
            gui.Label(L.T(Registry.Get(_addIds[_addIdx])!.HintKey), AutoH, LeftAlign, Wrap);
        }
    }

    private static bool HasModule(Profile p, string type)
    {
        foreach (ModuleInstance m in p.Modules)
        {
            if (m.Type == type)
            {
                return true;
            }
        }
        return false;
    }
}
