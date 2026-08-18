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
}
