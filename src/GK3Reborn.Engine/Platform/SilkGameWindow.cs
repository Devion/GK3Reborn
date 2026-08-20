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
public sealed class SilkGameWindow : IGameWindow, IVulkanSurfaceSource, IGameInput
{
    private static readonly Dictionary<CameraAction, Key[]> Bindings = new()
    {
        [CameraAction.Forward] = [Key.W, Key.Up],
        [CameraAction.Back] = [Key.S, Key.Down],
        [CameraAction.Left] = [Key.A, Key.Left],
        [CameraAction.Right] = [Key.D, Key.Right],
        [CameraAction.Up] = [Key.E, Key.Space],
        [CameraAction.Down] = [Key.Q, Key.ControlLeft],
        [CameraAction.Fast] = [Key.ShiftLeft, Key.ShiftRight],
        [CameraAction.Reset] = [Key.R],
        [CameraAction.NextCamera] = [Key.Tab],
        [CameraAction.CycleRayTracing] = [Key.F2],
        [CameraAction.Quit] = [Key.Escape],
    };

    private readonly IWindow _window;
    /// <summary>How far the pointer may travel between press and release and still be a click.</summary>
    private const float DragThreshold = 4f;

    private readonly HashSet<CameraAction> _pressed = [];
    private readonly HashSet<PointerButton> _clicked = [];
    private IInputContext? _input;
    private IKeyboard? _keyboard;
    private IMouse? _mouse;
    private Vector2 _pointerDelta;
    private Vector2 _lastPointer;
    private Vector2 _pressedAt;
    private bool _hasPointer;

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
    /// <returns>The window.</returns>
    public static SilkGameWindow Open(string title, int width = 1280, int height = 720)
    {
        WindowOptions options = WindowOptions.DefaultVulkan with
        {
            Title = title,
            Size = new Vector2D<int>(width, height),
            API = GraphicsAPI.DefaultVulkan,
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
    public Vector2 PointerDelta => _pointerDelta;

    /// <inheritdoc/>
    public Vector2 PointerPosition => _lastPointer;

    /// <inheritdoc/>
    public bool WasClicked(PointerButton button) => _clicked.Contains(button);

    /// <inheritdoc/>
    public bool IsDragging =>
        _mouse is not null &&
        (_mouse.IsButtonPressed(MouseButton.Left) || _mouse.IsButtonPressed(MouseButton.Right));

    /// <inheritdoc/>
    public bool IsHeld(CameraAction action) =>
        _keyboard is not null &&
        Bindings.TryGetValue(action, out Key[]? keys) &&
        Array.Exists(keys, _keyboard.IsKeyPressed);

    /// <inheritdoc/>
    public bool WasPressed(CameraAction action) => _pressed.Contains(action);

    /// <inheritdoc/>
    public void EndFrame()
    {
        _pressed.Clear();
        _clicked.Clear();
        _pointerDelta = Vector2.Zero;
    }

    /// <inheritdoc/>
    public void PumpEvents()
    {
        _window.DoEvents();

        // Pointer movement is tracked by difference rather than through the move event,
        // because raw motion is not delivered on every backend and a difference works the
        // same everywhere.
        if (_mouse is not null)
        {
            var position = new Vector2(_mouse.Position.X, _mouse.Position.Y);

            if (_hasPointer)
            {
                _pointerDelta += position - _lastPointer;
            }

            _lastPointer = position;
            _hasPointer = true;
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

            _mouse.MouseUp += (_, mouseButton) =>
            {
                var at = new Vector2(_mouse.Position.X, _mouse.Position.Y);

                if ((at - _pressedAt).Length() > DragThreshold)
                {
                    return;
                }

                if (mouseButton == MouseButton.Left)
                {
                    _clicked.Add(PointerButton.Primary);
                }
                else if (mouseButton == MouseButton.Right)
                {
                    _clicked.Add(PointerButton.Secondary);
                }
            };
        }

        if (_keyboard is not null)
        {
            _keyboard.KeyDown += (_, key, _) =>
            {
                foreach ((CameraAction action, Key[] keys) in Bindings)
                {
                    if (Array.IndexOf(keys, key) >= 0)
                    {
                        _pressed.Add(action);
                    }
                }
            };
        }
    }
}
