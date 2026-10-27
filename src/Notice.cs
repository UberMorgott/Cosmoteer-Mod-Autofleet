using System;
using Cosmoteer.Game;
using Cosmoteer.Game.Gui;
using Cosmoteer.Ships;
using Halfling.Graphics;

namespace MiningDrone;

/// <summary>
/// Player-facing notices for the "no targets" state: a one-time message in the game's chat/system log
/// (IChatBoxProvider.ChatBox.DisplaySystemMessage, local only, not sent to other players) ; the world/minimap marker is the
/// state icon (Icons). Floating text was dropped: GameGui.CreateFloatingText projects to the screen once, so it drifts.
/// </summary>
internal static class Notice
{
    private static readonly Color Warn = new Color(1f, 0.8f, 0.2f, 1f);

    /// <summary>Local system message (UI feedback such as a rejected anchor).</summary>
    public static void Info(string msg)
    {
        try
        {
            (MinerManager.Game?.NetManager as IChatBoxProvider)?.ChatBox?.DisplaySystemMessage(msg, Warn);
        }
        catch (Exception ex)
        {
            Log.Error("notice", ex);
        }
    }

    public static void NoTargets(GameRoot game, Ship ship, string reason)
    {
        try
        {
            string msg = ship.Metadata.PlainShipName + ": " + reason;
            // Chat only for ships the player has selected (local selection, PlayerInputHandler.IsShipSelected); the
            // "!" icon and hover text still flag the others.
            bool selected = game.Sim?.PlayerInput?.IsShipSelected(ship) == true;
            if (selected)
            {
                (game.NetManager as IChatBoxProvider)?.ChatBox?.DisplaySystemMessage(msg, Warn);
            }
            Log.Info("notice" + (selected ? "" : " (not selected, chat skipped)") + ": " + msg);
        }
        catch (Exception ex)
        {
            Log.Error("notice", ex);
        }
    }
}
