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

    /// <summary>Look in your own pockets.</summary>
    Inventory,

    /// <summary>Show every hotspot in the room, while held.</summary>
    /// <remarks>
    /// A 1999 adventure game hides what can be clicked and expects the player to sweep the
    /// pointer across the furniture until something lights up. Held rather than toggled: the
    /// answer to "what is in this room" is wanted for a second and not for an evening.
    /// </remarks>
    ShowHotspots,

    /// <summary>Open the quest log.</summary>
    /// <remarks>
    /// A key of its own rather than a corner of another screen. Somebody who has lost the
    /// thread should reach it in one gesture, from anywhere, without first finding the thing
    /// that opens the thing.
    /// </remarks>
    Journal,

    /// <summary>Write the game to the quick-save slot.</summary>
    QuickSave,

    /// <summary>Put the quick-save slot back.</summary>
    QuickLoad,

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

    /// <summary>Look closely at the thing under the pointer.</summary>
    /// <remarks>
    /// A button of its own because looking closely is not doing something. Given to the left
    /// button it won every click — the close-up was offered for nearly every noun in the
    /// game, so it came out ahead of talking, opening and using, and a click meant to cross
    /// the room leaned in at a doorframe instead.
    /// </remarks>
    Middle,
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

    /// <summary>Step what is chosen down: a quieter volume, the previous setting.</summary>
    /// <remarks>
    /// The arrows and not A and D. Those are movement keys, and a menu that took them would
    /// walk the camera across the room behind it.
    /// </remarks>
    Left,

    /// <summary>The same, the other way.</summary>
    Right,

    /// <summary>Put the console away.</summary>
    Escape,

    /// <summary>Show the console, or put it away.</summary>
    /// <remarks>
    /// The key under Escape, which every game with a console has used for thirty years and
    /// which no other part of this one wants.
    /// </remarks>
    Console,

    /// <summary>Show the settings section before this one.</summary>
    /// <remarks>
    /// <para>
    /// The settings are one screen with a list of sections down the side, so there has to
    /// be a way to move between them that is not the pointer. Page Up and Page Down, which
    /// is what a list of pages has answered to since before this game was written, and the
    /// shoulder buttons on a pad, which is where every console game has put the same job.
    /// </para>
    /// <para>
    /// Deliberately not Left and Right: those step the value of the row the player is on,
    /// and a key that changed the volume on one row and the whole page on another is a key
    /// nobody can use.
    /// </para>
    /// </remarks>
    PreviousSection,

    /// <summary>Show the settings section after this one.</summary>
    NextSection,
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

    /// <summary>Whether a pointer button is down right now.</summary>
    /// <param name="button">Which button.</param>
    /// <returns>True for as long as it is held.</returns>
    /// <remarks>
    /// Apart from <see cref="WasClicked"/> because a hold is a different gesture from a
    /// click, and the interesting thing about it is how long it has lasted. What reads this
    /// is counting seconds; what reads a click is acting once.
    /// </remarks>
    bool IsHeld(PointerButton button);

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

    /// <summary>Which key and which pad button do which job.</summary>
    /// <remarks>
    /// Settable, because the whole point of it is that the player can change it. Replaced
    /// wholesale rather than edited in place: <see cref="InputBindings"/> is immutable, so
    /// nothing can be half-rebound while a frame is being read.
    /// </remarks>
    InputBindings Bindings { get; set; }

    /// <summary>Whether a gamepad is plugged in.</summary>
    /// <remarks>
    /// <para>
    /// Read by the settings screen, which says so rather than offering a page of pad
    /// bindings with no pad to press. It can change while the game is running — that is
    /// what a USB socket is — so it is asked every frame rather than at startup.
    /// </para>
    /// <para>
    /// <b>This and the three below answer for themselves.</b> A pad is a capability rather
    /// than a requirement, and "there is no pad, nothing was pressed and the sticks are
    /// centred" is the correct answer for every input source that has not got one — which
    /// includes every fake in the tests. <see cref="Bindings"/> deliberately has no default:
    /// an implementation that quietly swallowed the bindings would be one where every key
    /// the player rebound did nothing, and that is not a failure anybody would find.
    /// </para>
    /// </remarks>
    bool HasGamepad => false;

    /// <summary>Where the pad's sticks are pointing and how hard its triggers are pressed.</summary>
    GamepadSticks Sticks => GamepadSticks.Still;

    /// <summary>
    /// How fast a stick pushed all the way moves the pointer, in logical pixels a second.
    /// </summary>
    /// <remarks>
    /// <b>Nought is the switch as well as the speed.</b> A stick that moves the cursor no
    /// pixels a second is a stick that does not move the cursor, and a separate flag saying
    /// the same thing would be a second thing to keep in step with this one.
    /// </remarks>
    float PointerSpeed
    {
        get => 0f;
        set { }
    }

    /// <summary>
    /// The key pressed since the last poll, for a settings page that is listening for one.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="WasPressed(EditKey)"/> and from the actions because it is a
    /// different question: those ask "was this particular thing pressed", and rebinding asks
    /// "what was pressed". A page that had to ask the first question about a hundred keys to
    /// answer the second would be a page that could only bind the keys somebody had thought
    /// to list.
    /// </remarks>
    InputKey AnyKey => InputKey.None;

    /// <summary>The pad button pressed since the last poll, for the same reason.</summary>
    GamepadButton AnyButton => GamepadButton.None;

    /// <summary>
    /// Puts the pointer somewhere, without the mouse having moved.
    /// </summary>
    /// <param name="position">Where, in pixels from the top-left of the window.</param>
    /// <remarks>
    /// What lets a stick drive the cursor. The position is the game's from then on until
    /// the mouse itself moves, at which point the mouse takes it back — so a player with
    /// both a pad and a mouse on the desk can use either without a mode to switch between
    /// them, and the cursor never fights itself.
    /// </remarks>
    void MovePointer(Vector2 position)
    {
    }

    /// <summary>Clears the per-frame state. Called once a frame, after reading it.</summary>
    void EndFrame();

    /// <summary>
    /// Throws away input that has been gathered but not read.
    /// </summary>
    /// <remarks>
    /// The same clearing <see cref="EndFrame"/> does, under a name that says why rather
    /// than when — for the places where a frame is abandoned rather than finished. Leaving
    /// a room is one: the click that opened the door returns out of the room's loop before
    /// its frame has ended, so without this the click is still on the books when the next
    /// room reads them and is acted on a second time, somewhere it was never aimed.
    /// </remarks>
    void Forget();
}
