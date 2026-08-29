namespace GK3Reborn.Platform;

/// <summary>Presentation mode of the game window.</summary>
public enum WindowMode
{
    /// <summary>A resizable window.</summary>
    Windowed,

    /// <summary>A borderless window covering the chosen monitor.</summary>
    BorderlessFullscreen,

    /// <summary>Exclusive fullscreen at a chosen video mode.</summary>
    ExclusiveFullscreen,
}

/// <summary>
/// The window and its surface, abstracted so Silk.NET windowing or SDL can back it.
/// </summary>
/// <remarks>
/// Plan/01-architecture.md section 2 keeps windowing behind an interface precisely so
/// the backend choice can change after the Windows and Linux DPI, IME, controller and
/// raw-mouse proofs. Nothing above this layer may reference a windowing library.
/// </remarks>
public interface IGameWindow : IDisposable
{
    /// <summary>Framebuffer width in physical pixels.</summary>
    int FramebufferWidth { get; }

    /// <summary>Framebuffer height in physical pixels.</summary>
    int FramebufferHeight { get; }

    /// <summary>Ratio of physical pixels to logical UI units on the current monitor.</summary>
    float DpiScale { get; }

    /// <summary>Current presentation mode.</summary>
    WindowMode Mode { get; }

    /// <summary>Raised after the framebuffer size changes, including DPI transitions.</summary>
    event Action<int, int>? Resized;

    /// <summary>Pumps platform events once.</summary>
    void PumpEvents();

    /// <summary>
    /// Puts the window into a mode, at a size.
    /// </summary>
    /// <param name="mode">Windowed, borderless over the monitor, or fullscreen.</param>
    /// <param name="width">Width in logical pixels, or nought for the monitor's own.</param>
    /// <param name="height">Height, or nought.</param>
    /// <remarks>
    /// <para>
    /// A size is only meaningful for two of the three: a borderless window is the size of
    /// the monitor by definition, and asking for anything else would make it a large window
    /// with no border, which is not what anybody means by the phrase.
    /// </para>
    /// <para>
    /// Implementations must be safe to call with what is already true, because the settings
    /// are applied whenever anything on the page changes and most of those changes are not
    /// this one.
    /// </para>
    /// </remarks>
    void Present(WindowMode mode, int width = 0, int height = 0);
}
