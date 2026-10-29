using System;
using System.Collections.Generic;
using Cosmoteer.Game;
using Cosmoteer.Game.Gui;
using Cosmoteer.Gui;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Halfling;
using Halfling.Geometry;
using Halfling.Graphics.Text;
using Halfling.Gui;
using Halfling.Gui.Components.Children;
using Halfling.Gui.Layout;
using Halfling.Gui.Quick;
using Halfling.Gui.Quick.WidgetModifiers;
using Halfling.Input;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>Quick-GUI modifier from a delegate (static singletons only, so declarations stay allocation-free).</summary>
internal sealed class Mod<T> : IQuickWidgetModifier<T> where T : class
{
    private readonly Action<T> _apply;

    public Mod(Action<T> apply) => _apply = apply;

    public void Modify(T widget) => _apply(widget);

    void IQuickWidgetModifier.Modify(object widget)
    {
        if (widget is T t)
        {
            _apply(t);
        }
    }
}

/// <summary>
/// Автофлот window shell (spec §1): the tab bar (Назначения, Профили, Настройки) on top. Назначения: fleet tree with
/// checkboxes on the left (the window's targets, independent of the game selection), on the right the targets'
/// title, status, module-derived command bar and the assignment editor. Multi-target shows mixed values and every
/// action writes only the touched field, to every target.
/// </summary>
internal sealed partial class MinerWindow : QuickWindowBox
{
    private const float Pad = 16f;
    private const float Gap = 8f;

    // ---- static modifiers (allocated once) ----
    private static readonly IQuickWidgetModifier<Widget> H24 = QuickWidgetModifiers.Height(24f);
    private static readonly IQuickWidgetModifier<Widget> H28 = QuickWidgetModifiers.Height(28f);
    private static readonly IQuickWidgetModifier<Widget> H40 = QuickWidgetModifiers.Height(40f);
    private static readonly IQuickWidgetModifier<Widget> H44 = QuickWidgetModifiers.Height(44f);
    private static readonly IQuickWidgetModifier<Widget> H48 = QuickWidgetModifiers.Height(48f);
    private string? _statusTipFor;
    private IQuickWidgetModifier<Widget>? _statusTip;
    private static readonly IQuickWidgetModifier<Widget> H56 = QuickWidgetModifiers.Height(56f);
    private static readonly IQuickWidgetModifier<Widget> H72 = QuickWidgetModifiers.Height(72f);
    private static readonly IQuickWidgetModifier<Widget> AutoH = QuickWidgetModifiers.AutoHeight;
    private static readonly IQuickWidgetModifier<Widget> Disabled = QuickWidgetModifiers.InputDisabled;
    private static readonly IQuickWidgetModifier<Label> LeftAlign = QuickWidgetModifiers.TextAlignLeft;
    private static readonly IQuickWidgetModifier<Label> Wrap = QuickWidgetModifiers.WrapText;
    private static readonly IQuickWidgetModifier<Label> Bold = QuickWidgetModifiers.Bold;
    private static readonly IQuickWidgetModifier<Label> ShrinkLabel = new Mod<Label>(static l => l.TextRenderer.OversizeMode = OversizeMode.Shrink);
    private static readonly IQuickWidgetModifier<Label> Ellipsis = new Mod<Label>(static l => l.TextRenderer.OversizeMode = OversizeMode.Ellipses);
    private static readonly IQuickWidgetModifier<Button> Shrink = new Mod<Button>(static b =>
    {
        b.StateNormalTextRenderer.OversizeMode = OversizeMode.Shrink;
        b.StateHighlightedTextRenderer.OversizeMode = OversizeMode.Shrink;
        b.StatePressedTextRenderer.OversizeMode = OversizeMode.Shrink;
        b.StateDisabledTextRenderer.OversizeMode = OversizeMode.Shrink;
    });
    private static readonly IQuickWidgetModifier<ScrollBox<Widget>> VScrollOnly = new Mod<ScrollBox<Widget>>(static s =>
    {
        s.ScrollBarController.ShowHorizontal = ScrollBarShowMode.Never;
        s.ScrollBarController.ShowVertical = ScrollBarShowMode.WhenNeeded;
    });
    // Quick LayoutRegion defaults to AutoWidth+AutoHeight (LayoutRegionType.DefaultLayoutRegion). With a Stretch
    // algorithm the children take their size from the container, so an auto-sized region collapses to 0 and
    // everything inside vanishes. Rows/panels with an explicit size must switch auto-size off.
    // The region default WidgetPadding is 2 px (LayoutRegionType.DefaultLayoutRegion); all row math here uses Gap,
    // so the padding is set to Gap to make the math match the layout.
    private static readonly IQuickWidgetModifier<LayoutBox<Widget, Widget>> Fixed = new Mod<LayoutBox<Widget, Widget>>(static b =>
    {
        b.AutoSize.AutoWidthMode = AutoSizeMode.Disable;
        b.AutoSize.AutoHeightMode = AutoSizeMode.Disable;
        b.Children.WidgetPadding = new Vector2(Gap, Gap);
    });
    // A draggable vertical block (SortList): width from the row math, height from its children.
    private static readonly IQuickWidgetModifier<LayoutBox<Widget, Widget>> Section = new Mod<LayoutBox<Widget, Widget>>(static b =>
    {
        b.AutoSize.AutoWidthMode = AutoSizeMode.Disable;
        b.Children.WidgetPadding = new Vector2(Gap, Gap);
    });
    private static readonly IQuickWidgetModifier<LayoutBox<Widget, Widget>> FixedWidth = new Mod<LayoutBox<Widget, Widget>>(static b => b.AutoSize.AutoWidthMode = AutoSizeMode.Disable);
    private static readonly IQuickWidgetModifier<Widget> Lit = QuickWidgetModifiers.ColorScale(1f, 1f, 1f, 1f);
    private static readonly IQuickWidgetModifier<Widget> Dim = QuickWidgetModifiers.ColorScale(0.35f, 0.35f, 0.35f, 0.45f);
    private static readonly IQuickWidgetModifier<Widget> Partial = QuickWidgetModifiers.ColorScale(1f, 0.85f, 0.45f, 0.8f);
    private static readonly IQuickWidgetModifier<Widget> Chosen = QuickWidgetModifiers.ColorScale(0.55f, 1f, 0.55f, 1f);
    private static readonly IQuickWidgetModifier<Widget> Warn = QuickWidgetModifiers.ColorScale(1f, 0.75f, 0.35f, 1f);
    private static readonly IQuickWidgetModifier<Widget> IconSize = QuickWidgetModifiers.Size(48f, 48f);
    private static readonly IQuickWidgetModifier<ScrollBar> RadiusMin = QuickWidgetModifiers.MinValue(250f);
    private static readonly IQuickWidgetModifier<ScrollBar> RadiusMax = QuickWidgetModifiers.MaxValue(20000f);

    private static readonly Dictionary<int, IQuickWidgetModifier<Widget>> Widths = new();

    private static IQuickWidgetModifier<Widget> W(float px)
    {
        int k = Math.Max(1, (int)px);
        if (!Widths.TryGetValue(k, out var m))
        {
            m = QuickWidgetModifiers.Width(k);
            Widths[k] = m;
        }
        return m;
    }

    private static readonly Dictionary<int, IQuickWidgetModifier<Widget>> Heights = new();

    private static IQuickWidgetModifier<Widget> Hgt(float px)
    {
        int k = Math.Max(1, (int)px);
        if (!Heights.TryGetValue(k, out var m))
        {
            m = QuickWidgetModifiers.Height(k);
            Heights[k] = m;
        }
        return m;
    }

    /// <summary>
    /// Measures a ScrollBox's real content width. Quick modifiers run only when the widget is created
    /// (QuickWidgetManager.DeclareWidget), so the probe keeps the instance and reads its children's client rect
    /// (the viewport minus the scroll bar) from the last layout. Row widths come from this, never from a guess.
    /// </summary>
    private sealed class WidthProbe
    {
        private ScrollBox<Widget>? _box;
        public readonly IQuickWidgetModifier<ScrollBox<Widget>> Mod;

        public WidthProbe() => Mod = new Mod<ScrollBox<Widget>>(s => _box = s);

        /// <summary>Content width of the probed box, or <paramref name="fallback"/> before its first layout.</summary>
        public float Width(float fallback)
        {
            ScrollBox<Widget>? b = _box;
            if (b == null || b.Parent == null)
            {
                return fallback;
            }
            float w = (float)Math.Floor(b.Children.ClientScreenRect.Width) - 1f;
            return w > 48f ? w : fallback;
        }

        /// <summary>On-screen rect of the probed box (drag auto-scroll edges), or null before its first layout.</summary>
        public Rect? Viewport => _box?.Parent != null ? _box.ScreenRect : null;
    }

    private readonly WidthProbe _probeAssignOne = new();
    private readonly WidthProbe _probeProfile = new();
    private readonly WidthProbe _probeProfList = new();

    // tooltips (created once per window; strings are loaded by then)
    private readonly IQuickWidgetModifier<Widget> _tipFromGame = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipFromGameSelection"));
    private readonly IQuickWidgetModifier<Widget> _tipMoveUp = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipFolderUp"));
    private readonly IQuickWidgetModifier<Widget> _tipMoveDown = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipFolderDown"));
    private readonly IQuickWidgetModifier<Widget> _tipNoOffloadPoint = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipNoOffloadPoint"));
    private readonly IQuickWidgetModifier<Widget> _tipCmdSubset = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipCmdSubset"));
    private readonly IQuickWidgetModifier<Widget> _tipPrioDown = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipPrioDown"));
    private readonly IQuickWidgetModifier<Widget> _tipPrioUp = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipPrioUp"));
    private readonly IQuickWidgetModifier<Widget> _tipActivation = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipActivation"));
    private readonly IQuickWidgetModifier<Widget> _tipRemove = QuickWidgetModifiers.ToolTip(L.T("MiningDrone/TipRemoveModule"));

    private static readonly Dictionary<(int, int, int, int), IQuickWidgetModifier<ScrollBar>[]> Ranges = new();

    /// <summary>Cached min/max/step slider modifiers for a param descriptor range.</summary>
    private static IQuickWidgetModifier<ScrollBar>[] Range(double min, double max, double step)
    {
        var key = ((int)(min * 1000), (int)(max * 1000), (int)(step * 1000), 0);
        if (!Ranges.TryGetValue(key, out var m))
        {
            m = step > 0
                ? new[] { QuickWidgetModifiers.MinValue((float)min), QuickWidgetModifiers.MaxValue((float)max), QuickWidgetModifiers.ScrollInterval((float)step) }
                : new[] { QuickWidgetModifiers.MinValue((float)min), QuickWidgetModifiers.MaxValue((float)max) };
            Ranges[key] = m;
        }
        return m;
    }

    private static void Slider(QuickGui gui, ref float v, double min, double max, double step)
    {
        var m = Range(min, max, step);
        // Compare against the value the ScrollBar actually holds: it snaps to Round(v / step) * step in float (0.65 at
        // step 0.05 -> 0.65000004), and a reported "change" every frame invalidates all later widget outputs (lost clicks).
        float shown = Snap(v, step), nv = shown;
        if (m.Length == 3)
        {
            gui.HorizontalSlider(ref nv, H40, m[0], m[1], m[2]);
        }
        else
        {
            gui.HorizontalSlider(ref nv, H40, m[0], m[1]);
        }
        if (nv != shown)
        {
            v = nv;
        }
    }

    private readonly GameRoot _game;

    // window targets: the checked ships of the fleet tree, rebuilt every frame (no allocation). Independent of the
    // game selection, which is imported only when the window opens or on demand.
    private readonly List<Ship> _ships = new();
    private readonly List<ShipAssignment> _asg = new();
    private readonly HashSet<string> _checked = new();
    private readonly HashSet<string> _collapsed = new();

    // fleet tree: every own ship, grouped by profile in library order, unmanaged last (refreshed 1 Hz)
    private readonly List<Ship> _fleet = new();
    private readonly List<string> _fleetIds = new();
    private readonly List<string> _ownIds = new(); // checkable rows: ships this client drives (others are read-only)
    private readonly List<string> _fleetText = new();
    private readonly List<IQuickWidgetModifier<Widget>?> _fleetTips = new();
    private readonly List<TreeGroup> _groups = new();
    private long _nextListRefresh;
    private readonly WidthProbe _probeTree = new();

    // drag-reorder lists (UI-only state; the model changes on drop)
    private readonly SortList _sortModules = new(), _sortProfiles = new(), _sortFolders = new(), _sortPrio = new();

    private sealed class TreeGroup
    {
        public string Key = "";
        public string Title = "";
        public Profile? Profile;
        public readonly List<int> Rows = new();
        public readonly List<string> Ids = new();
    }

    private int _loggedSig;
    private Vector2 _listScroll;
    private Vector2 _colScroll1;
    private Vector2 _colScroll2;
    private int _tab;

    private static readonly string[] TabKeys =
    {
        "MiningDrone/TabAssign", "MiningDrone/TabProfiles", "MiningDrone/TabBuild", "MiningDrone/TabLogistics", "MiningDrone/TabEconomy", "MiningDrone/TabSettings",
    };

    // Build / Logistics / Economy have no controls yet: they are listed under "Coming later" in Settings instead of
    // taking tab space (indices into TabKeys stay stable for when they ship).
    private static readonly int[] VisibleTabs = { 0, 1, 5 };

    public MinerWindow(GameRoot game, GameGui gui)
        : base(null)
    {
        _game = game;
        TitleText = L.T("MiningDrone/Title");
        BoundsProvider = gui.RootWidget;
        Children.BorderPadding = new Borders(Pad);
        Children.WidgetPadding = new Vector2(Pad, Pad);
        QuickWidgetManager.Callback += OnGui;
        Deactivated += static (_, _) => Ui.OnWindowClosed();
        Deactivated += (_, _) =>
        {
            _sortModules.Cancel();
            _sortProfiles.Cancel();
            _sortFolders.Cancel();
            _sortPrio.Cancel();
        };
        // Vanilla windows (ResourceLimitsWindow.cs:591) draw their backdrop via AddBlur.
        WidgetRules.AddBlur(this);
    }

    private void OnGui(QuickGui gui)
    {
        try
        {
            var sim = _game.Sim;
            if (sim == null)
            {
                gui.Label(L.T("MiningDrone/NoGame"), H40);
                return;
            }
            BuildScope(sim);
            // Sizes come from the window's own on-screen rect (never Widget.Size of anchored children).
            Rect scr = ScreenRect; // fill-anchored: Width/Height are the zero anchor offsets
            float innerW = scr.Width - 2f * Pad - 8f;
            float clientH = scr.Height - 2f * Pad - 48f; // title bar
            float bodyH = Math.Max(120f, clientH - 40f - Pad);
            float listW = innerW >= 1150f ? 320f : innerW >= 990f ? 300f : 260f; // folder rows carry fold, check, title, ↑↓
            float editorW = innerW - listW - Gap;
            int sig = (int)scr.Width * 10000 + (int)scr.Height;
            if (sig != _loggedSig)
            {
                _loggedSig = sig;
                Log.Info($"UI layout: window {scr.Width:0}x{scr.Height:0}, tree {listW:0}, right {editorW:0}, body h {bodyH:0}");
            }

            // Global navigation first; everything below depends on the tab (spec §1).
            gui.LayoutAlgorithm = LayoutAlgorithms.StretchTopToBottom;
            DrawTabs(gui, innerW);
            switch (_tab)
            {
                case 1:
                    DrawProfilesTab(gui, innerW, bodyH);
                    break;
                case 5:
                    DrawSettingsTab(gui, bodyH);
                    break;
                default:
                    _tab = 0;
                    using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, Hgt(bodyH), Fixed))
                    {
                        using (gui.ScrollBox(LayoutAlgorithms.StretchTopToBottom, ref _listScroll, W(listW), VScrollOnly, _probeTree.Mod))
                        {
                            DrawFleetTree(gui, listW);
                        }
                        using (gui.LayoutRegion(LayoutAlgorithms.StretchTopToBottom, W(editorW), Fixed))
                        {
                            float used = DrawHeader(gui, editorW);
                            DrawAssignTab(gui, sim, editorW, Math.Max(120f, bodyH - used - Gap));
                        }
                    }
                    break;
            }
            if (((IQuickGui)gui).Phase == QuickGuiPhase.Output)
            {
                _sortModules.Sweep();
                _sortProfiles.Sweep();
                _sortFolders.Sweep();
                _sortPrio.Sweep();
            }
        }
        catch (Exception ex)
        {
            Log.Error("ui", ex);
        }
    }

    private void DrawTabs(QuickGui gui, float innerW)
    {
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            var tw = W((innerW - (VisibleTabs.Length - 1) * Gap) / VisibleTabs.Length);
            foreach (int t in VisibleTabs)
            {
                if (gui.Button(L.T(TabKeys[t]), tw, _tab == t ? Chosen : Lit, Shrink))
                {
                    _tab = t;
                }
            }
        }
    }

    // ------------------------------------------------------------------ scope

    /// <summary>Window targets <- the game selection (own, non-asteroid ships). Called when the window opens and by
    /// the tree button; an empty game selection keeps the current targets.</summary>
    public void ImportGameSelection()
    {
        var sim = _game.Sim;
        if (sim == null)
        {
            return;
        }
        bool any = false;
        foreach (Ship s in sim.PlayerInput.SelectedShips)
        {
            if (World.Drives(_game, s) && !s.Rules.IsAsteroid)
            {
                if (!any)
                {
                    _checked.Clear();
                    any = true;
                }
                _checked.Add(s.UniqueID.ToString());
            }
        }
        _nextListRefresh = 0;
    }

    /// <summary>Checked ship IDs (the area picker places the circle for these while the window is open).</summary>
    public IEnumerable<string> TargetIds
    {
        get
        {
            for (int i = 0; i < _fleet.Count; i++)
            {
                // Live owner check, as in BuildScope: a ship given away since the last tree refresh is not placed.
                if (_checked.Contains(_fleetIds[i]) && World.Drives(_game, _fleet[i]))
                {
                    yield return _fleetIds[i];
                }
            }
        }
    }

    private void BuildScope(Cosmoteer.Simulation.SimRoot sim)
    {
        long now = Environment.TickCount64;
        if (now >= _nextListRefresh)
        {
            _nextListRefresh = now + 1000;
            RefreshFleet(sim);
        }
        _ships.Clear();
        _asg.Clear();
        for (int i = 0; i < _fleet.Count; i++)
        {
            Ship s = _fleet[i];
            // Live owner check: the tree refreshes at 1 Hz, a ship given away meanwhile must not be edited.
            if (!_checked.Contains(_fleetIds[i]) || s.IsJunk || !s.ExistsInSim(sim) || !World.Drives(_game, s))
            {
                continue;
            }
            _ships.Add(s);
            Vector2 p = s.WorldCenter;
            // Draft only: the area circle starts on the ship, not at world (0,0).
            _asg.Add(Store.GetOrCreateDraft(_fleetIds[i], p.X, p.Y, s.Metadata.PlayerIndex));
        }
        // Preview only for a single unassigned ship; automated ships are drawn by MinerManager.
        MinerManager.PreviewArea = null;
        if (_ships.Count == 1 && MinerManager.Get(_ships[0]) == null)
        {
            ShipAssignment a = _asg[0];
            HarvestScope scope = (HarvestScope)(int)(Resolver.Param(a, Registry.Mine, Registry.PScope) ?? 0);
            double maxSearch = Resolver.Param(a, Registry.Mine, Registry.PMaxSearch) ?? 30000;
            if (scope == HarvestScope.Zone)
            {
                MinerManager.PreviewArea = new Circle(a.Bindings.Zone.X, a.Bindings.Zone.Y, a.Bindings.Zone.R);
            }
            else if (scope == HarvestScope.Nearest && maxSearch < Registry.UnlimitedSearch)
            {
                MinerManager.PreviewArea = new Circle(_ships[0].WorldCenter, (float)maxSearch);
            }
        }
    }

    private bool Same<T>(Func<ShipAssignment, T> get)
    {
        if (_asg.Count <= 1)
        {
            return true;
        }
        T first = get(_asg[0]);
        var cmp = EqualityComparer<T>.Default;
        for (int i = 1; i < _asg.Count; i++)
        {
            if (!cmp.Equals(first, get(_asg[i])))
            {
                return false;
            }
        }
        return true;
    }

    private void ForAll(Action<ShipAssignment> set)
    {
        for (int i = 0; i < _asg.Count; i++)
        {
            set(_asg[i]);
            Store.Touch(_asg[i]);
        }
    }

    // ------------------------------------------------------------------ left: fleet tree

    private const float CheckW = 36f;
    private const float FoldW = 28f;
    private const float ShipIndent = 2f * FoldW + Gap;

    /// <summary>Accordion: "All ships" -> profile folders (library order, ↑↓ reorder) -> ships; unmanaged ships in
    /// a last folder. Checkboxes on every level; a folder/root counts collapsed ships too.</summary>
    private void DrawFleetTree(QuickGui gui, float listW)
    {
        float rowW = _probeTree.Width(listW - 24f);
        gui.Label(L.T("MiningDrone/FleetTree"), H28, LeftAlign, Bold);
        if (gui.Button(L.T("MiningDrone/FromGameSelection"), H28, Shrink, _tipFromGame))
        {
            ImportGameSelection();
        }
        if (_fleet.Count == 0)
        {
            gui.Label(L.T("MiningDrone/NoMiners"), AutoH, LeftAlign, Wrap);
            return;
        }
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H28, Fixed))
        {
            NodeCheck(gui, _ownIds);
            gui.Label(L.T("MiningDrone/AllShips") + "  " + FleetChecks.Count(_ownIds, _checked) + "/" + _ownIds.Count, W(rowW - CheckW - Gap), LeftAlign, Bold, ShrinkLabel);
        }
        // Each folder (header + ships) is a draggable block; drag its title to reorder profiles (↑/↓ stay).
        _sortFolders.Begin();
        for (int gi = 0; gi < _groups.Count; gi++)
        {
            TreeGroup g = _groups[gi];
            using (gui.LayoutRegion(LayoutAlgorithms.StretchTopToBottom, W(rowW), Section, g.Profile == null ? null : _sortFolders.Block(g.Profile.Id, g.Title)))
            {
                DrawFolder(gui, gi, rowW);
            }
        }
        if (_sortFolders.End(gui, _probeTree.Viewport, ref _listScroll) is { } drop && Store.FindProfile(drop.Key) is { } dp)
        {
            Store.MoveProfile(dp, Store.FindProfile(drop.Before), Store.FindProfile(drop.After));
            _nextListRefresh = 0;
        }
    }

    private void DrawFolder(QuickGui gui, int gi, float rowW)
    {
        TreeGroup g = _groups[gi];
        bool open = !_collapsed.Contains(g.Key);
        bool movable = g.Profile != null;
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H28, Fixed))
        {
            if (gui.Button(open ? "−" : "+", W(FoldW), Shrink))
            {
                if (!_collapsed.Remove(g.Key))
                {
                    _collapsed.Add(g.Key);
                }
            }
            NodeCheck(gui, g.Ids);
            float titleW = rowW - FoldW - CheckW - 2f * Gap - (movable ? 2f * (FoldW + Gap) : 0f);
            gui.Label(g.Title + "  " + FleetChecks.Count(g.Ids, _checked) + "/" + g.Ids.Count, W(titleW), LeftAlign, Bold, ShrinkLabel, movable ? _sortFolders.Grab(g.Profile!.Id) : null);
            if (movable)
            {
                // Swap with the neighbouring folder (profiles without ships have no folder and stay put).
                Profile? up = gi > 0 ? _groups[gi - 1].Profile : null;
                Profile? down = gi + 1 < _groups.Count ? _groups[gi + 1].Profile : null;
                if (gui.Button("↑", W(FoldW), Shrink, up == null ? Disabled : null, _tipMoveUp) && up != null)
                {
                    Store.SwapProfiles(g.Profile!, up);
                    _nextListRefresh = 0;
                }
                if (gui.Button("↓", W(FoldW), Shrink, down == null ? Disabled : null, _tipMoveDown) && down != null)
                {
                    Store.SwapProfiles(g.Profile!, down);
                    _nextListRefresh = 0;
                }
            }
        }
        if (!open)
        {
            return;
        }
        for (int k = 0; k < g.Rows.Count; k++)
        {
            int r = g.Rows[k];
            string id = _fleetIds[r];
            using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H44, Fixed))
            {
                // Depth step = CheckW: root check at 0, folder check at FoldW+Gap (36), ship check at 2*36.
                gui.Label("", W(ShipIndent));
                bool was = _checked.Contains(id);
                bool v = was;
                // Another player's ship (shared with us): shown with its owner, never checkable here.
                bool ro = !World.Drives(_game, _fleet[r]);
                gui.ToggleButton(ToggleButton.Style.Check, "", ref v, W(CheckW), ro ? Disabled : null);
                // The text is a button too: a click anywhere on the row toggles the check.
                if (gui.Button(_fleetText[r], W(rowW - ShipIndent - CheckW - 2f * Gap), Shrink, ro ? Disabled : v ? Chosen : Lit, _fleetTips[r]))
                {
                    v = !v;
                }
                if (v != was && !ro)
                {
                    if (v)
                    {
                        _checked.Add(id);
                    }
                    else
                    {
                        _checked.Remove(id);
                    }
                }
            }
        }
    }
    /// <summary>Folder/root checkbox: checked = all, orange = partial (a click checks all), clear = none.</summary>
    private void NodeCheck(QuickGui gui, List<string> ids)
    {
        CheckState st = FleetChecks.State(ids, _checked);
        bool all = st == CheckState.All;
        bool v = all;
        gui.ToggleButton(ToggleButton.Style.Check, "", ref v, W(CheckW), st == CheckState.Partial ? Partial : Lit);
        if (v != all)
        {
            FleetChecks.Set(ids, _checked, v);
        }
    }

    private readonly List<(Ship Ship, ShipAgent? Agent, int Order)> _sortBuf = new();

    private void RefreshFleet(Cosmoteer.Simulation.SimRoot sim)
    {
        _fleet.Clear();
        _fleetIds.Clear();
        _ownIds.Clear();
        _fleetText.Clear();
        _fleetTips.Clear();
        _groups.Clear();
        _sortBuf.Clear();
        var ships = sim.Ships;
        for (int i = 0; i < ships.Count; i++)
        {
            Ship s = ships[i];
            if (!s.IsLocalPlayerShip || s.Rules.IsAsteroid || s.IsJunk || !s.IsSignificant)
            {
                continue;
            }
            ShipAgent? c = MinerManager.Get(s);
            // Folder order = library order; an unknown profile id after the library, unmanaged ships last.
            int order = c == null ? int.MaxValue : Store.Profiles.IndexOf(Store.ProfileFor(c.Assignment.ProfileId));
            _sortBuf.Add((s, c, order < 0 ? int.MaxValue - 1 : order));
        }
        _sortBuf.Sort(static (a, b) =>
        {
            int c = a.Order.CompareTo(b.Order);
            if (c == 0 && a.Agent != null && b.Agent != null)
            {
                c = string.CompareOrdinal(a.Agent.Assignment.ProfileId, b.Agent.Assignment.ProfileId);
            }
            return c != 0 ? c : a.Ship.UniqueID.CompareTo(b.Ship.UniqueID);
        });
        TreeGroup? g = null;
        for (int i = 0; i < _sortBuf.Count; i++)
        {
            (Ship s, ShipAgent? c, _) = _sortBuf[i];
            string key = c == null ? "" : c.Assignment.ProfileId;
            if (g == null || g.Key != key)
            {
                Profile? p = c == null ? null : Store.FindProfile(key);
                g = new TreeGroup
                {
                    Key = key,
                    Profile = p,
                    Title = c == null ? L.T("MiningDrone/GroupUnmanaged") : ProfileName(c.Assignment),
                };
                _groups.Add(g);
            }
            string id = s.UniqueID.ToString();
            string name = s.Metadata.PlainShipName;
            g.Rows.Add(_fleet.Count);
            _fleet.Add(s);
            _fleetIds.Add(id);
            if (!World.Drives(_game, s))
            {
                string by = L.F("MiningDrone/DrivenBy", _game.Mode.GetPlayerName(s.Metadata.PlayerIndex) ?? L.T("Misc/Unknown"));
                _fleetText.Add(name + "\n" + by);
                _fleetTips.Add(QuickWidgetModifiers.ToolTip(name + "\n" + by));
                continue;
            }
            g.Ids.Add(id);
            _ownIds.Add(id);
            if (c == null)
            {
                _fleetText.Add(name);
                _fleetTips.Add(QuickWidgetModifiers.ToolTip(name));
                continue;
            }
            string line2 = L.Module(c.Owner) + " · " + L.State(c.State) + (HasCargo(c.Cfg) ? " · " + c.Percent + "%" : "");
            _fleetText.Add(name + "\n" + line2);
            _fleetTips.Add(QuickWidgetModifiers.ToolTip(name + "\n" + c.StatusText));
        }
        _checked.IntersectWith(_ownIds); // destroyed / given-away / other players' ships stop being targets
    }

    private static string ProfileName(ShipAssignment a) => Registry.DisplayName(a.Profile.Name, a.ProfileId);

    /// <summary>Cargo % is shown only when a module works with the hold.</summary>
    private static bool HasCargo(AgentConfig c) => c.Mine.Active || c.Loot.Active || c.Offload.Active;

    // ------------------------------------------------------------------ Назначения: targets header

    /// <summary>Pinned in Назначения: target title, status, command bar. Returns the height used.</summary>
    private float DrawHeader(QuickGui gui, float editorW)
    {
        if (_ships.Count == 0)
        {
            gui.Label(L.T("MiningDrone/SelectShip"), H28, LeftAlign);
            return 28f + Gap;
        }
        string title = _ships.Count == 1 ? _ships[0].Metadata.PlainShipName : L.F("MiningDrone/SelectedCount", _ships.Count);
        gui.Label(title, H28, LeftAlign, Bold, ShrinkLabel);
        float used = 28f + Gap;
        used += DrawStatus(gui);
        used += DrawActions(gui, editorW);
        return used;
    }

    /// <summary>
    /// Command bar. Row 1, universal: Start (unmanaged targets), Pause / Resume (managed), Unassign (managed). Row 2,
    /// module commands from <see cref="ShipCommands"/>: shown when any target's profile contributes them, labelled
    /// "K/N" when only K of N targets can run them, applied only to those K (revalidated on click).
    /// </summary>
    private float DrawActions(QuickGui gui, float editorW)
    {
        int n = _ships.Count, unmanaged = 0, managed = 0, paused = 0, offers = 0, canUnload = 0, canReturn = 0, home = 0;
        for (int i = 0; i < n; i++)
        {
            if (_asg[i].Temp == TempIntent.ReturnHome)
            {
                home++;
            }
            ShipAgent? c = MinerManager.Get(_ships[i]);
            if (c == null)
            {
                unmanaged++;
                continue;
            }
            managed++;
            Resolver.Refresh(c.Cfg, c.Assignment); // a profile edit this frame must not leave stale commands (no-op when unchanged)
            paused += c.State == MinerState.Paused ? 1 : 0;
            offers += ShipCommands.Of(c.Cfg) != ShipCmd.None ? 1 : 0;
            canUnload += ShipCommands.CanRun(c.Cfg, ShipCmd.Unload) ? 1 : 0;
            canReturn += ShipCommands.CanRun(c.Cfg, ShipCmd.Return) && _asg[i].Temp != TempIntent.ReturnHome ? 1 : 0;
        }
        int running = managed - paused;
        int row1 = (unmanaged > 0 ? 1 : 0) + (running > 0 ? 1 : 0) + (paused > 0 ? 1 : 0) + (managed > 0 ? 1 : 0);
        float used = 0f;
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H44, Fixed))
        {
            var bw = W((editorW - (row1 - 1) * Gap) / row1);
            if (unmanaged > 0 && gui.Button(Cnt("MiningDrone/Assign", unmanaged, n), bw, Shrink))
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (MinerManager.Get(_ships[i]) == null)
                    {
                        MinerManager.Assign(_ships[i], _asg[i]);
                    }
                }
            }
            if (running > 0 && gui.Button(Cnt("MiningDrone/Pause", running, n), bw, Shrink))
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    ShipAgent? c = MinerManager.Get(_ships[i]);
                    if (c != null && c.State != MinerState.Paused)
                    {
                        c.PauseByUser(_game);
                    }
                }
            }
            // Resume only the paused automated ships; unmanaged targets are never enrolled silently.
            if (paused > 0 && gui.Button(Cnt("MiningDrone/Resume", paused, n), bw, Shrink))
            {
                for (int i = 0; i < _ships.Count; i++)
                {
                    if (MinerManager.Get(_ships[i])?.State == MinerState.Paused)
                    {
                        MinerManager.Assign(_ships[i], _asg[i]);
                    }
                }
            }
            if (managed > 0)
            {
                if (gui.Button(Cnt("MiningDrone/Unassign", managed, n), bw, Shrink))
                {
                    for (int i = 0; i < _ships.Count; i++)
                    {
                        MinerManager.Unassign(_ships[i]);
                    }
                }
            }
        }
        used += 44f + Gap;

        // Cancel targets the returning subset and stays available after the profile lost Offload.
        int row2 = (offers > 0 ? 2 : 0) + (home > 0 ? 1 : 0);
        if (row2 == 0)
        {
            return used;
        }
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H44, Fixed))
        {
            var bw = W((editorW - (row2 - 1) * Gap) / row2);
            if (offers > 0)
            {
                IQuickWidgetModifier<Widget>? tip = canUnload == 0 ? _tipNoOffloadPoint : canUnload < n ? _tipCmdSubset : null;
                if (gui.Button(Cnt("MiningDrone/UnloadNow", canUnload, n), bw, Shrink, canUnload == 0 ? Disabled : null, tip) && canUnload > 0)
                {
                    for (int i = 0; i < _ships.Count; i++)
                    {
                        ShipAgent? c = MinerManager.Get(_ships[i]);
                        if (c != null && ShipCommands.CanRun(c.Cfg, ShipCmd.Unload))
                        {
                            c.UnloadNow(_game);
                        }
                    }
                }
            }
            if (offers > 0)
            {
                IQuickWidgetModifier<Widget>? tip = canReturn == 0 ? (home > 0 ? null : _tipNoOffloadPoint) : canReturn < n ? _tipCmdSubset : null;
                if (gui.Button(Cnt("MiningDrone/TaskReturn", canReturn, n), bw, Shrink, canReturn == 0 ? Disabled : null, tip) && canReturn > 0)
                {
                    for (int i = 0; i < _ships.Count; i++)
                    {
                        ShipAgent? c = MinerManager.Get(_ships[i]);
                        if (c != null && ShipCommands.CanRun(c.Cfg, ShipCmd.Return))
                        {
                            _asg[i].Temp = TempIntent.ReturnHome;
                            Store.Touch(_asg[i]);
                        }
                    }
                }
            }
            if (home > 0 && gui.Button(Cnt("MiningDrone/CancelReturn", home, n), bw, Shrink, Chosen))
            {
                for (int i = 0; i < _asg.Count; i++)
                {
                    if (_asg[i].Temp == TempIntent.ReturnHome)
                    {
                        _asg[i].Temp = TempIntent.None;
                        Store.Touch(_asg[i]);
                    }
                }
            }
        }
        return used + 44f + Gap;
    }

    /// <summary>Button text with its reach: plain for one ship, "— N" for all N targets, "— K/N" for a subset.</summary>
    private static string Cnt(string key, int k, int n) => n <= 1 ? L.T(key) : k < n ? L.T(key) + " — " + k + "/" + n : L.T(key) + " — " + n;

    private float DrawStatus(QuickGui gui)
    {
        if (_ships.Count == 1)
        {
            ShipAgent? c = MinerManager.Get(_ships[0]);
            if (c == null)
            {
                gui.Label(L.F("MiningDrone/NotAssignedProfile", ProfileName(_asg[0])), H28, LeftAlign, ShrinkLabel);
                return 28f + Gap;
            }
            gui.Label(HasCargo(c.Cfg)
                ? L.F("MiningDrone/StateLineCells", L.State(c.State), c.UsedCells, c.AvailCells, c.Percent)
                : L.F("MiningDrone/StateOnly", L.State(c.State)), H28, LeftAlign);
            // Fixed heights: the returned height sizes the tab below, so wrapped text must not grow past it.
            gui.Label(c.StatusText, H48, LeftAlign, Wrap, Ellipsis);
            string line = L.F("MiningDrone/ModuleLine", L.Module(c.Owner), c.Preempt.Length > 0 ? c.Preempt : "—");
            string blockers = Blockers(c);
            string full = blockers.Length > 0 ? line + "  ·  " + blockers : line;
            if (full != _statusTipFor)
            {
                _statusTipFor = full;
                _statusTip = QuickWidgetModifiers.ToolTip(full);
            }
            gui.Label(full, H24, LeftAlign, Ellipsis, blockers.Length > 0 ? Warn : null, _statusTip);
            return 28f + 48f + 24f + 3f * Gap;
        }
        int working = 0, pausedN = 0, outside = 0, cargo = 0;
        int lo = 100, hi = 0;
        for (int i = 0; i < _ships.Count; i++)
        {
            ShipAgent? c = MinerManager.Get(_ships[i]);
            if (c == null)
            {
                outside++;
                continue;
            }
            if (c.State == MinerState.Paused)
            {
                pausedN++;
            }
            else
            {
                working++;
            }
            if (HasCargo(c.Cfg))
            {
                cargo++;
                lo = Math.Min(lo, c.Percent);
                hi = Math.Max(hi, c.Percent);
            }
        }
        gui.Label(L.F("MiningDrone/GroupStatus", working, pausedN, outside), H28, LeftAlign, ShrinkLabel);
        if (cargo == 0)
        {
            return 28f + Gap;
        }
        gui.Label(L.F("MiningDrone/CargoRange", lo, hi), H28, LeftAlign);
        return 56f + 2f * Gap;
    }

    private static string Blockers(ShipAgent c)
    {
        var b = c.Cfg.Blockers;
        if (b.Count == 0 && c.RuntimeBlocker.Length == 0)
        {
            return "";
        }
        string s = c.RuntimeBlocker;
        for (int i = 0; i < b.Count; i++)
        {
            s = s.Length == 0 ? b[i] : s + "; " + b[i];
        }
        return L.F("MiningDrone/BlockersLine", s);
    }

    // ------------------------------------------------------------------ settings + placeholders

    private void DrawSettingsTab(QuickGui gui, float restH)
    {
        using (gui.ScrollBox(LayoutAlgorithms.StretchTopToBottom, ref _colScroll1, Hgt(restH), VScrollOnly))
        {
            gui.Label(L.T("MiningDrone/SecPrefs"), H28, LeftAlign, Bold);
            PrefToggle(gui, "MiningDrone/ShowCircles", Store.Prefs.ShowCircles, static v => Store.Prefs.ShowCircles = v);
            PrefToggle(gui, "MiningDrone/ShowStateIcons", Store.Prefs.ShowStateIcons, static v => Store.Prefs.ShowStateIcons = v);
            PrefToggle(gui, "MiningDrone/ShowCargoPercent", Store.Prefs.ShowCargoPercent, static v => Store.Prefs.ShowCargoPercent = v);
            PrefToggle(gui, "MiningDrone/KeepInCellsPref", Store.Prefs.KeepInCells, static v => Store.Prefs.KeepInCells = v);
            gui.MenuSeparator();
            gui.Label(L.F("MiningDrone/StateSource", Store.StateSource == "save" ? L.T("MiningDrone/SourceSave") : L.T("MiningDrone/SourceWorking")), AutoH, LeftAlign, Wrap);
            gui.Label(L.T("MiningDrone/SettingsHint"), AutoH, LeftAlign, Wrap);
            gui.MenuSeparator();
            gui.Label(L.T("MiningDrone/SecComingLater"), H28, LeftAlign, Bold);
            gui.Label("• " + L.T("MiningDrone/TabBuild") + ": " + L.T("MiningDrone/PlaceholderBuild"), AutoH, LeftAlign, Wrap);
            gui.Label("• " + L.T("MiningDrone/TabLogistics") + ": " + L.T("MiningDrone/PlaceholderLogistics"), AutoH, LeftAlign, Wrap);
            gui.Label("• " + L.T("MiningDrone/TabEconomy") + ": " + L.T("MiningDrone/PlaceholderEconomy"), AutoH, LeftAlign, Wrap);
        }
    }

    private void PrefToggle(QuickGui gui, string key, bool cur, Action<bool> set)
    {
        bool v = cur;
        gui.ToggleButton(ToggleButton.Style.Check, L.T(key), ref v, H40);
        if (v != cur)
        {
            set(v);
            Store.MarkPrefs();
        }
    }
}
