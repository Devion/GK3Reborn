using System.Numerics;

namespace GK3Reborn.Platform;

/// <summary>The actions the free camera responds to.</summary>
/// <remarks>
/// Named for what they do rather than for the keys that trigger them, so the binding lives
/// in one place and the camera never mentions a keyboard.
/// </remarks>
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

    /// <summary>Leave.</summary>
    Quit,
}

/// <summary>The pointer buttons the game reads.</summary>
public enum PointerButton
{
    /// <summary>Do the thing under the pointer.</summary>
    Primary,

    /// <summary>Ask what the thing under the pointer answers to.</summary>
    Secondary,
}

/// <summary>What the player is doing right now.</summary>
/// <summary>A key that edits a line of text rather than adding to it.</summary>
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

    /// <summary>Put the console away.</summary>
    Escape,

    /// <summary>Show the console, or put it away.</summary>
    /// <remarks>
    /// The key under Escape, which every game with a console has used for thirty years and
    /// which no other part of this one wants.
    /// </remarks>
    Console,
}

public interface IGameInput
{
    /// <summary>Whether an action is currently held.</summary>
    /// <param name="action">The action.</param>
    /// <returns>True while held.</returns>
    bool IsHeld(CameraAction action);

    /// <summary>Whether an action was triggered since the last poll.</summary>
    /// <param name="action">The action.</param>
    /// <returns>True once per press.</returns>
    bool WasPressed(CameraAction action);

    /// <summary>How far the pointer moved since the last poll, in pixels.</summary>
    Vector2 PointerDelta { get; }

    /// <summary>Where the pointer is, in pixels from the top-left of the window.</summary>
    /// <remarks>
    /// Absolute rather than relative, because pointing at a thing in the room is a
    /// different question from looking around: a click has to become a ray, and a ray needs
    /// a position on the screen rather than how far the mouse moved to get there.
    /// </remarks>
    Vector2 PointerPosition { get; }

    /// <summary>How far the wheel turned since the last poll, in notches.</summary>
    /// <remarks>
    /// Positive is away from the player. Whole notches rather than pixels, because what
    /// reads it is choosing between list items rather than scrolling a surface.
    /// </remarks>
    int ScrollDelta { get; }

    /// <summary>Whether the pointer was clicked since the last poll.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>True once per press.</returns>
    /// <remarks>
    /// A click, not a hold: the interesting event is the transition, and reading a held
    /// button in a frame loop turns one click into thirty actions.
    /// </remarks>
    bool WasClicked(PointerButton button);

    /// <summary>Whether the click just reported was the second of a pair.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>True on the second click of a double-click, alongside <see cref="WasClicked"/>.</returns>
    /// <remarks>
    /// <para>
    /// Reported <em>as well as</em> the click rather than instead of it, because the two
    /// mean the same thing and differ only in urgency: a double-click is "do that, and get
    /// on with it". Swallowing the first click to see whether a second arrives would put a
    /// delay on every single click in the game to serve the rarer case.
    /// </para>
    /// <para>
    /// Here rather than in the game because deciding it needs the clock, and reading the
    /// clock outside the platform layer is what ADR 0004 forbids.
    /// </para>
    /// </remarks>
    bool WasDoubleClicked(PointerButton button);

    /// <summary>Whether the pointer is being dragged with a button held.</summary>
    bool IsDragging { get; }

    /// <summary>The printable characters typed since the last poll.</summary>
    /// <remarks>
    /// Characters rather than keys, because what a console wants is what the player meant
    /// to write: the platform has already applied the keyboard layout, the shift state and
    /// any dead keys, and reconstructing that from key codes is how a console ends up
    /// unusable on every layout but the author's.
    /// </remarks>
    string Typed { get; }

    /// <summary>Whether an editing key was pressed since the last poll.</summary>
    /// <param name="key">Which one.</param>
    /// <returns>True once per press.</returns>
    /// <remarks>
    /// Apart from <see cref="Typed"/> because these are not characters. Backspace and
    /// Escape do arrive as control characters on some platforms and none on others, which
    /// is not something anything above the platform layer should have to know.
    /// </remarks>
    bool WasPressed(EditKey key);

    /// <summary>Clears the per-frame state. Called once a frame, after reading it.</summary>
    void EndFrame();
}
