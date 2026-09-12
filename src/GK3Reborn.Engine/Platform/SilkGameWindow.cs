using System.Numerics;
using GK3Reborn.Foundation.Diagnostics;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace GK3Reborn.Platform;

/// <summary>
/// Supplies a Vulkan surface for a window.
/// </summary>
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
public interface IWin32WindowSource
{
    /// <summary>The window's <c>HWND</c>, or zero where there is no such thing.</summary>
    nint WindowHandle { get; }
}

/// <summary>Which graphics API a window is opened for.</summary>
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
public sealed class SilkGameWindow : IGameWindow, IVulkanSurfaceSource, IWin32WindowSource, IGameInput
{
    /// <summary>
    /// This game's key names, resolved to Silk.NET's own.
    /// </summary>
    private static readonly Key[] SilkKeys = BuildKeyMap();

    /// <summary>And back the other way, for reporting which key was just pressed.</summary>
    private static readonly Dictionary<Key, InputKey> Ours = BuildKeyNames();

    /// <summary>Which of Silk's gamepad buttons is which of ours.</summary>
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
    private const float TriggerPress = 0.5f;

    /// <summary>How far a stick has to travel before it counts as a press.</summary>
    private const float StickPress = 0.6f;

    /// <summary>Which key does which editing job.</summary>
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
    private const double DoubleClickWindow = 0.5;

    /// <summary>How far apart two clicks may land and still pair, in pixels.</summary>
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
    private Vector2 _mouseAt;

    /// <summary>The key pressed this frame, for a page that is listening for one.</summary>
    private InputKey _anyKey;

    /// <summary>And the pad button.</summary>
    private GamepadButton _anyButton;

    /// <summary>When the last frame was, for moving the cursor at a speed rather than a rate.</summary>
    private double _lastFrame;

    /// <summary>What the pointer is drawn as.</summary>
    private PointerShape _pointer = PointerShape.Default;

    /// <summary>How big, as a multiple of the usual.</summary>
    private float _pointerScale = 1f;

    /// <summary>The side of the pointer on screen now, or nought while it is the system's own.</summary>
    private int _pointerSize;

    /// <summary>The pictures already shrunk to the size in use, one a shape.</summary>
    private readonly Dictionary<PointerShape, PointerImage?> _pointerImages = [];

    /// <summary>
    /// Whether the platform refused a picture. Once it has, the system's own arrow is left
    /// alone rather than asked for again every frame.
    /// </summary>
    private bool _pointerRefused;

    /// <summary>Whether the mouse is pinned and hidden for looking about.</summary>
    private bool _pointerLocked;

    /// <summary>Whether the platform refused to pin it, in which case nothing is asked again.</summary>
    private bool _lockRefused;

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
    /// <param name="visible">
    /// Whether to put it on screen at once. False for a window whose first frame is seconds
    /// away — see <see cref="Show"/>, which is what puts it up.
    /// </param>
    /// <returns>The window.</returns>
    public static SilkGameWindow Open(
        string title,
        int width = 1280,
        int height = 720,
        WindowGraphics graphics = WindowGraphics.Vulkan,
        bool visible = true)
    {
        WindowOptions options = WindowOptions.DefaultVulkan with
        {
            Title = title,
            Size = new Vector2D<int>(width, height),
            API = graphics == WindowGraphics.Vulkan ? GraphicsAPI.DefaultVulkan : GraphicsAPI.None,
            IsVisible = visible,
        };

        IWindow window = Window.Create(options);
        window.Initialize();

        var created = new SilkGameWindow(window);
        created.AttachInput();

        return created;
    }

    /// <summary>Puts a window opened hidden on screen.</summary>
    public void Show() => _window.IsVisible = true;

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
    public nint WindowHandle => _window.Native?.Win32?.Hwnd ?? 0;

    /// <inheritdoc/>
    public PointerShape PointerShape
    {
        get => _pointer;
        set
        {
            if (_pointer != value)
            {
                _pointer = value;
                ShowPointer();
            }
        }
    }

    /// <inheritdoc/>
    public float PointerScale
    {
        get => _pointerScale;
        set
        {
            float wanted = float.IsFinite(value) && value > 0f ? value : 1f;

            if (Math.Abs(wanted - _pointerScale) > 0.0001f)
            {
                _pointerScale = wanted;
                ShowPointer();
            }
        }
    }

    /// <summary>
    /// Hands the platform the picture for the shape at the size the framebuffer asks for.
    /// A picture is shrunk once a size and kept; a new size throws the old ones away,
    /// because a resize is rare and five pictures at every size ever seen is a leak.
    /// </summary>
    private void ShowPointer()
    {
        if (_mouse is null || _pointerRefused)
        {
            return;
        }

        int size = PointerArt.SizeFor(FramebufferHeight, _pointerScale);

        if (size != _pointerSize)
        {
            _pointerImages.Clear();
        }

        if (!_pointerImages.TryGetValue(_pointer, out PointerImage? image))
        {
            image = PointerArt.Load(_pointer, size);
            _pointerImages[_pointer] = image;
        }

        ICursor cursor = _mouse.Cursor;

        try
        {
            if (image is null)
            {
                cursor.Type = CursorType.Standard;
                cursor.StandardCursor = StandardCursor.Default;
                _pointerSize = 0;

                return;
            }

            // Back to the platform's own first, so that the three properties below are
            // three assignments rather than three rebuilds of a cursor the platform
            // remakes every time one of them changes.
            cursor.Type = CursorType.Standard;
            cursor.Image = new RawImage(image.Width, image.Height, image.Pixels);
            cursor.HotspotX = image.HotspotX;
            cursor.HotspotY = image.HotspotY;
            cursor.Type = CursorType.Custom;
            _pointerSize = size;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A platform that will not take a picture - a compositor with no cursor
            // protocol, a size it refuses - is left with its own arrow. Said once, because
            // a game that runs is better than one that insists.
            _pointerRefused = true;
            _pointerSize = 0;
            Log.Warning($"Pointer: the platform refused a custom cursor, keeping its own ({error.Message})");

            try
            {
                cursor.Type = CursorType.Standard;
                cursor.StandardCursor = StandardCursor.Default;
            }
            catch (Exception again) when (again is not OutOfMemoryException)
            {
                // Nothing left to fall back to.
            }
        }
    }

    /// <inheritdoc/>
    public Vector2 PointerDelta => _pointerDelta;

    /// <inheritdoc/>
    public Vector2 PointerPosition => _lastPointer;

    /// <inheritdoc/>
    public bool WasClicked(PointerButton button) => _clicked.Contains(button);

    /// <summary>
    /// Presses a pointer button for the frame that has just begun, as if a mouse had.
    /// </summary>
    /// <param name="button">Which button.</param>
    public void Press(PointerButton button) => _clicked.Add(button);

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
        _clicked.Contains(button) ||
        (_mouse is not null &&
         _mouse.IsButtonPressed(button switch
         {
             PointerButton.Secondary => MouseButton.Right,
             PointerButton.Middle => MouseButton.Middle,
             _ => MouseButton.Left,
         }));

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
    public float PointerSpeed { get; set; } = 1200f;

    /// <inheritdoc/>
    public bool PointerLocked
    {
        get => _pointerLocked;

        set
        {
            if (_pointerLocked == value || _mouse is null || _lockRefused)
            {
                return;
            }

            try
            {
                _mouse.Cursor.CursorMode = value ? CursorMode.Raw : CursorMode.Normal;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Some platforms have no raw motion. Hiding and pinning is the whole of
                // what is needed; a difference in position works either way.
                try
                {
                    _mouse.Cursor.CursorMode = value ? CursorMode.Disabled : CursorMode.Normal;
                }
                catch (Exception again) when (again is not OutOfMemoryException)
                {
                    _lockRefused = true;
                    Log.Warning(
                        $"Pointer: the platform will not pin the cursor, so first person " +
                        $"is looked around with a button held ({again.Message})");

                    return;
                }
            }

            _pointerLocked = value;

            // Handed back in the middle of the window, which is where the crosshair was and
            // so where whatever has just opened is anchored.
            if (!value)
            {
                _lastPointer = new Vector2(_window.Size.X / 2f, _window.Size.Y / 2f);
                _mouse.Position = _lastPointer;
            }

            // Wherever the mouse now reads, that is the mark the next frame's movement is
            // measured from: pinning it moves it, and that move is not the player's.
            _mouseAt = new Vector2(_mouse.Position.X, _mouse.Position.Y);
            _pointerDelta = Vector2.Zero;
        }
    }

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

        // The pointer keeps its proportion to the framebuffer, so a window that has just
        // been resized or moved to a sharper monitor gets it remade at the new size.
        if (_pointerSize != 0 && _pointerSize != PointerArt.SizeFor(FramebufferHeight, _pointerScale))
        {
            ShowPointer();
        }

        // Pointer movement is tracked by difference rather than through the move event,
        // because raw motion is not delivered on every backend and a difference works the
        // same everywhere.
        if (_mouse is null)
        {
            return;
        }

        var position = new Vector2(_mouse.Position.X, _mouse.Position.Y);

        // Pinned for looking about: every movement is a turn and none of it is a place on
        // screen, so the difference is reported and the pointer itself is left standing.
        if (_pointerLocked)
        {
            _pointerDelta += position - _mouseAt;
            _mouseAt = position;
            _hasPointer = true;

            return;
        }

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
    private void AttachInput()
    {
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards.Count > 0 ? _input.Keyboards[0] : null;
        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;

        if (_mouse is not null)
        {
            // The other four pictures are decoded off this thread, so the first time the
            // pointer crosses a noun it changes shape at once rather than after a PNG.
            // The arrow is wanted now and is decoded here.
            _ = Task.Run(PointerArt.Warm);
            ShowPointer();

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
