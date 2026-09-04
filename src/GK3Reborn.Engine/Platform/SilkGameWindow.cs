using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace GK3Reborn.Platform;

/// <summary>
/// Supplies a Vulkan surface for a window.
/// </summary>
/// <remarks>
/// Deliberately declared in terms of native handles rather than Vulkan types. The
/// renderer needs a surface from the window, but the platform layer must not depend on
/// the graphics backend — the layering tests forbid it, and the reason is that a window
/// should not have to change when the renderer does.
/// </remarks>
public interface IVulkanSurfaceSource
{
    /// <summary>Instance extensions the window needs enabled to present.</summary>
    IReadOnlyList<string> RequiredInstanceExtensions { get; }

    /// <summary>Creates a surface for this window.</summary>
    /// <param name="vulkanInstance">Handle of the Vulkan instance.</param>
    /// <returns>Handle of the created surface.</returns>
    nint CreateSurface(nint vulkanInstance);
}

/// <summary>
/// Supplies the native window handle a Direct3D swapchain is made against.
/// </summary>
/// <remarks>
/// The Direct3D counterpart of <see cref="IVulkanSurfaceSource"/>, and declared the same
/// way and for the same reason: in terms of native handles, so that the platform layer
/// does not depend on a graphics backend. The asymmetry between the two is not an
/// oversight. Vulkan wants a surface *object*, which only the loader can make, so the
/// window has to make one; Direct3D wants nothing but the window handle, and DXGI makes
/// the swapchain itself.
/// </remarks>
public interface IWin32WindowSource
{
    /// <summary>The window's <c>HWND</c>, or zero where there is no such thing.</summary>
    nint WindowHandle { get; }
}

/// <summary>Which graphics API a window is opened for.</summary>
/// <remarks>
/// Not a rendering type, deliberately: a window should not have to know what a backend is.
/// The only thing it changes is what the window asks the platform for when it is created —
/// a Vulkan window and a Direct3D window are both windows with no client API, but Silk
/// refuses to make the Vulkan one on a machine with no loader, and a Direct3D machine
/// should not need one.
/// </remarks>
public enum WindowGraphics
{
    /// <summary>No client API. What a Direct3D window wants.</summary>
    None,

    /// <summary>Vulkan, so the window can hand out a surface.</summary>
    Vulkan,
}

/// <summary>
/// A game window backed by Silk.NET.
/// </summary>
/// <remarks>
/// <para>
/// Kept behind <see cref="IGameWindow"/> so the backend can change after the Windows and
/// Linux proofs the plan requires, and so headless tests never need one.
/// </para>
/// <para>
/// The window is created with no graphics API of its own: Vulkan owns presentation
/// entirely, and letting Silk.NET set up an OpenGL context alongside it would be both
/// wasteful and a source of driver confusion.
/// </para>
/// </remarks>
public sealed class SilkGameWindow : IGameWindow, IVulkanSurfaceSource, IWin32WindowSource, IGameInput
{
    /// <summary>
    /// This game's key names, resolved to Silk.NET's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="InputKey"/> is declared member for member against Silk's <see cref="Key"/>
    /// precisely so that this is a name lookup and not a switch somebody has to keep in
    /// step. Built once: <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> is
    /// reflection, and a key press is not the place for it.
    /// </para>
    /// <para>
    /// A member with no Silk counterpart maps to nothing and simply never fires, which is
    /// what a binding to a key this platform does not report should do.
    /// </para>
    /// </remarks>
    private static readonly Key[] SilkKeys = BuildKeyMap();

    /// <summary>And back the other way, for reporting which key was just pressed.</summary>
    private static readonly Dictionary<Key, InputKey> Ours = BuildKeyNames();

    /// <summary>Which of Silk's gamepad buttons is which of ours.</summary>
    /// <remarks>
    /// The face buttons are renamed on the way through. Silk calls them A, B, X and Y after
    /// the Xbox pad; this game calls them by where they are, because the same physical
    /// button is Cross on a PlayStation pad and B on a Nintendo one and a settings page has
    /// to be right about the hardware in the player's hands.
    /// </remarks>
    private static readonly Dictionary<ButtonName, GamepadButton> Pad = new()
    {
        [ButtonName.A] = GamepadButton.South,
        [ButtonName.B] = GamepadButton.East,
        [ButtonName.X] = GamepadButton.West,
        [ButtonName.Y] = GamepadButton.North,
        [ButtonName.LeftBumper] = GamepadButton.LeftShoulder,
        [ButtonName.RightBumper] = GamepadButton.RightShoulder,
        [ButtonName.LeftStick] = GamepadButton.LeftStick,
        [ButtonName.RightStick] = GamepadButton.RightStick,
        [ButtonName.Back] = GamepadButton.Back,
        [ButtonName.Start] = GamepadButton.Start,
        [ButtonName.Home] = GamepadButton.Home,
        [ButtonName.DPadUp] = GamepadButton.DPadUp,
        [ButtonName.DPadDown] = GamepadButton.DPadDown,
        [ButtonName.DPadLeft] = GamepadButton.DPadLeft,
        [ButtonName.DPadRight] = GamepadButton.DPadRight,
    };

    /// <summary>
    /// What the menu does with a pad, which is not a binding and is not meant to be.
    /// </summary>
    /// <remarks>
    /// A player who has rebound the inventory to the bottom face button has said something
    /// about the game, not about the menu, and a menu whose Choose key moved when they did
    /// would be a menu they could not get out of. Every console settings screen for twenty
    /// years has had the same fixed set, and this is it.
    /// </remarks>
    private static readonly (GamepadButton Button, EditKey Edit)[] Menu =
    [
        (GamepadButton.DPadUp, EditKey.Up),
        (GamepadButton.DPadDown, EditKey.Down),
        (GamepadButton.DPadLeft, EditKey.Left),
        (GamepadButton.DPadRight, EditKey.Right),
        (GamepadButton.South, EditKey.Enter),
        (GamepadButton.East, EditKey.Escape),
        (GamepadButton.LeftShoulder, EditKey.PreviousSection),
        (GamepadButton.RightShoulder, EditKey.NextSection),
    ];

    /// <summary>Builds the map from this game's key names onto Silk's.</summary>
    private static Key[] BuildKeyMap()
    {
        InputKey[] all = Enum.GetValues<InputKey>();
        var map = new Key[(int)all.Max() + 1];

        foreach (InputKey key in all)
        {
            map[(int)key] = Enum.TryParse(key.ToString(), out Key found) ? found : (Key)(-1);
        }

        return map;
    }

    /// <summary>And the map back the other way.</summary>
    private static Dictionary<Key, InputKey> BuildKeyNames()
    {
        Dictionary<Key, InputKey> names = [];

        foreach (InputKey key in Enum.GetValues<InputKey>())
        {
            if (key != InputKey.None && Enum.TryParse(key.ToString(), out Key found))
            {
                names[found] = key;
            }
        }

        return names;
    }

    /// <summary>How far a trigger has to travel before it counts as a press.</summary>
    /// <remarks>
    /// Halfway. A trigger bound to an action is being used as a button, and a button that
    /// fires on the first millimetre of travel is one nobody can rest a finger on.
    /// </remarks>
    private const float TriggerPress = 0.5f;

    /// <summary>How far a stick has to travel before it counts as a press.</summary>
    /// <remarks>
    /// Further than the deadzone, and past the middle: this is only used for capturing a
    /// binding and for stepping a menu, where an accidental nudge is worse than having to
    /// push properly.
    /// </remarks>
    private const float StickPress = 0.6f;

    /// <summary>Which key does which editing job.</summary>
    /// <remarks>
    /// Grave and Escape both appear here and in the camera bindings, which is deliberate:
    /// the key is one key and what it means depends on whether the console has the
    /// keyboard. Deciding that here would put a piece of the interface in the platform
    /// layer.
    /// </remarks>
    private static readonly (EditKey Edit, Key Which)[] Editing =
    [
        (EditKey.Backspace, Key.Backspace),
        (EditKey.Enter, Key.Enter),
        (EditKey.Enter, Key.KeypadEnter),
        (EditKey.Tab, Key.Tab),
        (EditKey.Up, Key.Up),
        (EditKey.Down, Key.Down),
        (EditKey.Left, Key.Left),
        (EditKey.Right, Key.Right),
        (EditKey.Escape, Key.Escape),
        (EditKey.Console, Key.GraveAccent),
        (EditKey.PreviousSection, Key.PageUp),
        (EditKey.NextSection, Key.PageDown),
    ];

    private readonly IWindow _window;
    /// <summary>How far the pointer may travel between press and release and still be a click.</summary>
    private const float DragThreshold = 4f;

    /// <summary>How long a second click may take to arrive and still pair, in seconds.</summary>
    /// <remarks>
    /// Windows' own default. Worth matching rather than choosing, because a player's idea
    /// of how fast a double-click is comes from the rest of their machine.
    /// </remarks>
    private const double DoubleClickWindow = 0.5;

    /// <summary>How far apart two clicks may land and still pair, in pixels.</summary>
    /// <remarks>
    /// Two clicks at opposite ends of the room are two decisions, however quickly they were
    /// made. Looser than <see cref="DragThreshold"/>: a hand that is hurrying wanders.
    /// </remarks>
    private const float DoubleClickDistance = 8f;

    private readonly HashSet<CameraAction> _pressed = [];
    private readonly HashSet<PointerButton> _clicked = [];
    private readonly HashSet<PointerButton> _doubleClicked = [];
    private readonly HashSet<EditKey> _edits = [];
    private readonly System.Text.StringBuilder _typed = new();
    private readonly Dictionary<PointerButton, (double At, Vector2 Where)> _lastClick = [];
    private readonly HashSet<GamepadButton> _padPressed = [];
    private readonly HashSet<GamepadButton> _padHeld = [];
    private IInputContext? _input;
    private IKeyboard? _keyboard;
    private IMouse? _mouse;
    private Vector2 _pointerDelta;
    private Vector2 _lastPointer;
    private Vector2 _pressedAt;
    private int _scroll;
    private bool _hasPointer;

    /// <summary>Where the mouse itself was, as against where the game thinks the pointer is.</summary>
    /// <remarks>
    /// The two part company the moment a stick moves the cursor, and come back together the
    /// moment the mouse is touched. Keeping the mouse's own position separately is what
    /// tells the difference between "the mouse moved" and "we moved the mouse", and without
    /// it the stick and the mouse fight over the cursor every frame.
    /// </remarks>
    private Vector2 _mouseAt;

    /// <summary>The key pressed this frame, for a page that is listening for one.</summary>
    private InputKey _anyKey;

    /// <summary>And the pad button.</summary>
    private GamepadButton _anyButton;

    /// <summary>When the last frame was, for moving the cursor at a speed rather than a rate.</summary>
    private double _lastFrame;

    private SilkGameWindow(IWindow window)
    {
        _window = window;

        _window.FramebufferResize += size =>
        {
            // Minimising reports a zero-sized framebuffer. Passing that on would have the
            // renderer build a zero-extent swapchain, so it is filtered here.
            if (size.X > 0 && size.Y > 0)
            {
                Resized?.Invoke(size.X, size.Y);
            }
        };
    }

    /// <inheritdoc/>
    public event Action<int, int>? Resized;

    /// <inheritdoc/>
    public int FramebufferWidth => _window.FramebufferSize.X;

    /// <inheritdoc/>
    public int FramebufferHeight => _window.FramebufferSize.Y;

    /// <inheritdoc/>
    public float DpiScale => _window.Size.X > 0
        ? (float)_window.FramebufferSize.X / _window.Size.X
        : 1f;

    /// <inheritdoc/>
    public WindowMode Mode => _window.WindowState switch
    {
        WindowState.Fullscreen => WindowMode.ExclusiveFullscreen,
        WindowState.Maximized => WindowMode.Windowed,
        _ => _window.WindowBorder == WindowBorder.Hidden
            ? WindowMode.BorderlessFullscreen
            : WindowMode.Windowed,
    };

    /// <summary>Whether the window has been asked to close.</summary>
    public bool IsClosing => _window.IsClosing;

    /// <inheritdoc/>
    public IReadOnlyList<string> RequiredInstanceExtensions
    {
        get
        {
            unsafe
            {
                if (_window.VkSurface is null)
                {
                    return [];
                }

                byte** names = _window.VkSurface.GetRequiredExtensions(out uint count);
                string[] extensions = new string[count];

                for (uint i = 0; i < count; i++)
                {
                    extensions[i] = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)names[i]) ?? string.Empty;
                }

                return extensions;
            }
        }
    }

    /// <summary>Opens a window.</summary>
    /// <param name="title">Window title.</param>
    /// <param name="width">Initial width in logical pixels.</param>
    /// <param name="height">Initial height in logical pixels.</param>
    /// <param name="graphics">Which API the window will present with.</param>
    /// <returns>The window.</returns>
    /// <remarks>
    /// Both kinds of window are windows with no client API — the platform is never asked
    /// to set up a context, because the backend owns presentation entirely and an OpenGL
    /// context alongside it would be both wasteful and a source of driver confusion. The
    /// difference is only that a Vulkan window is declared as one, so that Silk will give
    /// it a surface, and that declaration fails on a machine with no loader. A Direct3D
    /// machine should not need one.
    /// </remarks>
    public static SilkGameWindow Open(
        string title,
        int width = 1280,
        int height = 720,
        WindowGraphics graphics = WindowGraphics.Vulkan)
    {
        WindowOptions options = WindowOptions.DefaultVulkan with
        {
            Title = title,
            Size = new Vector2D<int>(width, height),
            API = graphics == WindowGraphics.Vulkan ? GraphicsAPI.DefaultVulkan : GraphicsAPI.None,
        };

        IWindow window = Window.Create(options);
        window.Initialize();

        var created = new SilkGameWindow(window);
        created.AttachInput();

        return created;
    }

    /// <inheritdoc/>
    public unsafe nint CreateSurface(nint vulkanInstance)
    {
        if (_window.VkSurface is null)
        {
            throw new InvalidOperationException("This window was not created for Vulkan.");
        }

        return (nint)_window.VkSurface
            .Create<nint>(new Silk.NET.Core.Native.VkHandle(vulkanInstance), null)
            .Handle;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Zero anywhere but Windows, and zero on a Windows window Silk chose to back with
    /// something other than Win32. A swapchain cannot be made against zero, so the caller
    /// checks rather than assuming — the alternative is DXGI refusing with an invalid
    /// argument and nothing to say which argument.
    /// </remarks>
    public nint WindowHandle => _window.Native?.Win32?.Hwnd ?? 0;

    /// <inheritdoc/>
    public Vector2 PointerDelta => _pointerDelta;

    /// <inheritdoc/>
    public Vector2 PointerPosition => _lastPointer;

    /// <inheritdoc/>
    public bool WasClicked(PointerButton button) => _clicked.Contains(button);

    /// <inheritdoc />
    public bool WasDoubleClicked(PointerButton button) => _doubleClicked.Contains(button);

    /// <inheritdoc />
    public string Typed => _typed.ToString();

    /// <inheritdoc />
    public bool WasPressed(EditKey key) => _edits.Contains(key);

    /// <inheritdoc/>
    public int ScrollDelta => _scroll;

    /// <inheritdoc/>
    public bool IsDragging =>
        _mouse is not null &&
        (_mouse.IsButtonPressed(MouseButton.Left) || _mouse.IsButtonPressed(MouseButton.Right));

    /// <inheritdoc/>
    public bool IsHeld(PointerButton button) =>
        _mouse is not null &&
        _mouse.IsButtonPressed(button switch
        {
            PointerButton.Secondary => MouseButton.Right,
            PointerButton.Middle => MouseButton.Middle,
            _ => MouseButton.Left,
        });

    /// <inheritdoc/>
    public bool IsHeld(CameraAction action)
    {
        if (_padHeld.Contains(Bindings.Button(action)))
        {
            return true;
        }

        if (_keyboard is null)
        {
            return false;
        }

        foreach (InputKey key in Bindings.Keys(action))
        {
            if (Held(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether one of this game's keys is down.</summary>
    private bool Held(InputKey key)
    {
        Key which = Which(key);

        return which >= 0 && _keyboard is not null && _keyboard.IsKeyPressed(which);
    }

    /// <summary>Silk's key for one of ours, or a negative value where it has none.</summary>
    private static Key Which(InputKey key) =>
        key > InputKey.None && (int)key < SilkKeys.Length ? SilkKeys[(int)key] : (Key)(-1);

    /// <inheritdoc/>
    public bool WasPressed(CameraAction action) => _pressed.Contains(action);

    /// <inheritdoc/>
    public InputBindings Bindings { get; set; } = InputBindings.Default;

    /// <inheritdoc/>
    public bool HasGamepad => _input is { Gamepads.Count: > 0 } &&
        _input.Gamepads.Any(pad => pad.IsConnected);

    /// <inheritdoc/>
    public GamepadSticks Sticks { get; private set; } = GamepadSticks.Still;

    /// <inheritdoc/>
    public InputKey AnyKey => _anyKey;

    /// <inheritdoc/>
    public GamepadButton AnyButton => _anyButton;

    /// <inheritdoc/>
    /// <remarks>
    /// Set from the settings, because the right speed depends on how large the window is
    /// and on the person. The default crosses a 1080p screen in about a second and a half,
    /// which is quick enough to reach a doorway and slow enough to land on a keyhole.
    /// </remarks>
    public float PointerSpeed { get; set; } = 1200f;

    /// <inheritdoc/>
    public void MovePointer(Vector2 position)
    {
        _lastPointer = position;

        if (_mouse is not null)
        {
            _mouse.Position = position;
            _mouseAt = position;
        }
    }

    /// <inheritdoc/>
    public void EndFrame() => Forget();

    /// <inheritdoc/>
    public void Forget()
    {
        _pressed.Clear();
        _clicked.Clear();
        _doubleClicked.Clear();
        _edits.Clear();
        _typed.Clear();
        _padPressed.Clear();
        _pointerDelta = Vector2.Zero;
        _scroll = 0;
        _anyKey = InputKey.None;
        _anyButton = GamepadButton.None;
    }

    /// <inheritdoc/>
    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Borderless is a hidden border plus the monitor's own size and position, not a
    /// window state of its own: Silk.NET's <c>Fullscreen</c> is the exclusive kind, which
    /// takes the display over and makes alt-tabbing a mode change. The borderless
    /// arrangement is what most people mean by fullscreen now — it composites, it switches
    /// away instantly, and it costs a frame of latency nobody in an adventure game will
    /// notice.
    /// </para>
    /// <para>
    /// Every branch checks what is already true before changing anything. This is called
    /// whenever any setting changes, and setting a window state it is already in makes
    /// some backends flash the window.
    /// </para>
    /// </remarks>
    public void Present(WindowMode mode, int width = 0, int height = 0)
    {
        IMonitor? monitor = _window.Monitor ?? Silk.NET.Windowing.Monitor.GetMainMonitor(_window);

        switch (mode)
        {
            case WindowMode.BorderlessFullscreen:
                if (_window.WindowState == WindowState.Fullscreen)
                {
                    _window.WindowState = WindowState.Normal;
                }

                if (_window.WindowBorder != WindowBorder.Hidden)
                {
                    _window.WindowBorder = WindowBorder.Hidden;
                }

                if (monitor is not null)
                {
                    Vector2D<int> at = monitor.Bounds.Origin;
                    Vector2D<int> size = monitor.Bounds.Size;

                    if (_window.Position != at)
                    {
                        _window.Position = at;
                    }

                    if (_window.Size != size)
                    {
                        _window.Size = size;
                    }
                }

                break;

            case WindowMode.ExclusiveFullscreen:
                if (width > 0 && height > 0 && _window.Size != new Vector2D<int>(width, height))
                {
                    _window.Size = new Vector2D<int>(width, height);
                }

                if (_window.WindowState != WindowState.Fullscreen)
                {
                    _window.WindowState = WindowState.Fullscreen;
                }

                break;

            default:
                if (_window.WindowState == WindowState.Fullscreen)
                {
                    _window.WindowState = WindowState.Normal;
                }

                if (_window.WindowBorder != WindowBorder.Resizable)
                {
                    _window.WindowBorder = WindowBorder.Resizable;
                }

                if (width > 0 && height > 0 && _window.Size != new Vector2D<int>(width, height))
                {
                    _window.Size = new Vector2D<int>(width, height);

                    // Put back on the monitor after a resize that would otherwise leave it
                    // half off the bottom, which is what happens when a small window is
                    // enlarged near an edge.
                    if (monitor is not null)
                    {
                        Vector2D<int> bounds = monitor.Bounds.Size;
                        Vector2D<int> origin = monitor.Bounds.Origin;

                        _window.Position = new Vector2D<int>(
                            Math.Clamp(_window.Position.X, origin.X, origin.X + Math.Max(0, bounds.X - width)),
                            Math.Clamp(_window.Position.Y, origin.Y, origin.Y + Math.Max(0, bounds.Y - height)));
                    }
                }

                break;
        }
    }

    /// <inheritdoc/>
    public void PumpEvents()
    {
        _window.DoEvents();

        double now = _window.Time;
        float seconds = _lastFrame > 0 ? (float)Math.Clamp(now - _lastFrame, 0, 0.1) : 0f;
        _lastFrame = now;

        Poll();

        // Pointer movement is tracked by difference rather than through the move event,
        // because raw motion is not delivered on every backend and a difference works the
        // same everywhere.
        if (_mouse is null)
        {
            return;
        }

        var position = new Vector2(_mouse.Position.X, _mouse.Position.Y);

        // The mouse itself, if it has moved. It always wins: somebody who reaches for the
        // mouse has said which device they want, and a cursor that had to be given back by
        // putting the pad down would be a cursor with a mode in it.
        if (!_hasPointer || (position - _mouseAt).LengthSquared() > 0.01f)
        {
            if (_hasPointer)
            {
                _pointerDelta += position - _lastPointer;
            }

            _lastPointer = position;
            _mouseAt = position;
            _hasPointer = true;

            return;
        }

        _mouseAt = position;

        // Otherwise the left stick, if it is being pushed. Squared, so that a small push is
        // a small movement: a linear stick is either too slow to cross the screen with or
        // too coarse to land on anything, and the square is what every console cursor does
        // about that.
        Vector2 push = Sticks.Left;
        float reach = push.Length();

        if (seconds <= 0f || reach <= 0f || PointerSpeed <= 0f)
        {
            return;
        }

        Vector2 moved = push * (reach * PointerSpeed * seconds);

        _lastPointer = new Vector2(
            Math.Clamp(_lastPointer.X + moved.X, 0, Math.Max(0, _window.Size.X - 1)),
            Math.Clamp(_lastPointer.Y + moved.Y, 0, Math.Max(0, _window.Size.Y - 1)));

        _pointerDelta += moved;

        // Put the real cursor where the stick has driven it, so that the arrow the operating
        // system draws is the one the game is acting on, and record it as ours - otherwise
        // the next frame reads it as the mouse having moved and the two chase each other.
        _mouse.Position = _lastPointer;
        _mouseAt = _lastPointer;
    }

    /// <summary>Reads the pad, once a frame.</summary>
    /// <remarks>
    /// <para>
    /// Polled rather than taken from the events, because two of the four things a pad
    /// reports are not events at all: a stick that is being held over is not moving, and a
    /// trigger at forty per cent has not been pressed. The buttons arrive as events too and
    /// are read here anyway, so that everything about the pad is answered from one place at
    /// one moment in the frame.
    /// </para>
    /// <para>
    /// The first connected pad and no others. Two people cannot play this game at once, and
    /// a second pad plugged in for something else should not be able to move the cursor.
    /// </para>
    /// </remarks>
    private void Poll()
    {
        IGamepad? pad = null;

        if (_input is not null)
        {
            foreach (IGamepad candidate in _input.Gamepads)
            {
                if (candidate.IsConnected)
                {
                    pad = candidate;

                    break;
                }
            }
        }

        if (pad is null)
        {
            Sticks = GamepadSticks.Still;
            _padHeld.Clear();

            return;
        }

        Vector2 left = Vector2.Zero;
        Vector2 right = Vector2.Zero;

        foreach (Thumbstick stick in pad.Thumbsticks)
        {
            var where = new Vector2(stick.X, stick.Y);

            if (stick.Index == 0)
            {
                left = where;
            }
            else if (stick.Index == 1)
            {
                right = where;
            }
        }

        float leftTrigger = 0f;
        float rightTrigger = 0f;

        foreach (Trigger trigger in pad.Triggers)
        {
            if (trigger.Index == 0)
            {
                leftTrigger = trigger.Position;
            }
            else if (trigger.Index == 1)
            {
                rightTrigger = trigger.Position;
            }
        }

        Sticks = new GamepadSticks(left, right, leftTrigger, rightTrigger);

        // What is down now, so that what has just gone down is the difference. Held is kept
        // between frames and pressed is not, which is the same shape the keyboard has.
        HashSet<GamepadButton> down = [];

        foreach (Button button in pad.Buttons)
        {
            if (button.Pressed && Pad.TryGetValue(button.Name, out GamepadButton which))
            {
                down.Add(which);
            }
        }

        if (leftTrigger >= TriggerPress)
        {
            down.Add(GamepadButton.LeftTrigger);
        }

        if (rightTrigger >= TriggerPress)
        {
            down.Add(GamepadButton.RightTrigger);
        }

        // The left stick steps a menu as well as moving the cursor, because a page of
        // settings is a list and a list is walked rather than pointed at. Held over, it is
        // the D-pad held over, so the same edge detection covers both and neither runs down
        // the whole page in a third of a second.
        if (left.Y <= -StickPress)
        {
            down.Add(GamepadButton.DPadUp);
        }

        if (left.Y >= StickPress)
        {
            down.Add(GamepadButton.DPadDown);
        }

        foreach (GamepadButton button in down)
        {
            if (_padHeld.Add(button))
            {
                Fell(button);
            }
        }

        _padHeld.RemoveWhere(button => !down.Contains(button));
    }

    /// <summary>Notes a pad button that has just gone down, and what it means.</summary>
    /// <remarks>
    /// Every meaning at once, in the same way the keyboard records a key as an editing key
    /// and as an action both and lets whoever is listening this frame decide which it was.
    /// A pad in a menu is stepping a list; the same pad in a room is opening the inventory.
    /// </remarks>
    private void Fell(GamepadButton button)
    {
        _padPressed.Add(button);
        _anyButton = button;

        foreach ((GamepadButton which, EditKey edit) in Menu)
        {
            if (which == button)
            {
                _edits.Add(edit);
            }
        }

        foreach (CameraAction action in InputBindings.Actions)
        {
            if (Bindings.Button(action) == button)
            {
                _pressed.Add(action);
            }
        }

        foreach (PointerButton pointer in Enum.GetValues<PointerButton>())
        {
            if (Bindings.Button(pointer) == button)
            {
                _clicked.Add(pointer);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _input?.Dispose();
        _window.Dispose();
    }

    /// <summary>Attaches the keyboard and mouse.</summary>
    /// <remarks>
    /// Done after the window exists rather than in the constructor, because creating the
    /// input context requires an initialised window.
    /// </remarks>
    private void AttachInput()
    {
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards.Count > 0 ? _input.Keyboards[0] : null;
        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;

        if (_mouse is not null)
        {
            // A click is only a click if the pointer did not travel while the button was
            // down. Dragging to look around passes over every noun between where it
            // started and where it stopped, and acting on the one it happens to end over
            // is not what the player asked for.
            _mouse.MouseDown += (_, _) =>
            {
                _pressedAt = new Vector2(_mouse.Position.X, _mouse.Position.Y);
            };

            _mouse.Scroll += (_, wheel) =>
            {
                // Rounded away from zero, so the smallest turn a trackpad reports still
                // counts as one notch rather than being lost.
                _scroll += Math.Sign(wheel.Y) * (int)Math.Ceiling(Math.Abs(wheel.Y));
            };

            _mouse.MouseUp += (_, mouseButton) =>
            {
                var at = new Vector2(_mouse.Position.X, _mouse.Position.Y);

                if ((at - _pressedAt).Length() > DragThreshold)
                {
                    return;
                }

                PointerButton? which = mouseButton switch
                {
                    MouseButton.Left => PointerButton.Primary,
                    MouseButton.Right => PointerButton.Secondary,
                    MouseButton.Middle => PointerButton.Middle,
                    _ => null,
                };

                if (which is not { } button)
                {
                    return;
                }

                _clicked.Add(button);

                // The window's own clock, which is the one this layer is allowed to read.
                double now = _window.Time;

                if (_lastClick.TryGetValue(button, out (double At, Vector2 Where) previous) &&
                    now - previous.At <= DoubleClickWindow &&
                    (at - previous.Where).Length() <= DoubleClickDistance)
                {
                    _doubleClicked.Add(button);

                    // Forgotten, so a third click in quick succession starts a new pair
                    // rather than making every click after the second a double one.
                    _lastClick.Remove(button);
                }
                else
                {
                    _lastClick[button] = (now, at);
                }
            };
        }

        if (_keyboard is not null)
        {
            // What the player meant to write, with the layout and the shift state already
            // applied by the platform. Reconstructing this from key codes is how a console
            // ends up working on one keyboard layout and no others.
            _keyboard.KeyChar += (_, c) =>
            {
                if (c >= ' ' && c != (char)127)
                {
                    _typed.Append(c);
                }
            };

            _keyboard.KeyDown += (_, key, _) =>
            {
                // Recorded whether or not anything is reading them. Which of the two
                // meanings a key has — a camera action or an edit — is decided by whoever
                // is listening this frame, and a console that is open takes the keyboard.
                foreach ((EditKey edit, Key which) in Editing)
                {
                    if (key == which)
                    {
                        _edits.Add(edit);
                    }
                }

                // Which key it was, whatever it is bound to, for a Controls page that is
                // waiting to hear one. Recorded before the bindings are consulted, because
                // the key somebody presses to rebind an action is very often already bound
                // to something else — that is rather the point of rebinding it.
                if (Ours.TryGetValue(key, out InputKey ours))
                {
                    _anyKey = ours;

                    foreach (CameraAction action in InputBindings.Actions)
                    {
                        if (Bindings.Keys(action).Contains(ours))
                        {
                            _pressed.Add(action);
                        }
                    }
                }
            };
        }
    }
}
