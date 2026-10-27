using System;
using System.Linq;
using Cosmoteer.Game;
using Halfling;
using Halfling.Application;
using Halfling.Input;

namespace MiningDrone;

/// <summary>Entry point called by Yet Another Mod Loader (public static void GameLoadInitializer()).</summary>
public static class Main
{
    private static bool _initialized;
    private static long _nextAliveCheck;

    public static void GameLoadInitializer()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        Log.Init();
        Autofleet.Store.Load();
        App.Director.FrameEnded += OnFrameEnded;
        Log.Info("MiningDrone initialized");
    }

    private static void OnFrameEnded(object? sender, EventArgs e)
    {
        try
        {
            long now = Environment.TickCount64;
            GameRoot? current = MinerManager.Game;

            // Attach whenever a GameRoot is on top; a *different* GameRoot instance means a new/loaded game.
            if (App.Director.PeekState() is GameRoot top)
            {
                if (!ReferenceEquals(top, current))
                {
                    if (current != null)
                    {
                        Detach();
                    }
                    Attach(top);
                    current = top;
                }
            }
            else if (current != null && now >= _nextAliveCheck)
            {
                // A dialog/menu state is on top: keep running unless our GameRoot left the state stack.
                _nextAliveCheck = now + 1000;
                if (!App.Director.States.Contains(current))
                {
                    Detach();
                    current = null;
                }
            }

            if (current == null)
            {
                return;
            }

            Ui.Frame();
            // Ctrl+Alt+A: unbound in vanilla Inputs.cs (Ctrl+Shift+A = SelectAllParts, Alt+A = ToggleAutoThrust).
            if (App.Keyboard.HotkeyPressed(ViKey.Control, ViKey.Alt, ViKey.A))
            {
                Ui.Toggle();
            }
            MinerManager.Frame(now);
        }
        catch (Exception ex)
        {
            Log.Error("frame", ex);
        }
    }

    private static void Attach(GameRoot game)
    {
        MinerManager.OnGameAttached(game);
        Ui.Attach(game);
        Log.Info("Attached to GameRoot");
    }

    private static void Detach()
    {
        Ui.Detach();
        MinerManager.OnGameDetached();
        Log.Info("Detached from GameRoot");
    }
}
