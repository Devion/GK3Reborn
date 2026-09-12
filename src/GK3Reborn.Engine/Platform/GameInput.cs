using System.Numerics;

namespace GK3Reborn.Platform;

/// <summary>The actions the free camera responds to.</summary>
public enum CameraAction
{
    /// <summary>Move towards where the camera looks.</summary>
    Forward,

    /// <summary>Move away from where the camera looks.</summary>
    Back,

    /// <summary>Move to the camera's left.</summary>
    Left,

    /// <summary>Move to the camera's right.</summary>
    Right,

    /// <summary>Move along the up axis.</summary>
    Up,

    /// <summary>Move against the up axis.</summary>
    Down,

    /// <summary>Move faster while held.</summary>
    Fast,

    /// <summary>Return to the scene's own camera.</summary>
    Reset,

    /// <summary>Step to the scene's next camera.</summary>
    NextCamera,

    /// <summary>Step through the ray-tracing quality levels.</summary>
    CycleRayTracing,

    /// <summary>Look in your own pockets.</summary>
    Inventory,

    /// <summary>Show every hotspot in the room, while held.</summary>
    ShowHotspots,

    /// <summary>Open the quest log.</summary>
    Journal,

    /// <summary>Write the game to the quick-save slot.</summary>
    QuickSave,

    /// <summary>Put the quick-save slot back.</summary>
    QuickLoad,

    /// <summary>Leave.</summary>
    Quit,

    /// <summary>Give the pointer back while held, so something off to one side can be clicked without turning to face it.</summary>
    FreeCursor,
}

/// <summary>The pointer buttons the game reads.</summary>
public enum PointerButton
{
    /// <summary>Do the thing under the pointer.</summary>
    Primary,

    /// <summary>Ask what the thing under the pointer answers to.</summary>
    Secondary,

    /// <summary>Look closely at the thing under the pointer.</summary>
    Middle,
}

/// <summary>What the player is doing right now.</summary>
public enum EditKey
{
    /// <summary>Delete the character before the caret.</summary>
    Backspace,

    /// <summary>Run what has been typed.</summary>
    Enter,

    /// <summary>Take the chosen completion.</summary>
    Tab,

    /// <summary>Move the choice, or recall an earlier line.</summary>
    Up,

    /// <summary>The same, the other way.</summary>
    Down,

    /// <summary>Step what is chosen down: a quieter volume, the previous setting.</summary>
    Left,

    /// <summary>The same, the other way.</summary>
    Right,

    /// <summary>Put the console away.</summary>
    Escape,

    /// <summary>Show the console, or put it away.</summary>
    Console,

    /// <summary>Show the settings section before this one.</summary>
    PreviousSection,

    /// <summary>Show the settings section after this one.</summary>
    NextSection,
}

public interface IGameInput
{
    /// <summary>Whether an action is currently held.</summary>
    /// <returns>True while held.</returns>
    /// <param name="action">The action.</param>
    bool IsHeld(CameraAction action);

    /// <summary>Whether an action was triggered since the last poll.</summary>
    /// <returns>True once per press.</returns>
    /// <param name="action">The action.</param>
    bool WasPressed(CameraAction action);

    /// <summary>How far the pointer moved since the last poll, in pixels.</summary>
    Vector2 PointerDelta { get; }

    /// <summary>Where the pointer is, in pixels from the top-left of the window.</summary>
    Vector2 PointerPosition { get; }

    /// <summary>How far the wheel turned since the last poll, in notches.</summary>
    int ScrollDelta { get; }

    /// <summary>Whether the pointer was clicked since the last poll.</summary>
    /// <returns>True once per press.</returns>
    /// <param name="button">Which button.</param>
    bool WasClicked(PointerButton button);

    /// <summary>Whether the click just reported was the second of a pair.</summary>
    /// <returns>True on the second click of a double-click, alongside .</returns>
    /// <param name="button">Which button.</param>
    bool WasDoubleClicked(PointerButton button);

    /// <summary>Whether the pointer is being dragged with a button held.</summary>
    bool IsDragging { get; }

    /// <summary>Whether a pointer button is down right now.</summary>
    /// <returns>True for as long as it is held.</returns>
    /// <param name="button">Which button.</param>
    bool IsHeld(PointerButton button);

    /// <summary>The printable characters typed since the last poll.</summary>
    string Typed { get; }

    /// <summary>Whether an editing key was pressed since the last poll.</summary>
    /// <returns>True once per press.</returns>
    /// <param name="key">Which one.</param>
    bool WasPressed(EditKey key);

    /// <summary>Which key and which pad button do which job.</summary>
    InputBindings Bindings { get; set; }

    /// <summary>Whether a gamepad is plugged in.</summary>
    bool HasGamepad => false;

    /// <summary>Where the pad's sticks are pointing and how hard its triggers are pressed.</summary>
    GamepadSticks Sticks => GamepadSticks.Still;

    /// <summary>How fast a stick pushed all the way moves the pointer, in logical pixels a second.</summary>
    float PointerSpeed
    {
        get => 0f;
        set { }
    }

    /// <summary>The key pressed since the last poll, for a settings page that is listening for one.</summary>
    InputKey AnyKey => InputKey.None;

    /// <summary>The pad button pressed since the last poll, for the same reason.</summary>
    GamepadButton AnyButton => GamepadButton.None;

    /// <summary>What the pointer is drawn as.</summary>
    PointerShape PointerShape
    {
        get => PointerShape.Default;
        set { }
    }

    /// <summary>How big it is drawn, as a multiple of its usual size.</summary>
    float PointerScale
    {
        get => 1f;
        set { }
    }

    /// <summary>Whether the pointer is held for looking about rather than for pointing at things.</summary>
    bool PointerLocked
    {
        get => false;
        set { }
    }

    /// <summary>Puts the pointer somewhere, without the mouse having moved.</summary>
    /// <param name="position">Where, in pixels from the top-left of the window.</param>
    void MovePointer(Vector2 position)
    {
    }

    /// <summary>Clears the per-frame state.</summary>
    void EndFrame();

    /// <summary>Throws away input that has been gathered but not read.</summary>
    void Forget();
}
