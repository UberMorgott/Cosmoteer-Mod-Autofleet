using System;
using Cosmoteer;
using Cosmoteer.Game;
using Cosmoteer.Game.SelectionStates;
using Cosmoteer.Input;
using Cosmoteer.Simulation.Overlays;
using Halfling;
using Halfling.Geometry;
using Halfling.Input;

namespace MiningDrone;

/// <summary>
/// Interactive area placement, modelled on vanilla PlaceCustomMarkerState: a selection state registered in
/// GameGui.SelectionStates and driven by GameGui.StateSelectionManager. The circle follows the cursor (camera zoom
/// keeps working), +/- (main row or numpad) or Ctrl+wheel resizes it, left click places center + radius for every
/// checked ship of the Autofleet window (hidden during the pick, shown again when it ends), right click / Esc cancels.
/// </summary>
internal sealed class AreaPickState : BaseSelectionState
{
    public const float MinRadius = 250f;
    public const float MaxRadius = 20000f;

    public AreaPickState(GameRoot game)
        : base(game)
    {
        GateCameraKeys();
    }

    private static readonly InputButton NotPicking = new DelegatedInputButton(App.Input, static () => !Ui.Picking, static () => !Ui.Picking);

    /// <summary>Our +/- are vanilla View.ZoomIn/Out (Ctrl: ZoomMax/Min) and nothing consumes input; ViewController polls
    /// them before our Input runs, so gate them off while picking. Not DisableCameraZoom: GetTotalZoom then returns 0
    /// and ApplyZoom snaps the camera to max zoom. Gate is inert when not picking, so it is installed once, never removed.</summary>
    private static void GateCameraKeys()
    {
        foreach (CustomInput ci in new[] { Inputs.View.ZoomIn, Inputs.View.ZoomOut, Inputs.View.ZoomMax, Inputs.View.ZoomMin })
        {
            ci.Condition ??= NotPicking;
        }
    }

    /// <summary>Ctrl+wheel: camera already applied this frame's wheel step (ViewController.ZoomByMouseWheel runs in
    /// Sim.Input, before the selection state's Input), so undo that step: exact inverse while the clamp did not bite,
    /// else the target as of last frame's draw (same frame of reference unless it changed this very frame).</summary>
    private void UndoWheelZoom(float v)
    {
        if (!Settings.EnableMouseWheelZoom)
        {
            return;
        }
        var vc = Game.Sim.Camera.ViewController;
        float t = vc.FRTargetZoomScale;
        vc.FRTargetZoomScale = t > vc.FRMinZoomScale && t < vc.FRMaxZoomScale
            ? t / Mathx.Pow(Settings.MouseWheelZoomSpeed.Squared(), -Mathx.Sign(v))
            : _zoomBeforeInput;
    }

    private float _zoomBeforeInput;

    private void SnapZoom() => _zoomBeforeInput = Game.Sim.Camera.ViewController.FRTargetZoomScale;

    public override void OnSelected()
    {
        base.OnSelected();
        SnapZoom();
    }

    public override void OnDeselected()
    {
        base.OnDeselected();
        Ui.OnPickEnded();
    }

    public static float Radius { get; set; } = 3000f;

    public static void Grow(float factor) => Radius = Math.Clamp(Radius * factor, MinRadius, MaxRadius);

    public override void DrawOverlay()
    {
        SnapZoom(); // after this frame's Update, before next frame's camera input
        if (Game.Gui.Reacted)
        {
            return;
        }
        var sim = Game.Sim;
        sim.Overlays.Rules.TentativeSalvageCommandCircle.Draw(new Circle(sim.WorldMouseLoc, Radius), sim.Camera.UniformScale, null);
        DrawTextUnderCursor(L.F("MiningDrone/PickAreaHint", Radius.ToString("0")), background: true);
    }

    public override void Input()
    {
        var kb = App.Keyboard;
        if (kb.HotkeyPressed(ViKey.PlusEquals, false) || kb.HotkeyPressed(ViKey.NumPadPlus, false))
        {
            Grow(1.15f);
        }
        if (kb.HotkeyPressed(ViKey.MinusUnderscore, false) || kb.HotkeyPressed(ViKey.NumPadMinus, false))
        {
            Grow(1f / 1.15f);
        }
        // Ctrl: no vanilla wheel+modifier binding; plain wheel stays camera zoom.
        float wheel = App.Mouse.WheelVelocity; // raw: radius ignores InvertMouseWheelZoom
        // Only when GameRoot.Input ran Sim.Input (sim camera took the wheel), not on/into the galaxy map.
        if (wheel != 0f && kb.HotkeyDown(ViKey.Control, false) && !Game.Gui.Reacted && Game.MapDisplayFactor <= 0f)
        {
            UndoWheelZoom(Inputs.View.WheelZoom); // the value the camera used (inversion included)
            Grow(wheel > 0f ? 1.15f : 1f / 1.15f);
        }
        if (Inputs.General.UIAltClick.WasPressed || Inputs.General.Cancel.WasPressed)
        {
            Finish();
            return;
        }
        if (Inputs.General.UIClick.WasPressed && !Game.Gui.Reacted)
        {
            Vector2 c = Game.Sim.WorldMouseLoc;
            int n = 0;
            foreach (string id in Ui.WindowTargets ?? Array.Empty<string>())
            {
                n += Place(id, c) ? 1 : 0;
            }
            Log.Info($"Area picked at {c.X:0},{c.Y:0} r={Radius:0} for {n} ship(s)");
            Finish();
        }
    }

    /// <summary>Placement only: whether a ship works in a circle is its profile's logic, never set from here.</summary>
    private static bool Place(string id, Vector2 c)
    {
        if (!Autofleet.Store.Assignments.TryGetValue(id, out var a) || !Autofleet.Store.WorksInZone(a))
        {
            return false;
        }
        a.Bindings.Zone.X = c.X;
        a.Bindings.Zone.Y = c.Y;
        a.Bindings.Zone.R = Radius;
        Autofleet.Store.Touch(a);
        return true;
    }

    private void Finish() => Game.Gui.StateSelectionManager.ClearSelected();
}
