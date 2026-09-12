using System.Globalization;

namespace GK3Reborn.Platform;

/// <summary>Which key and which gamepad button do which job.</summary>
public sealed class InputBindings
{
    /// <summary>What every action answers to when nobody has said otherwise.</summary>
    private static readonly Dictionary<CameraAction, InputKey[]> DefaultKeys = new()
    {
        [CameraAction.Forward] = [InputKey.W, InputKey.Up],
        [CameraAction.Back] = [InputKey.S, InputKey.Down],
        [CameraAction.Left] = [InputKey.A, InputKey.Left],
        [CameraAction.Right] = [InputKey.D, InputKey.Right],
        [CameraAction.Up] = [InputKey.E, InputKey.Space],
        [CameraAction.Down] = [InputKey.Q, InputKey.ControlLeft],
        [CameraAction.Fast] = [InputKey.ShiftLeft, InputKey.ShiftRight],
        [CameraAction.Reset] = [InputKey.R],
        [CameraAction.NextCamera] = [InputKey.Tab],
        [CameraAction.CycleRayTracing] = [InputKey.F2],

        // The original made the inventory a small target to click at the edge of the screen.
        [CameraAction.Inventory] = [InputKey.I],
        [CameraAction.Journal] = [InputKey.J],
        [CameraAction.ShowHotspots] = [InputKey.AltLeft, InputKey.AltRight],

        // Where every adventure game has put them for thirty years.
        [CameraAction.QuickSave] = [InputKey.F5],
        [CameraAction.QuickLoad] = [InputKey.F9],
        [CameraAction.Quit] = [InputKey.Escape],

        // Under the hand that is already on WASD.
        [CameraAction.FreeCursor] = [InputKey.C],
    };

    /// <summary>What every action answers to on a gamepad when nobody has said otherwise.</summary>
    private static readonly Dictionary<CameraAction, GamepadButton> DefaultButtons = new()
    {
        [CameraAction.Inventory] = GamepadButton.North,
        [CameraAction.Journal] = GamepadButton.Back,
        [CameraAction.ShowHotspots] = GamepadButton.LeftTrigger,
        [CameraAction.NextCamera] = GamepadButton.RightShoulder,
        [CameraAction.Reset] = GamepadButton.LeftShoulder,
        [CameraAction.Fast] = GamepadButton.LeftStick,
        [CameraAction.Quit] = GamepadButton.Start,
    };

    /// <summary>Which pad button is which mouse button.</summary>
    private static readonly Dictionary<PointerButton, GamepadButton> DefaultPointers = new()
    {
        [PointerButton.Primary] = GamepadButton.South,
        [PointerButton.Secondary] = GamepadButton.East,
        [PointerButton.Middle] = GamepadButton.West,
    };

    private readonly Dictionary<CameraAction, InputKey[]> _keys;
    private readonly Dictionary<CameraAction, GamepadButton> _buttons;
    private readonly Dictionary<PointerButton, GamepadButton> _pointers;

    private InputBindings( Dictionary<CameraAction, InputKey[]> keys, Dictionary<CameraAction, GamepadButton> buttons,
        Dictionary<PointerButton, GamepadButton> pointers)
    {
        _keys = keys;
        _buttons = buttons;
        _pointers = pointers;
    }

    /// <summary>The bindings nobody has changed.</summary>
    public static InputBindings Default { get; } = new([], [], []);

    /// <summary>Every action that can be bound, in the order a page lists them.</summary>
    public static IReadOnlyList<CameraAction> Actions { get; } =
        [.. Enum.GetValues<CameraAction>()];

    /// <summary>Which keys trigger an action.</summary>
    /// <returns>Its keys, which may be none.</returns>
    /// <param name="action">The action.</param>
    public IReadOnlyList<InputKey> Keys(CameraAction action) => _keys.TryGetValue(action, out InputKey[]? changed) ? changed
            : DefaultKeys.TryGetValue(action, out InputKey[]? standard) ? standard : [];

    /// <summary>Which gamepad button triggers an action.</summary>
    /// <returns>Its button, or .</returns>
    /// <param name="action">The action.</param>
    public GamepadButton Button(CameraAction action) => _buttons.TryGetValue(action, out GamepadButton changed) ? changed
            : DefaultButtons.GetValueOrDefault(action);

    /// <summary>Which gamepad button is a mouse button.</summary>
    /// <returns>Its pad button, or .</returns>
    /// <param name="button">The mouse button.</param>
    public GamepadButton Button(PointerButton button) => _pointers.TryGetValue(button, out GamepadButton changed) ? changed
            : DefaultPointers.GetValueOrDefault(button);

    /// <summary>Whether an action is bound to what it was born bound to.</summary>
    /// <returns>True when the player has not touched it.</returns>
    /// <param name="action">The action.</param>
    public bool IsDefault(CameraAction action) => !_keys.ContainsKey(action) && !_buttons.ContainsKey(action);

    /// <summary>Whether anything at all has been changed.</summary>
    public bool Untouched => _keys.Count == 0 && _buttons.Count == 0 && _pointers.Count == 0;

    /// <summary>The same bindings with one action answering to one key.</summary>
    /// <returns>The new bindings.</returns>
    /// <param name="action">The action.</param>
    /// <param name="key">The key, or to unbind it.</param>
    public InputBindings With(CameraAction action, InputKey key)
    {
        Dictionary<CameraAction, InputKey[]> keys = new(_keys);

        if (key != InputKey.None)
        {
            foreach (CameraAction other in Actions)
            {
                if (other == action)
                {
                    continue;
                }

                IReadOnlyList<InputKey> had = Keys(other);

                if (had.Contains(key))
                {
                    keys[other] = [.. had.Where(k => k != key)];
                }
            }
        }

        keys[action] = key == InputKey.None ? [] : [key];

        return new InputBindings(keys, new(_buttons), new(_pointers));
    }

    /// <summary>The same bindings with one action answering to one pad button.</summary>
    /// <returns>The new bindings.</returns>
    /// <param name="action">The action.</param>
    /// <param name="button">The button, or to unbind it.</param>
    public InputBindings With(CameraAction action, GamepadButton button)
    {
        Dictionary<CameraAction, GamepadButton> buttons = new(_buttons);
        Dictionary<PointerButton, GamepadButton> pointers = new(_pointers);

        if (button != GamepadButton.None)
        {
            foreach (CameraAction other in Actions)
            {
                if (other != action && Button(other) == button)
                {
                    buttons[other] = GamepadButton.None;
                }
            }

            foreach (PointerButton other in Enum.GetValues<PointerButton>())
            {
                if (Button(other) == button)
                {
                    pointers[other] = GamepadButton.None;
                }
            }
        }

        buttons[action] = button;

        return new InputBindings(new(_keys), buttons, pointers);
    }

    /// <summary>The same bindings with one mouse button answering to one pad button.</summary>
    /// <returns>The new bindings.</returns>
    /// <param name="which">The mouse button.</param>
    /// <param name="button">The pad button, or none.</param>
    public InputBindings With(PointerButton which, GamepadButton button)
    {
        Dictionary<CameraAction, GamepadButton> buttons = new(_buttons);
        Dictionary<PointerButton, GamepadButton> pointers = new(_pointers);

        if (button != GamepadButton.None)
        {
            foreach (CameraAction other in Actions)
            {
                if (Button(other) == button)
                {
                    buttons[other] = GamepadButton.None;
                }
            }

            foreach (PointerButton other in Enum.GetValues<PointerButton>())
            {
                if (other != which && Button(other) == button)
                {
                    pointers[other] = GamepadButton.None;
                }
            }
        }

        pointers[which] = button;

        return new InputBindings(new(_keys), buttons, pointers);
    }

    /// <summary>The same bindings with everything back where it started.</summary>
    public static InputBindings Cleared() => Default;

    /// <summary>What to write in the settings file: the differences and nothing else.</summary>
    /// <returns>The changed bindings, or null when there are none.</returns>
    public StoredBindings? Store()
    {
        if (Untouched)
        {
            return null;
        }

        Dictionary<string, string> keys = [];
        Dictionary<string, string> buttons = [];
        Dictionary<string, string> pointers = [];

        foreach ((CameraAction action, InputKey[] bound) in _keys)
        {
            keys[action.ToString()] = string.Join(',', bound);
        }

        foreach ((CameraAction action, GamepadButton button) in _buttons)
        {
            buttons[action.ToString()] = button.ToString();
        }

        foreach ((PointerButton pointer, GamepadButton button) in _pointers)
        {
            pointers[pointer.ToString()] = button.ToString();
        }

        return new StoredBindings(keys, buttons, pointers);
    }

    /// <summary>Reads the bindings back.</summary>
    /// <returns>The bindings.</returns>
    /// <param name="stored">What was in the settings file, or null for none.</param>
    public static InputBindings Restore(StoredBindings? stored)
    {
        if (stored is null)
        {
            return Default;
        }

        Dictionary<CameraAction, InputKey[]> keys = [];
        Dictionary<CameraAction, GamepadButton> buttons = [];
        Dictionary<PointerButton, GamepadButton> pointers = [];

        foreach ((string name, string bound) in stored.Keys)
        {
            if (Enum.TryParse(name, ignoreCase: true, out CameraAction action))
            {
                keys[action] =
                [
                    .. bound .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) .Select(InputKeys.Parse)
                        .Where(k => k != InputKey.None), ];
            }
        }

        foreach ((string name, string bound) in stored.Buttons)
        {
            if (Enum.TryParse(name, ignoreCase: true, out CameraAction action))
            {
                buttons[action] = GamepadButtons.Parse(bound);
            }
        }

        foreach ((string name, string bound) in stored.Pointers)
        {
            if (Enum.TryParse(name, ignoreCase: true, out PointerButton pointer))
            {
                pointers[pointer] = GamepadButtons.Parse(bound);
            }
        }

        return new InputBindings(keys, buttons, pointers);
    }

    /// <summary>What to print on a settings page for an action's keys.</summary>
    /// <returns>The keys, separated by commas, or a dash for none.</returns>
    /// <param name="action">The action.</param>
    public string Describe(CameraAction action)
    {
        IReadOnlyList<InputKey> keys = Keys(action);

        return keys.Count == 0 ? "—" : string.Join(", ", keys.Select(InputKeys.Describe));
    }

    /// <summary>What to call an action on a settings page.</summary>
    /// <returns>Its name, in words.</returns>
    /// <param name="action">The action.</param>
    public static string Name(CameraAction action) => action switch
    {
        CameraAction.Forward => "Camera forward", CameraAction.Back => "Camera back", CameraAction.Left => "Camera left",
        CameraAction.Right => "Camera right", CameraAction.Up => "Camera up", CameraAction.Down => "Camera down",
        CameraAction.Fast => "Camera faster", CameraAction.Reset => "Back to the room's camera", CameraAction.NextCamera => "Next camera angle",
        CameraAction.CycleRayTracing => "Step the lighting quality", CameraAction.Inventory => "Inventory",
        CameraAction.ShowHotspots => "Show what can be clicked", CameraAction.Journal => "Journal", CameraAction.QuickSave => "Quick save",
        CameraAction.QuickLoad => "Quick load", CameraAction.Quit => "Menu", CameraAction.FreeCursor => "Show the pointer", _ => action.ToString(),
    };

    /// <summary>What to call a mouse button on a settings page.</summary>
    /// <returns>What clicking it means in this game.</returns>
    /// <param name="button">The button.</param>
    public static string Name(PointerButton button) => button switch
    {
        PointerButton.Primary => "Do the thing", PointerButton.Secondary => "Ask what it does", PointerButton.Middle => "Look closely",
        _ => button.ToString(),
    };
}

/// <summary>The bindings as they sit in the settings file.</summary>
/// <param name="Keys">Action name to a comma-separated list of key names.</param>
/// <param name="Buttons">Action name to a gamepad button name.</param>
/// <param name="Pointers">Mouse button name to a gamepad button name.</param>
public sealed record StoredBindings( Dictionary<string, string> Keys, Dictionary<string, string> Buttons, Dictionary<string, string> Pointers)
{
    /// <summary>An empty set, for a file that had none.</summary>
    public StoredBindings() : this([], [], [])
    {
    }
}

/// <summary>Where a gamepad's sticks point, and how hard its triggers are pressed.</summary>
/// <param name="Left">The left stick, each axis from -1 to 1, with y positive downwards.</param>
/// <param name="Right">The right stick.</param>
/// <param name="LeftTrigger">The left trigger, from nought to one.</param>
/// <param name="RightTrigger">The right trigger.</param>
public readonly record struct GamepadSticks( System.Numerics.Vector2 Left, System.Numerics.Vector2 Right, float LeftTrigger, float RightTrigger)
{
    /// <summary>A pad nobody is touching.</summary>
    public static GamepadSticks Still => default;

    /// <summary>Whether anything is being pushed at all.</summary>
    public bool Moving => Left != System.Numerics.Vector2.Zero || Right != System.Numerics.Vector2.Zero;

    /// <summary>How fast the pointer is moving, for the log.</summary>
    public override string ToString() => string.Create( CultureInfo.InvariantCulture,
        $"L({Left.X:F2},{Left.Y:F2}) R({Right.X:F2},{Right.Y:F2}) T({LeftTrigger:F2},{RightTrigger:F2})");
}
