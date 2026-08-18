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

    /// <summary>Leave.</summary>
    Quit,
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

    /// <summary>Whether the pointer is being dragged with a button held.</summary>
    bool IsDragging { get; }

    /// <summary>Clears the per-frame state. Called once a frame, after reading it.</summary>
    void EndFrame();
}
