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
public sealed class SilkGameWindow : IGameWindow, IVulkanSurfaceSource
{
    private readonly IWindow _window;

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

        return new SilkGameWindow(window);
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
    public void PumpEvents() => _window.DoEvents();

    /// <inheritdoc/>
    public void Dispose() => _window.Dispose();
}
