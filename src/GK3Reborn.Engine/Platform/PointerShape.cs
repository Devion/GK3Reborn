namespace GK3Reborn.Platform;

/// <summary>
/// What the pointer is drawn as, which is what a click on the thing under it would do.
/// </summary>
public enum PointerShape
{
    /// <summary>The arrow. Nothing under the pointer answers to a click.</summary>
    Default,

    /// <summary>A hand on a doorknob: a click leaves the room.</summary>
    Exit,

    /// <summary>A magnifying glass: a click looks at it.</summary>
    Look,

    /// <summary>A pointing hand: a click does something to it.</summary>
    Interact,

    /// <summary>A speech bubble: a click talks to them.</summary>
    Talk,
}
