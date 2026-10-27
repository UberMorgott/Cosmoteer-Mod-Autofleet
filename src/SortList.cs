using System;
using System.Collections.Generic;
using Cosmoteer.Gui;
using Cosmoteer.Input;
using Halfling;
using Halfling.Geometry;
using Halfling.Graphics;
using Halfling.Gui;
using Halfling.Gui.Components.Hierarchy;
using Halfling.Gui.Quick;
using Halfling.Gui.Quick.WidgetModifiers;
using Halfling.Input;

namespace MiningDrone;

/// <summary>
/// Drag-reorder of one vertical quick-GUI list, with animation. Each block (a row or a whole section) carries a cached
/// per-key capture modifier (unique per key, so QuickWidgetManager keeps the widget bound to its key) and a grab
/// widget (a label: buttons still click, since a click needs Pressed->Highlighted on the button itself). Everything
/// here is render/input-state only (Widget.RenderSettings.LocationOffset/ColorScale, SelfInputActive, a ghost on the
/// root's drop-target layer like WidgetDragAndDropSource.DragWidget); layout and model change only on drop, through
/// the caller. Blocks drawn away from their layout rect (settling) take no input, so a click never hits a hidden row.
/// </summary>
internal sealed class SortList
{
    private const float Threshold = 8f; // WidgetDragAndDropSource.DragThreshold
    private const float TauMs = 60f;
    private const float EdgePx = 48f, ScrollPxPerMs = 0.9f;
    private const long SettleMs = 400;

    private readonly Dictionary<string, IQuickWidgetModifier<Widget>> _blockMods = new(), _grabMods = new();
    private readonly Dictionary<string, Widget> _blocks = new(), _grabs = new();
    private readonly List<string> _keys = new(), _titles = new();
    private readonly List<float> _tops = new(), _heights = new();
    private Dictionary<string, float> _shown = new(), _next = new();

    private string? _press, _drag, _ghostKey;
    private Vector2 _pressAt, _grabAt, _ghostAt;
    private Color _dragColor;
    private Widget? _dragWidget;
    private Button? _ghost;
    private long _last, _animUntil;
    private bool _ended;

    /// <summary>Starts a declaration pass (QuickGui runs the callback twice per frame: Declaration and Output).</summary>
    public void Begin()
    {
        _keys.Clear();
        _titles.Clear();
    }

    /// <summary>Registers a block (in display order) and returns the modifier for its outermost widget.</summary>
    public IQuickWidgetModifier<Widget> Block(string key, string title)
    {
        _keys.Add(key);
        _titles.Add(title);
        if (!_blockMods.TryGetValue(key, out var m))
        {
            m = new Mod<Widget>(w => _blocks[key] = w);
            _blockMods[key] = m;
        }
        return m;
    }

    /// <summary>Modifier for the widget a press must start on to drag <paramref name="key"/>.</summary>
    public IQuickWidgetModifier<Widget> Grab(string key)
    {
        if (!_grabMods.TryGetValue(key, out var m))
        {
            m = new Mod<Widget>(w => _grabs[key] = w);
            _grabMods[key] = m;
        }
        return m;
    }

    /// <summary>Window, once per Output pass after everything is declared: a list that was not drawn (tab, profile,
    /// folded section, empty tree) drops its gesture and ghost.</summary>
    public void Sweep()
    {
        if (!_ended)
        {
            Cancel(animate: false);
        }
        _ended = false;
    }

    /// <summary>
    /// Once per frame after the list is declared: drag input, animation, auto-scroll of <paramref name="scroll"/> while
    /// the cursor is near the <paramref name="viewport"/> edge. Returns the dropped key with its new neighbours (next,
    /// previous) on the release frame of a real move, else null.
    /// </summary>
    public (string Key, string? Before, string? After)? End(QuickGui gui, Rect? viewport, ref Vector2 scroll)
    {
        if (((IQuickGui)gui).Phase != QuickGuiPhase.Output)
        {
            return null;
        }
        _ended = true;
        long now = Environment.TickCount64;
        float dt = Math.Clamp(now - _last, 0, 100);
        _last = now;
        _tops.Clear();
        _heights.Clear();
        Widget? any = null;
        float listTop = float.MaxValue;
        foreach (string k in _keys)
        {
            if (!_blocks.TryGetValue(k, out Widget? w) || w.Parent == null)
            {
                Cancel(animate: false); // a block without layout yet (just added): nothing to measure this frame
                return null;
            }
            Rect r = w.ScreenRect;
            _tops.Add(r.Top);
            _heights.Add(r.Height);
            listTop = Math.Min(listTop, r.Top);
            any = w;
        }
        IGuiRoot? root = any?.Root;
        if (root == null)
        {
            Cancel(animate: false);
            return null;
        }
        for (int i = 0; i < _tops.Count; i++)
        {
            _tops[i] -= listTop;
        }
        Vector2 mouse = root.UIMouseLocation;
        IInputButton btn = Widget.GlobalDefaultInteractionButton;
        (string, string?, string?)? result = null;
        int d = _drag == null ? -1 : _keys.IndexOf(_drag);

        if (_drag == null)
        {
            if (_press == null && btn.WasPressed && (viewport == null || viewport.Value.Contains(mouse)))
            {
                foreach (string k in _keys)
                {
                    // the grab under the cursor that took the hover (not clipped away, not covered)
                    if (_grabs.TryGetValue(k, out Widget? g) && g.Parent != null && g.IsInputActive && g.IsMouseOver)
                    {
                        _press = k;
                        _pressAt = mouse;
                    }
                }
            }
            else if (_press != null && !btn.IsDown)
            {
                _press = null;
            }
            else if (_press != null && (mouse - _pressAt).Length >= Threshold && _keys.IndexOf(_press) is int pi and >= 0)
            {
                StartDrag(root, pi, mouse, listTop);
                d = pi;
            }
        }
        else if (d < 0 || !App.Window.IsFocused || Inputs.General.Cancel.WasPressed || Inputs.General.UIAltClick.WasPressed)
        {
            Cancel(animate: d >= 0); // rows and ghost fly back; focus loss never drops
            d = -1;
        }

        int t = -1;
        if (d >= 0)
        {
            float mid = mouse.Y - _grabAt.Y - listTop + _heights[d] / 2f;
            t = SortMath.InsertIndex(_tops, _heights, d, mid);
            _ghostAt = mouse - _grabAt;
            if (!btn.IsDown)
            {
                if (t != d)
                {
                    var (before, after) = SortMath.Neighbours(_keys, d, t);
                    result = (_keys[d], before, after);
                }
                Cancel(animate: true); // ends the drag; the caller's model edit relayouts, rows slide from where they are shown
                d = -1;
            }
            else if (viewport is Rect vp)
            {
                float dy = mouse.Y < vp.Top + EdgePx ? -1f : mouse.Y > vp.Bottom - EdgePx ? 1f : 0f;
                scroll.Y = Math.Max(0f, scroll.Y + dy * ScrollPxPerMs * dt); // the ScrollBar clamps the far end
            }
        }

        // Shown tops relative to the list top (scrolling moves all alike, so it never animates).
        float pad = _tops.Count > 1 ? Math.Max(0f, _tops[1] - _tops[0] - _heights[0]) : 0f;
        bool animate = d >= 0 || now < _animUntil;
        _next.Clear();
        for (int i = 0; i < _keys.Count; i++)
        {
            float target = d >= 0 ? SortMath.TargetTop(_tops, _heights, d, t, i, pad) : _tops[i];
            float shown = animate && _shown.TryGetValue(_keys[i], out float s) ? SortMath.Approach(s, target, dt, TauMs) : target;
            _next[_keys[i]] = shown;
            Widget b = _blocks[_keys[i]];
            b.RenderSettings.LocationOffset = new Vector2(0f, shown - _tops[i]);
            b.SelfInputActive = d >= 0 || shown == _tops[i]; // displaced while settling: inert until back on its rect
        }
        (_shown, _next) = (_next, _shown);
        FlyGhost(root, d >= 0, listTop, dt, now);
        return result;
    }

    private void StartDrag(IGuiRoot root, int i, Vector2 mouse, float listTop)
    {
        RemoveGhost();
        _drag = _keys[i];
        _press = null;
        Widget block = _blocks[_drag];
        _grabAt = mouse - new Vector2(block.ScreenRect.Left, block.ScreenRect.Top);
        _ghostAt = mouse - _grabAt;
        _dragWidget = block;
        _dragColor = block.RenderSettings.ColorScale;
        block.RenderSettings.ColorScale = new Color(1f, 1f, 1f, 0.3f); // faint placeholder in the slot it would take
        var g = new Button();
        g.CopySettingsFrom(WidgetRules.Instance.FlatButton);
        g.Text = _titles[i];
        g.InputBlockMode = InputBlockMode.Never;
        g.Size = new Vector2(block.ScreenRect.Width, Math.Min(block.ScreenRect.Height, 40f));
        g.RenderSettings.ColorScale = new Color(0.55f, 1f, 0.55f, 0.95f);
        root.AddDropTarget(g); // top layer, like WidgetDragAndDropSource.DragWidget
        _ghost = g;
        _ghostKey = _drag;
    }

    /// <summary>The ghost follows the cursor while dragging, then flies onto its block's drawn position and goes.</summary>
    private void FlyGhost(IGuiRoot root, bool dragging, float listTop, float dt, long now)
    {
        if (_ghost == null)
        {
            return;
        }
        if (!dragging)
        {
            if (_ghostKey == null || !_shown.TryGetValue(_ghostKey, out float top) || !_blocks.TryGetValue(_ghostKey, out Widget? b) || now >= _animUntil)
            {
                RemoveGhost();
                return;
            }
            var target = new Vector2(b.ScreenRect.Left, listTop + top);
            _ghostAt = new Vector2(SortMath.Approach(_ghostAt.X, target.X, dt, TauMs), SortMath.Approach(_ghostAt.Y, target.Y, dt, TauMs));
            if (_ghostAt.Equals(in target))
            {
                RemoveGhost();
                return;
            }
        }
        Rect scr = root.RectProvider.ScreenRect;
        var loc = new Vector2(Mathx.InverseLerp(scr.Left, scr.Right, _ghostAt.X), Mathx.InverseLerp(scr.Top, scr.Bottom, _ghostAt.Y));
        _ghost.AnchorRect = new Rect(in loc, in Vector2.Zero);
        _ghost.Location = Vector2.Zero;
    }

    private void RemoveGhost()
    {
        if (_ghost != null)
        {
            _ghost.Root?.RemoveDropTarget(_ghost);
            _ghost = null;
        }
        _ghostKey = null;
        if (_dragWidget != null)
        {
            _dragWidget.RenderSettings.ColorScale = _dragColor;
            _dragWidget = null;
        }
    }

    /// <summary>Ends any press/drag without a drop. Animated: rows and ghost fly back over the settle time; else
    /// everything snaps and the ghost goes now (window closed, list gone).</summary>
    public void Cancel(bool animate = false)
    {
        if (_drag != null && animate)
        {
            _animUntil = Environment.TickCount64 + SettleMs;
        }
        _press = null;
        _drag = null;
        if (!animate)
        {
            _animUntil = 0;
            RemoveGhost();
            foreach (Widget b in _blocks.Values)
            {
                b.RenderSettings.LocationOffset = Vector2.Zero;
                b.SelfInputActive = true;
            }
        }
    }
}
