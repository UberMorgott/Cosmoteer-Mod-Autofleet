using System;
using System.Collections.Generic;
using Cosmoteer;
using Cosmoteer.Game;
using Cosmoteer.Game.SelectionStates;
using Cosmoteer.Gui;
using Cosmoteer.Simulation;
using Halfling;
using Halfling.Geometry;
using Halfling.Graphics;
using Halfling.Graphics.Text;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>
/// State markers above automated ships, drawn in SimOverlayRenderer.DrawingCustomOverlays with the game's own sprites
/// only (no new art). Drawn in SCREEN space (Matrix.ProjectionOrthographic, as BaseSelectionState.DrawText does), so
/// icons keep a fixed orientation whatever the ship or camera rotation; the move icon (cmd_move.png, a diagonal
/// up-right arrow) is turned 45° to point right. A cargo % label sits right of the icon while the ship travels,
/// mines, loots or flies to offload. No allocations: sprites are rules references, labels come from a 0..100 cache.
/// </summary>
internal static class Icons
{
    private static readonly string[] Pct = BuildPct();

    private static string[] BuildPct()
    {
        var a = new string[101];
        for (int i = 0; i <= 100; i++)
        {
            a[i] = i + "%";
        }
        return a;
    }

    /// <summary>The game sprite for an agent's state.</summary>
    public static ISprite? For(GameRoot game, SimRoot sim, ShipAgent c, out bool isMove)
    {
        isMove = false;
        var gui = game.Gui.Rules;
        if (c.NoTargets || c.State == MinerState.Stuck)
        {
            return GameApp.Rules.Crew.DistanceWarningSprite; // distance_warning.png (closest game "!" warning)
        }
        switch (c.State)
        {
            case MinerState.Mining:
                return gui.SalvageIcon; // cmd_salvage.png
            case MinerState.FindTarget:
            case MinerState.Approach:
                isMove = true;
                return gui.MoveIcon; // cmd_move.png
            case MinerState.CollectLoose:
                return sim.Overlays.Rules.TentativeNuggetCollectSprite; // overlay_nugget.png
            case MinerState.Evading:
                return c.Owner == ModuleType.Guard ? gui.AttackIcon : gui.StrafeIcon; // cmd_attack / cmd_strafe
            case MinerState.FlyToBase:
            case MinerState.Trading:
                return gui.Resources.ToolIcon; // hud_resources.png
            case MinerState.Offload:
                return gui.Resources.GrabToolIcon; // tool_grab.png
            case MinerState.Holding:
            case MinerState.Waiting:
                return c.Owner == ModuleType.AnchorLink ? gui.FormationIcon : gui.HaltIcon; // formation_wedge / cmd_halt
            case MinerState.Manual:
                return gui.DirectControlOnIcon; // toggle_direct_control_on.png
            case MinerState.Paused:
                return gui.PauseSprite; // speed_pause.png
            default:
                return null;
        }
    }

    private static bool ShowsPercent(MinerState s) =>
        s == MinerState.Approach || s == MinerState.Mining || s == MinerState.CollectLoose || s == MinerState.FlyToBase;

    /// <summary>Hover text for the icon under the cursor this frame (drawn once after all icons).</summary>
    private static ShipAgent? _hover;

    public static void DrawAll(GameRoot game, SimRoot sim, IReadOnlyList<ShipAgent> agents)
    {
        _hover = null;
        Vector2 mouse = (Vector2)App.Mouse.Location;
        using (App.Graphics.PushTransform(Matrix.ProjectionOrthographic(App.Graphics)))
        {
            for (int i = 0; i < agents.Count; i++)
            {
                Draw(game, sim, agents[i], mouse);
            }
        }
        if (_hover != null)
        {
            // Same helper vanilla uses for cursor hints (BaseSelectionState.DrawTextUnderCursor).
            BaseSelectionState.DrawTextUnderCursor(_hover.Ship.Metadata.PlainShipName + ": " + (_hover.NoTargets ? _hover.NoTargetsReason : _hover.StatusText));
        }
    }

    private static void Draw(GameRoot game, SimRoot sim, ShipAgent c, Vector2 mouse)
    {
        ISprite? sprite = For(game, sim, c, out bool isMove);
        if (sprite == null)
        {
            return;
        }
        var vp = sim.Viewport;
        Circle bc = c.Ship.WorldBoundingCircle;
        Vector2 center = vp.TransformScreenPointFromWorld(bc.Center);
        // Screen radius of the ship: distance between the projected center and a projected rim point.
        float rPix = vp.TransformScreenPointFromWorld(new Vector2(bc.Center.X + bc.Radius, bc.Center.Y)).DistanceTo(center);
        Vector2 size = game.Gui.TransformToPixels(SizeOf(game, sprite)) * 0.7f;
        var iconCenter = new Vector2(center.X, center.Y - rPix - size.Y * 0.75f);
        Rect rect = Rect.CenteredOn(iconCenter, size);
        DrawIcon(sprite, rect, isMove, Color.White);
        if (Store.Prefs.ShowCargoPercent && ShowsPercent(c.State))
        {
            BaseSelectionState.DrawText(new Vector2(rect.Right + 4f, iconCenter.Y), Pct[c.Percent], HAlignment.Left, VAlignment.Center, background: true);
            App.Graphics.SetToMaterial(null);
        }
        if (rect.Contains(mouse))
        {
            _hover = c;
        }
    }

    /// <summary>
    /// Minimap: registered as an IMinimapDoodad (SimDoodadsManager.AddMinimapDoodad, a render-only list that
    /// Minimap.cs:212 draws each frame), so each ship's state icon also appears on the minimap (no % label there).
    /// </summary>
    internal sealed class MinimapLayer : Cosmoteer.Simulation.Doodads.IMinimapDoodad
    {
        public void DrawOnMinimap(float pixelScale, Color colorScale, RotRect minimapWorldBounds)
        {
            var game = MinerManager.Game;
            var sim = game?.Sim;
            if (game == null || sim == null || !Store.Prefs.ShowStateIcons)
            {
                return;
            }
            var all = MinerManager.All;
            for (int i = 0; i < all.Count; i++)
            {
                ISprite? sprite = For(game, sim, all[i], out bool isMove);
                if (sprite == null)
                {
                    continue;
                }
                Vector2 size = SizeOf(game, sprite) * pixelScale * 0.6f; // pixelScale = world units per minimap UI pixel
                DrawIcon(sprite, Rect.CenteredOn(all[i].Ship.WorldCenter, size), isMove, colorScale);
            }
        }
    }

    /// <summary>The nugget overlay is a world-sized (1.5) sprite; show it at the size of a GUI command icon.</summary>
    private static Vector2 SizeOf(GameRoot game, ISprite sprite) =>
        sprite.Shader == PivotShader ? game.Gui.Rules.SalvageIcon.Size : sprite.Size;

    private static Shader? PivotShader => GameApp.Rules.Simulation.SimGui.TentativeNuggetCollectSprite.Shader;

    /// <summary>
    /// Draws one state icon. A shader's vertex input must match the primitive or D3D11 CreateInputLayout throws
    /// E_INVALIDARG (crash 2026-10-27): pivot.shader (nugget overlay) needs PivotQuad + _pivotRotate/_pivotScale, as
    /// vanilla BatchedIconRenderer.Draw does; default/crew_warning shaders take Quad. Any other shader is skipped.
    /// </summary>
    private static void DrawIcon(ISprite sprite, Rect rect, bool isMove, Color colorScale)
    {
        var gfx = App.Graphics;
        Shader? sh = sprite.Shader;
        if (sh == PivotShader)
        {
            Color c = sprite.VertexColor;
            c.A = 1f; // rules alpha is tuned for a faint world overlay
            gfx.SetToMaterial(sprite);
            gfx.SetFloat(ShaderConstantIDs.PivotRotate, 0f);
            gfx.SetFloat(ShaderConstantIDs.PivotScale, 1f);
            gfx.Draw(new Cosmoteer.Simulation.Overlays.PivotQuad(rect, rect.Center, c * colorScale, sprite.UVRect));
            return;
        }
        if (sh != null && sh != gfx.DefaultShader && sh != GameApp.Rules.Crew.DistanceWarningSprite.Shader)
        {
            return; // unknown vertex input: never risk a Quad on it
        }
        gfx.SetToMaterial(sprite);
        gfx.Draw(isMove
            ? new Quad(rect, (float)(Math.PI / 4.0), sprite.VertexColor * colorScale, sprite.UVRect)
            : new Quad(rect, sprite.VertexColor * colorScale, sprite.UVRect));
    }

    public static readonly MinimapLayer Minimap = new();
}
