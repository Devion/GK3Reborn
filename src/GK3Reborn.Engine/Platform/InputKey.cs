namespace GK3Reborn.Platform;

/// <summary>
/// A key on the keyboard, named rather than numbered.
/// </summary>
public enum InputKey
{
    /// <summary>No key at all: an action nobody has bound.</summary>
    None = 0,

    Space,
    Apostrophe,
    Comma,
    Minus,
    Period,
    Slash,

    Number0,
    Number1,
    Number2,
    Number3,
    Number4,
    Number5,
    Number6,
    Number7,
    Number8,
    Number9,

    Semicolon,
    Equal,

    A,
    B,
    C,
    D,
    E,
    F,
    G,
    H,
    I,
    J,
    K,
    L,
    M,
    N,
    O,
    P,
    Q,
    R,
    S,
    T,
    U,
    V,
    W,
    X,
    Y,
    Z,

    LeftBracket,
    BackSlash,
    RightBracket,
    GraveAccent,

    Escape,
    Enter,
    Tab,
    Backspace,
    Insert,
    Delete,

    Right,
    Left,
    Down,
    Up,

    PageUp,
    PageDown,
    Home,
    End,

    CapsLock,
    ScrollLock,
    NumLock,
    PrintScreen,
    Pause,

    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,

    Keypad0,
    Keypad1,
    Keypad2,
    Keypad3,
    Keypad4,
    Keypad5,
    Keypad6,
    Keypad7,
    Keypad8,
    Keypad9,
    KeypadDecimal,
    KeypadDivide,
    KeypadMultiply,
    KeypadSubtract,
    KeypadAdd,
    KeypadEnter,

    ShiftLeft,
    ControlLeft,
    AltLeft,
    SuperLeft,
    ShiftRight,
    ControlRight,
    AltRight,
    SuperRight,
    Menu,
}

/// <summary>
/// A button or stick direction on a gamepad.
/// </summary>
public enum GamepadButton
{
    /// <summary>No button at all: an action nobody has bound.</summary>
    None = 0,

    /// <summary>A on an Xbox pad, Cross on a PlayStation one.</summary>
    South,

    /// <summary>B, or Circle.</summary>
    East,

    /// <summary>X, or Square.</summary>
    West,

    /// <summary>Y, or Triangle.</summary>
    North,

    /// <summary>The left shoulder button.</summary>
    LeftShoulder,

    /// <summary>The right shoulder button.</summary>
    RightShoulder,

    /// <summary>The left trigger, pressed past halfway.</summary>
    LeftTrigger,

    /// <summary>The right trigger, pressed past halfway.</summary>
    RightTrigger,

    /// <summary>Pressing the left stick in.</summary>
    LeftStick,

    /// <summary>Pressing the right stick in.</summary>
    RightStick,

    /// <summary>The small left-hand button: Back, Select, Share.</summary>
    Back,

    /// <summary>The small right-hand button: Start, Options.</summary>
    Start,

    /// <summary>The button in the middle with the maker's badge on it.</summary>
    Home,

    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
}

/// <summary>What to call a key, where a person has to read it.</summary>
public static class InputKeys
{
    /// <summary>Every key a player may bind something to, in the order a page lists them.</summary>
    public static readonly InputKey[] All =
        [.. Enum.GetValues<InputKey>().Where(k => k != InputKey.None)];

    /// <summary>
    /// What to print on a settings page for a key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>Its name, as somebody would say it out loud.</returns>
    public static string Describe(InputKey key) => key switch
    {
        InputKey.None => "—",

        InputKey.Apostrophe => "'",
        InputKey.Comma => ",",
        InputKey.Minus => "-",
        InputKey.Period => ".",
        InputKey.Slash => "/",
        InputKey.Semicolon => ";",
        InputKey.Equal => "=",
        InputKey.LeftBracket => "[",
        InputKey.BackSlash => "\\",
        InputKey.RightBracket => "]",
        InputKey.GraveAccent => "`",

        InputKey.Number0 => "0",
        InputKey.Number1 => "1",
        InputKey.Number2 => "2",
        InputKey.Number3 => "3",
        InputKey.Number4 => "4",
        InputKey.Number5 => "5",
        InputKey.Number6 => "6",
        InputKey.Number7 => "7",
        InputKey.Number8 => "8",
        InputKey.Number9 => "9",

        InputKey.ShiftLeft => "Left Shift",
        InputKey.ShiftRight => "Right Shift",
        InputKey.ControlLeft => "Left Ctrl",
        InputKey.ControlRight => "Right Ctrl",
        InputKey.AltLeft => "Left Alt",
        InputKey.AltRight => "Right Alt",
        InputKey.SuperLeft => "Left Meta",
        InputKey.SuperRight => "Right Meta",

        InputKey.PageUp => "Page Up",
        InputKey.PageDown => "Page Down",
        InputKey.PrintScreen => "Print Screen",
        InputKey.CapsLock => "Caps Lock",
        InputKey.ScrollLock => "Scroll Lock",
        InputKey.NumLock => "Num Lock",

        InputKey.KeypadDecimal => "Keypad .",
        InputKey.KeypadDivide => "Keypad /",
        InputKey.KeypadMultiply => "Keypad *",
        InputKey.KeypadSubtract => "Keypad -",
        InputKey.KeypadAdd => "Keypad +",
        InputKey.KeypadEnter => "Keypad Enter",

        _ when key.ToString().StartsWith("Keypad", StringComparison.Ordinal) =>
            "Keypad " + key.ToString()[6..],

        _ => key.ToString(),
    };

    /// <summary>Reads a key back from what was written in the settings file.</summary>
    /// <param name="text">The name, as <see cref="Enum.ToString()"/> gave it.</param>
    /// <returns>The key, or <see cref="InputKey.None"/> if it is not one.</returns>
    public static InputKey Parse(string? text) =>
        Enum.TryParse(text, ignoreCase: true, out InputKey key) ? key : InputKey.None;
}

/// <summary>What to call a gamepad button, where a person has to read it.</summary>
public static class GamepadButtons
{
    /// <summary>Every button a player may bind something to.</summary>
    public static readonly GamepadButton[] All =
        [.. Enum.GetValues<GamepadButton>().Where(b => b != GamepadButton.None)];

    /// <summary>What to print on a settings page for a button.</summary>
    /// <param name="button">The button.</param>
    /// <returns>Where it is on the pad.</returns>
    public static string Describe(GamepadButton button) => button switch
    {
        GamepadButton.None => "—",

        GamepadButton.South => "Bottom face",
        GamepadButton.East => "Right face",
        GamepadButton.West => "Left face",
        GamepadButton.North => "Top face",

        GamepadButton.LeftShoulder => "Left shoulder",
        GamepadButton.RightShoulder => "Right shoulder",
        GamepadButton.LeftTrigger => "Left trigger",
        GamepadButton.RightTrigger => "Right trigger",
        GamepadButton.LeftStick => "Left stick in",
        GamepadButton.RightStick => "Right stick in",

        GamepadButton.DPadUp => "D-pad up",
        GamepadButton.DPadDown => "D-pad down",
        GamepadButton.DPadLeft => "D-pad left",
        GamepadButton.DPadRight => "D-pad right",

        _ => button.ToString(),
    };

    /// <summary>Reads a button back from what was written in the settings file.</summary>
    /// <param name="text">The name.</param>
    /// <returns>The button, or <see cref="GamepadButton.None"/> if it is not one.</returns>
    public static GamepadButton Parse(string? text) =>
        Enum.TryParse(text, ignoreCase: true, out GamepadButton button)
            ? button
            : GamepadButton.None;
}
