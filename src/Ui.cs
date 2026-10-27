using System;
using System.Collections.Generic;
using Cosmoteer.Game;
using Cosmoteer.Game.Gui;
using Cosmoteer.Gui;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Halfling.Developer.Editing;
using Halfling.Geometry;
using Halfling.Gui;
using Halfling.Gui.Components.Text;
using Halfling.Gui.Quick;
using Halfling.Gui.Quick.WidgetModifiers;

namespace MiningDrone;

/// <summary>Menu-bar button + floating Quick-GUI window (same toolkit as the game's own Camera Helper window).</summary>
internal static class Ui
{
    private static GameRoot? _game;
    private static GameGui? _attachedGui;
    private static ImageButton? _button;
    private static MinerWindow? _window;
    private static Widget? _pickKey;
    private static AreaPickState? _pickState;

    /// <summary>Starts interactive area placement (only entry point: the Назначения tab button, WindowAssign).</summary>
    public static void StartAreaPick()
    {
        if (_attachedGui != null && _pickState != null && _window != null)
        {
            _attachedGui.ActivateSelectionState(_pickState);
            // The full-screen window would cover the world and block the pick: hide it until the pick ends.
            _hiddenForPick = true;
            _window.SelfActive = false;
        }
    }

    private static bool _hiddenForPick;

    /// <summary>AreaPickState.OnDeselected (placed or cancelled): bring the window back.</summary>
    public static void OnPickEnded()
    {
        if (_hiddenForPick && _window != null)
        {
            _hiddenForPick = false;
            _window.SelfActive = true;
        }
    }

    /// <summary>The window's checked ships (area picker targets), or null when the window is closed.</summary>
    public static System.Collections.Generic.IEnumerable<string>? WindowTargets => _window != null && (_window.SelfActive || _hiddenForPick) ? _window.TargetIds : null;

    public static bool Picking => _attachedGui != null && _pickState != null && ReferenceEquals(_attachedGui.GetSelectionState(), _pickState);

    public static void Attach(GameRoot game)
    {
        _game = game;
        _attachedGui = null;
    }

    public static void Detach()
    {
        try
        {
            if (_attachedGui != null)
            {
                if (_window != null)
                {
                    _attachedGui.FloatingWindows.Children.Remove(_window);
                }
                if (_button != null)
                {
                    _attachedGui.MenuBar.Children.Remove(_button);
                }
                if (_pickKey != null)
                {
                    _attachedGui.SelectionStates.Remove(_pickKey);
                }
            }
        }
        catch
        {
        }
        _window = null;
        _hiddenForPick = false;
        _button = null;
        _pickKey = null;
        _pickState = null;
        _attachedGui = null;
        _game = null;
    }

    public static void Toggle()
    {
        if (!EnsureBuilt())
        {
            return;
        }
        if (_hiddenForPick)
        {
            _attachedGui!.StateSelectionManager.ClearSelected(); // cancels the pick, OnPickEnded reopens
            return;
        }
        _window!.SelfActive = !_window.SelfActive;
        if (_window.SelfActive)
        {
            _window.ImportGameSelection();
            World.PrimeAsteroidMasks(_game!.Sim);
        }
    }

    /// <summary>Every close path (toggle, the title-bar ✕ via WindowBox.OnCloseClicked -> SelfActive=false, parent
    /// hidden) raises Widget.Deactivated, which calls this.</summary>
    public static void OnWindowClosed()
    {
        if (_hiddenForPick)
        {
            return; // hidden for the area pick, not closed
        }
        MinerManager.PreviewArea = null;
        if (Picking)
        {
            // The pick targets the window's checked ships; once it closes they are no longer shown, so cancel.
            _attachedGui!.StateSelectionManager.ClearSelected();
        }
    }

    /// <summary>Lazily builds the widgets once the GameGui exists (it may not on the first GameRoot frame).</summary>
    public static bool EnsureBuilt()
    {
        GameRoot? game = _game;
        GameGui? gui = game?.Gui;
        if (game == null || gui == null)
        {
            return false;
        }
        if (ReferenceEquals(gui, _attachedGui) && _window != null)
        {
            return true;
        }
        _attachedGui = gui;

        _button = new ImageButton(gui.Rules.FormationIcon);
        _button.CopySettingsFrom(WidgetRules.Instance.FlatButton);
        _button.Size = new Vector2(40f, 40f);
        _button.ToolTips.TextProvider = new SimpleTextProvider(L.T("MiningDrone/ButtonTip"), _button);
        _button.Clicked += (_, _) => Toggle();
        gui.MenuBar.AddChild(_button);

        // Area picker: a vanilla-style selection state. ActivateSelectionState needs a widget key; SelectionManager
        // never checks it is shown, so a parentless one keeps the pick off the menu bar (window-only entry).
        _pickState = new AreaPickState(game);
        _pickKey = new Widget();
        gui.SelectionStates.Add(_pickKey, _pickState);

        _window = new MinerWindow(game, gui);
        _window.SelfActive = false;
        // Full screen: fill-anchored with zero offsets follows resolution/UI-scale changes (Widget.AnchorRect);
        // no dragging/resizing (CrewToolbox.cs:550, FtlJumpGui.cs:200). Title bar ✕ and Ctrl+Alt+A still close it.
        _window.AnchorRect = AnchorPresets.Fill;
        _window.Rect = default;
        _window.AutoSize.AutoWidthMode = AutoSizeMode.Disable;
        _window.AutoSize.AutoHeightMode = AutoSizeMode.Disable;
        _window.DragController.SelfActive = false;
        _window.ResizeController.SelfActive = false;
        gui.FloatingWindows.AddChild(_window);
        return true;
    }

    public static void Frame()
    {
        if (_game != null && !ReferenceEquals(_game.Gui, _attachedGui))
        {
            EnsureBuilt();
        }
    }
}

