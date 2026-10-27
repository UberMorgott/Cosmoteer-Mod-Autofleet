using System;
using System.Collections.Generic;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Halfling;
using Halfling.Gui;
using Halfling.Gui.Components.Children;
using Halfling.Gui.Layout;
using Halfling.Gui.Quick;
using Halfling.Gui.Quick.WidgetModifiers;
using Halfling.Input;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>Cargo policy editor (collect filter, keep-on-board, "all") of Профили.</summary>
internal sealed partial class MinerWindow
{
    private readonly CargoScope _profileCargo = new();
    private IQuickWidgetModifier<Widget>[]? _colTips;

    // resource icons
    private IQuickWidgetModifier<Widget>?[] _tips = Array.Empty<IQuickWidgetModifier<Widget>?>();
    private ResourceRules[]? _tipsFor;
    private readonly Dictionary<long, IQuickWidgetModifier<Widget>> _partialTips = new();
    private ILayoutAlgorithm _grid = new LayoutTileAlgorithm(LayoutTileAlgorithm.LayoutDirection.TopLeftHorizontal, wrap: true, maxPerLine: 6);
    private int _gridCols = 6;
    private static readonly IQuickWidgetModifier<Widget> SmallIcon = QuickWidgetModifiers.Size(36f, 36f);

    private CargoScope ProfileCargo(Profile p)
    {
        _profileCargo.Profile = p;
        return _profileCargo;
    }

    private void DrawResources(QuickGui gui, float colW, CargoScope sc)
    {
        // Two separate sections: what to collect (Collect) vs what to keep aboard when offloading (KeepMin/KeepAll).
        ResourceRules[] all = World.Resources;
        if (!ReferenceEquals(all, _tipsFor))
        {
            _tipsFor = all;
            _tips = new IQuickWidgetModifier<Widget>?[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                _tips[i] = QuickWidgetModifiers.ToolTip(all[i].NameKey.Text);
            }
            _partialTips.Clear();
        }
        int cols = Math.Max(1, (int)((colW - 8f) / 56f)); // 48 hit area + 8 gap, from the column's inner width
        if (cols != _gridCols)
        {
            _gridCols = cols;
            _grid = new LayoutTileAlgorithm(LayoutTileAlgorithm.LayoutDirection.TopLeftHorizontal, wrap: true, maxPerLine: cols);
        }
        if (FoldHeader(gui, "MiningDrone/SecCollect", colW))
        {
            gui.Label(L.T("MiningDrone/SecCollectHint"), AutoH, LeftAlign, Wrap);
            ulong ast = World.AsteroidGroupMask;
            DrawGroup(gui, sc, all, ast, "MiningDrone/GroupAsteroid", ast, "MiningDrone/WarnNoOre", colW);
            // Loot picks up any selected stackable (ore included), so its warning checks the whole mask.
            DrawGroup(gui, sc, all, World.StackableMask & ~ast, "MiningDrone/GroupSpace", World.StackableMask, "MiningDrone/WarnNoLoot", colW);
        }
        DrawPriority(gui, sc, all, colW);
        DrawCargoTable(gui, sc, all, colW);
        DrawSurplusTable(gui, sc, all, colW);
    }

    private readonly List<string> _prioShown = new();
    private readonly List<ResourceRules> _prioRows = new();
    private IQuickWidgetModifier<Widget>? _prioTip;

    /// <summary>
    /// Collect priority (D18): the selected types high to low, ↑/↓ per row (like the module tree). The first move stores
    /// the displayed order; selected types not in the list share the last rank. "No priority" clears it (nearest target).
    /// </summary>
    private void DrawPriority(QuickGui gui, CargoScope sc, ResourceRules[] all, float colW)
    {
        gui.MenuSeparator();
        if (!FoldHeader(gui, "MiningDrone/SecPriority", colW))
        {
            return;
        }
        gui.Label(L.T("MiningDrone/SecPriorityHint"), AutoH, LeftAlign, Wrap);
        List<string>? order = sc.Read(0).CollectPriority;
        List<string>? sel = sc.Read(0).Collect;
        _prioShown.Clear();
        _prioRows.Clear();
        int listed = 0;
        if (order != null)
        {
            foreach (string rid in order)
            {
                if (_prioShown.Contains(rid))
                {
                    continue;
                }
                for (int i = 0; i < all.Length && i < 64; i++)
                {
                    ResourceRules r = all[i];
                    if (r.IsStackable && r.Icon != null && (sel == null || sel.Contains(rid)) && r.ID.ToString() == rid)
                    {
                        _prioShown.Add(rid);
                        _prioRows.Add(r);
                        break;
                    }
                }
            }
            listed = _prioShown.Count;
        }
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            ResourceRules r = all[i];
            string rid = r.ID.ToString();
            if (r.IsStackable && r.Icon != null && (sel == null || sel.Contains(rid)) && !_prioShown.Contains(rid))
            {
                _prioShown.Add(rid);
                _prioRows.Add(r);
            }
        }
        _prioTip ??= QuickWidgetModifiers.ToolTip(L.T("MiningDrone/PriorityTip"));
        if (gui.Button(L.T("MiningDrone/PriorityNone"), W(colW), Shrink, order is { Count: > 0 } ? Lit : Disabled, _prioTip) && order != null)
        {
            sc.Write(0).CollectPriority = null;
            return;
        }
        var w36 = W(36f);
        var textW = W(Math.Max(48f, colW - 3f * 36f - 3f * Gap));
        _sortPrio.Begin();
        for (int k = 0; k < _prioRows.Count; k++)
        {
            ResourceRules r = _prioRows[k];
            // Drag the name to reorder (↑/↓ stay as the precise alternative).
            using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed, _sortPrio.Block(_prioShown[k], r.NameKey.Text)))
            {
                gui.ImageButton("", r.Icon!, SmallIcon, _tips[World.IndexOf(r.ID)]); // rows have an icon (filtered above)
                string rank = order is not { Count: > 0 } ? "—" : (Math.Min(k, listed) + 1).ToString();
                gui.Label(rank + "  " + r.NameKey.Text, textW, Ellipsis, _sortPrio.Grab(_prioShown[k]));
                if (gui.Button("↑", w36, Shrink, k == 0 ? Disabled : Lit) && k > 0)
                {
                    CargoPolicy cp = sc.Write(0);
                    cp.CollectPriority = MovePriority(cp.CollectPriority, _prioShown, r.ID.ToString(), k - 1);
                }
                if (gui.Button("↓", w36, Shrink, k == _prioRows.Count - 1 ? Disabled : Lit) && k < _prioRows.Count - 1)
                {
                    CargoPolicy cp = sc.Write(0);
                    cp.CollectPriority = MovePriority(cp.CollectPriority, _prioShown, r.ID.ToString(), k + 1);
                }
            }
        }
        if (_sortPrio.End(gui, _probeProfile.Viewport, ref _colScroll2) is { } drop)
        {
            int to = drop.Before == null ? _prioShown.Count - 1 : _prioShown.IndexOf(drop.Before) - (_prioShown.IndexOf(drop.Before) > _prioShown.IndexOf(drop.Key) ? 1 : 0);
            CargoPolicy cp = sc.Write(0);
            cp.CollectPriority = MovePriority(cp.CollectPriority, _prioShown, drop.Key, to);
        }
    }

    private IQuickWidgetModifier<Widget>[]? _surplusTips;
    private static readonly string[] PolicyKeys = { "MiningDrone/PolNever", "MiningDrone/PolAboveMax", "MiningDrone/PolAboveMin" };

    /// <summary>
    /// Per-resource surplus table (D17): icon | keep-max (−/+, "not set" = unmanaged) | sell | pickup. Keep-max is the
    /// automatic intake ceiling; sell/pickup cycle Never → above max → above min (stable codes, Logistics.Never..AboveMin).
    /// </summary>
    private void DrawSurplusTable(QuickGui gui, CargoScope sc, ResourceRules[] all, float colW)
    {
        gui.MenuSeparator();
        if (!FoldHeader(gui, "MiningDrone/SecSurplus", colW))
        {
            return;
        }
        gui.Label(L.T("MiningDrone/SecSurplusHint"), AutoH, LeftAlign, Wrap);
        bool cellsMode = Store.Prefs.KeepInCells;
        // icon 36 | − 36 | max text | + 36 | sell | pickup: 6 cells, 5 gaps; the two policy buttons share the rest with the text.
        float flex = Math.Max(72f, colW - 3f * 36f - 5f * Gap);
        var textW = W(flex * 0.34f);
        var polW = W(flex * 0.33f);
        var w36 = W(36f);
        _surplusTips ??= new[]
        {
            QuickWidgetModifiers.ToolTip(L.T("MiningDrone/ColMaxTip")), QuickWidgetModifiers.ToolTip(L.T("MiningDrone/ColSellTip")),
            QuickWidgetModifiers.ToolTip(L.T("MiningDrone/ColPickupTip")),
        };
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            gui.Label(L.T("MiningDrone/ColMax"), W(3f * 36f + flex * 0.34f + 3f * Gap), Wrap, _surplusTips[0]);
            gui.Label(L.T("MiningDrone/ColSell"), polW, Wrap, _surplusTips[1]);
            gui.Label(L.T("MiningDrone/ColPickup"), polW, Wrap, _surplusTips[2]);
        }
        int n = sc.Count;
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            ResourceRules r = all[i];
            if (!r.IsStackable || r.Icon == null)
            {
                continue;
            }
            string rid = r.ID.ToString();
            CargoPolicy first = sc.Read(0);
            int max0 = 0;
            bool maxSet0 = first.KeepMax != null && first.KeepMax.TryGetValue(rid, out max0);
            int sell0 = Logistics.Code(first.Sell, r.ID), pick0 = Logistics.Code(first.Pickup, r.ID);
            bool maxSame = true, sellSame = true, pickSame = true;
            for (int s = 1; s < n; s++)
            {
                CargoPolicy cp = sc.Read(s);
                int m = 0;
                bool set = cp.KeepMax != null && cp.KeepMax.TryGetValue(rid, out m);
                maxSame &= set == maxSet0 && (!set || m == max0);
                sellSame &= Logistics.Code(cp.Sell, r.ID) == sell0;
                pickSame &= Logistics.Code(cp.Pickup, r.ID) == pick0;
            }
            // Every stackable type is listed (manufactured goods included), so any of them can be capped or sold.
            using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
            {
                gui.ImageButton("", r.Icon, SmallIcon, _tips[i]);
                int unitStep = App.Keyboard.HotkeyDown(ViKey.Shift, false) ? 10 : 1;
                if (gui.Button("−", w36, Shrink, Lit))
                {
                    StepMax(sc, r, -1, cellsMode, unitStep);
                }
                string text = !maxSame ? "—" : !maxSet0 ? L.T("MiningDrone/KeepUnmanaged") : Amount(r, max0, cellsMode);
                gui.Label(text, textW, Ellipsis);
                if (gui.Button("+", w36, Shrink, Lit))
                {
                    StepMax(sc, r, +1, cellsMode, unitStep);
                }
                if (gui.Button(sellSame ? PolicyLabel(sell0) : "—", polW, Shrink, Lit, _surplusTips[1]))
                {
                    CyclePolicy(sc, rid, sellSame ? sell0 : -1, sell: true);
                }
                if (gui.Button(pickSame ? PolicyLabel(pick0) : "—", polW, Shrink, Lit, _surplusTips[2]))
                {
                    CyclePolicy(sc, rid, pickSame ? pick0 : -1, sell: false);
                }
            }
        }
    }

    private static string PolicyLabel(int code) => code is >= 0 and < 3 ? L.T(PolicyKeys[code]) : "?";

    /// <summary>Never → above max → above min → never; an unknown or mixed value restarts at Never.</summary>
    internal static int NextPolicy(int code) => code is >= 0 and < 2 ? code + 1 : 0;

    private static void CyclePolicy(CargoScope sc, string rid, int cur, bool sell)
    {
        int next = NextPolicy(cur);
        for (int s = 0; s < sc.Count; s++)
        {
            CargoPolicy cp = sc.Write(s);
            Dictionary<string, int> d = sell ? cp.Sell ??= new Dictionary<string, int>() : cp.Pickup ??= new Dictionary<string, int>();
            if (next == Logistics.Never)
            {
                d.Remove(rid);
            }
            else
            {
                d[rid] = next;
            }
        }
    }

    /// <summary>Steps each policy's keep-max from its own value; "not set" + steps from the keep-min, − at 0 clears it.</summary>
    private static void StepMax(CargoScope sc, ResourceRules r, int dir, bool cellsMode, int unitStep)
    {
        int perCell = r.MaxStackSize > 0 ? r.MaxStackSize : 1;
        string rid = r.ID.ToString();
        for (int s = 0; s < sc.Count; s++)
        {
            CargoPolicy cp = sc.Write(s);
            int cur = 0;
            if (cp.KeepMax == null || !cp.KeepMax.TryGetValue(rid, out cur))
            {
                if (dir > 0)
                {
                    int min = cp.KeepMin != null && cp.KeepMin.TryGetValue(rid, out int k) ? Math.Max(0, k) : 0;
                    (cp.KeepMax ??= new Dictionary<string, int>())[rid] = min;
                }
                continue;
            }
            if (dir < 0 && cur <= 0)
            {
                cp.KeepMax!.Remove(rid);
                continue;
            }
            int next = cellsMode
                ? (dir > 0 ? (cur / perCell + 1) * perCell : Math.Max(0, (World.Cells(r, cur) - 1) * perCell))
                : Math.Max(0, cur + dir * unitStep);
            cp.KeepMax![rid] = next;
        }
    }

    /// <summary>
    /// Per-resource keep-on-board table: icon | keep (-/+) | all | on board. Collect is edited only in the icon grid above.
    /// Keep = the profile's keep-min units, pushed to its ships automatically (MinerManager.SyncKeep).
    /// </summary>
    private void DrawCargoTable(QuickGui gui, CargoScope sc, ResourceRules[] all, float colW)
    {
        gui.MenuSeparator();
        if (!FoldHeader(gui, "MiningDrone/SecKeep", colW))
        {
            return;
        }
        gui.Label(L.T("MiningDrone/SecKeepHint"), AutoH, LeftAlign, Wrap);
        bool cellsMode = Store.Prefs.KeepInCells;
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            var half = W((colW - Gap) / 2f);
            if (gui.Button(L.T("MiningDrone/ViewUnits"), half, !cellsMode ? Chosen : Lit, Shrink))
            {
                Store.Prefs.KeepInCells = false;
                Store.MarkPrefs();
            }
            if (gui.Button(L.T("MiningDrone/ViewCells"), half, cellsMode ? Chosen : Lit, Shrink))
            {
                Store.Prefs.KeepInCells = true;
                Store.MarkPrefs();
            }
        }
        // icon 36 | − 36 | keep text | + 36 | all 48 | on board text: 6 cells, 5 gaps. No collect column: collecting is set above only.
        float fixedW = 48f + 3f * 36f + 5f * Gap;
        float textPx = Math.Max(24f, (colW - fixedW) / 2f);
        var textW = W(textPx);
        var w48 = W(48f);
        var w36 = W(36f);
        // Header labels span their whole column group (icon+−/value/+) and wrap to two lines, full text in a tooltip.
        _colTips ??= new[]
        {
            QuickWidgetModifiers.ToolTip(L.T("MiningDrone/ColKeepTip")),
            QuickWidgetModifiers.ToolTip(L.T("MiningDrone/ColKeepAllTip")),
        };
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            gui.Label(L.T("MiningDrone/ColKeep"), W(3f * 36f + textPx + 3f * Gap), Wrap, _colTips[0]);
            gui.Label(L.T("MiningDrone/ColKeepAll"), w48, Wrap, _colTips[1]);
            gui.Label("", textW, Wrap);
        }
        ulong ast = World.AsteroidGroupMask;
        int n = sc.Count;
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            ResourceRules r = all[i];
            if (!r.IsStackable || r.Icon == null)
            {
                continue;
            }
            var id = r.ID;
            string rid = id.ToString();
            bool any = (ast & (1UL << i)) != 0;
            int keepFirst = KeepOf(sc, 0, r, out bool keepSet0);
            bool keepSame = true;
            bool allFirst = sc.Read(0).KeepAll?.Contains(rid) ?? false;
            bool allSame = true;
            any |= keepFirst > 0 || allFirst || keepSet0;
            for (int s = 1; s < n; s++)
            {
                int keepS = KeepOf(sc, s, r, out _);
                bool allS = sc.Read(s).KeepAll?.Contains(rid) ?? false;
                keepSame &= keepS == keepFirst;
                any |= keepS > 0 || allS; // row visibility aggregates the whole selection
                allSame &= allS == allFirst;
            }
            if (!any)
            {
                continue; // only rows that matter: asteroid ore, things aboard, or with a policy set
            }
            using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
            {
                gui.ImageButton("", r.Icon, SmallIcon, _tips[i]);
                // "All" overrides the minimum: −/+ would have no effect, so they are disabled while it is on.
                bool keepAll = allSame && allFirst;
                var stepLook = keepAll ? Disabled : Lit;
                // Stored value is always units. Cells mode steps whole cells (MaxStackSize units each, snapped);
                // units mode steps 1 unit (Shift = 10).
                int unitStep = App.Keyboard.HotkeyDown(ViKey.Shift, false) ? 10 : 1;
                if (gui.Button("−", w36, Shrink, stepLook))
                {
                    StepKeep(sc, r, -1, cellsMode, unitStep);
                }
                string keepText = keepAll ? L.T("MiningDrone/KeepAllValue")
                    : !keepSame ? "—"
                    : !keepSet0 ? L.T("MiningDrone/KeepUnmanaged")
                    : Amount(r, keepFirst, cellsMode);
                gui.Label(keepText, textW, Ellipsis);
                if (gui.Button("+", w36, Shrink, stepLook))
                {
                    StepKeep(sc, r, +1, cellsMode, unitStep);
                }
                // "All": never offload this type (our setting; no vanilla desire change, so no forced resupply).
                bool keepAllNew = keepAll;
                gui.ToggleButton(ToggleButton.Style.Check, allSame ? "" : "—", ref keepAllNew, w48, _colTips[1]);
                if (keepAllNew != keepAll)
                {
                    SetKeepAll(sc, rid, keepAllNew);
                }
                gui.Label("", textW, Ellipsis);
            }
        }
    }

    /// <summary>The profile's keep-min entry (absent = not managed).</summary>
    private static int KeepOf(CargoScope sc, int i, ResourceRules r, out bool set)
    {
        int v = 0;
        set = sc.Profile.Cargo.KeepMin?.TryGetValue(r.ID.ToString(), out v) == true;
        return v;
    }

    private static string Amount(ResourceRules r, int amount, bool cellsMode)
    {
        float div = r.QuantityDisplayDivisor > 0f ? r.QuantityDisplayDivisor : 1f;
        return L.F(cellsMode ? "MiningDrone/CellsFirst" : "MiningDrone/Units", (amount / div).ToString("0"), World.Cells(r, amount));
    }

    /// <summary>Steps the profile's keep-min by one cell or unit step.</summary>
    private static void StepKeep(CargoScope sc, ResourceRules r, int dir, bool cellsMode, int unitStep)
    {
        int perCell = r.MaxStackSize > 0 ? r.MaxStackSize : 1;
        string rid = r.ID.ToString();
        for (int s = 0; s < sc.Count; s++)
        {
            int cur = KeepOf(sc, s, r, out _);
            int next = cellsMode
                ? (dir > 0 ? (cur / perCell + 1) * perCell : Math.Max(0, (World.Cells(r, cur) - 1) * perCell))
                : Math.Max(0, cur + dir * unitStep);
            SetKeepMin(sc.Write(s), rid, next);
        }
    }

    /// <summary>An empty collect list stops mining/looting: record which control and profile did it.</summary>
    private static void LogIfEmpty(CargoScope sc, int s, CargoPolicy cp, string source)
    {
        if (cp.Collect is { Count: 0 })
        {
            Log.Info("Cargo collect selection emptied by " + source + " for " + "profile " + sc.Profile.Name);
        }
    }

    /// <summary>True when the policy collects at least one resource of the group (null Collect = all).</summary>
    private static bool AnySelected(List<string>? sel, ResourceRules[] all, ulong group)
    {
        if (sel == null)
        {
            return true;
        }
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            if ((group & (1UL << i)) != 0 && all[i].Icon != null && sel.Contains(all[i].ID.ToString()))
            {
                return true;
            }
        }
        return false;
    }

    private static int CountSelected(CargoScope sc, string rid)
    {
        int k = 0;
        for (int i = 0; i < sc.Count; i++)
        {
            List<string>? sel = sc.Read(i).Collect;
            if (sel == null || sel.Contains(rid))
            {
                k++;
            }
        }
        return k;
    }

    private void DrawGroup(QuickGui gui, CargoScope sc, ResourceRules[] all, ulong group, string labelKey, ulong warnGroup, string warnKey, float colW)
    {
        int total = 0, chosen = 0, n = sc.Count;
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            if ((group & (1UL << i)) != 0 && all[i].Icon != null)
            {
                total++;
                if (CountSelected(sc, all[i].ID.ToString()) == n)
                {
                    chosen++;
                }
            }
        }
        gui.Label(L.T(labelKey) + "  " + chosen + " / " + total, H24, LeftAlign);
        // Live consequence of an empty group: shown until the user selects something again (never auto-fixed).
        int none = 0;
        for (int s = 0; s < n && total > 0; s++)
        {
            if (!AnySelected(sc.Read(s).Collect, all, warnGroup))
            {
                none++;
            }
        }
        if (none > 0)
        {
            gui.Label("⚠ " + L.T(warnKey) + (n > 1 ? " (" + none + " / " + n + ")" : ""), AutoH, LeftAlign, Wrap, Warn);
        }
        var b2 = W((colW - Gap) / 2f);
        using (gui.LayoutRegion(LayoutAlgorithms.StretchLeftToRight, H40, Fixed))
        {
            if (gui.Button(L.T("MiningDrone/SelectAll"), b2, Shrink))
            {
                SetGroup(sc, all, group, true);
            }
            if (gui.Button(L.T("MiningDrone/SelectNone"), b2, Shrink))
            {
                SetGroup(sc, all, group, false);
            }
        }
        using (gui.LayoutRegion(_grid, AutoH, FixedWidth))
        {
            for (int i = 0; i < all.Length && i < 64; i++)
            {
                ResourceRules r = all[i];
                if ((group & (1UL << i)) == 0 || r.Icon == null)
                {
                    continue;
                }
                string rid = r.ID.ToString();
                int k = CountSelected(sc, rid);
                IQuickWidgetModifier<Widget> look = k == n ? Lit : (k == 0 ? Dim : Partial);
                IQuickWidgetModifier<Widget>? tip = k > 0 && k < n ? PartialTip(i, k, n, r) : _tips[i];
                if (gui.ImageButton("", r.Icon, IconSize, look, tip))
                {
                    bool turnOn = k < n; // all -> off; none/partial -> on for all
                    for (int s = 0; s < n; s++)
                    {
                        CargoPolicy cp = sc.Write(s);
                        SetOne(cp, all, rid, turnOn);
                        LogIfEmpty(sc, s, cp, "icon " + rid);
                    }
                }
            }
        }
    }

    private IQuickWidgetModifier<Widget> PartialTip(int i, int k, int n, ResourceRules r)
    {
        long key = ((long)i << 32) | ((long)k << 16) | (uint)n;
        if (!_partialTips.TryGetValue(key, out var m))
        {
            m = QuickWidgetModifiers.ToolTip(L.F("MiningDrone/PartialTip", r.NameKey.Text, k, n));
            _partialTips[key] = m;
        }
        return m;
    }

    private static void SetGroup(CargoScope sc, ResourceRules[] all, ulong group, bool on)
    {
        for (int s = 0; s < sc.Count; s++)
        {
            CargoPolicy cp = sc.Write(s);
            for (int i = 0; i < all.Length && i < 64; i++)
            {
                if ((group & (1UL << i)) != 0)
                {
                    SetOne(cp, all, all[i].ID.ToString(), on);
                }
            }
            LogIfEmpty(sc, s, cp, "group select-none");
        }
    }

    private static void SetOne(CargoPolicy cp, ResourceRules[] all, string rid, bool on)
    {
        if (cp.Collect == null)
        {
            if (on)
            {
                return; // already selected (default = all)
            }
            cp.Collect = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].IsStackable)
                {
                    cp.Collect.Add(all[i].ID.ToString());
                }
            }
        }
        cp.Collect.Remove(rid);
        if (on)
        {
            cp.Collect.Add(rid);
        }
    }
}
