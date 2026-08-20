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

    /// <summary>Whether the pointer was clicked since the last poll.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>True once per press.</returns>
    /// <remarks>
    /// A click, not a hold: the interesting event is the transition, and reading a held
    /// button in a frame loop turns one click into thirty actions.
    /// </remarks>
    bool WasClicked(PointerButton button);

    /// <summary>Whether the pointer is being dragged with a button held.</summary>
    bool IsDragging { get; }

    /// <summary>Clears the per-frame state. Called once a frame, after reading it.</summary>
    void EndFrame();
}
