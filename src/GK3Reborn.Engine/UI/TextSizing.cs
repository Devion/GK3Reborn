using GK3Reborn.Game;

namespace GK3Reborn.UI;

/// <summary>
/// How tall the interface's letters should be for a given window.
/// </summary>
public static class TextSizing
{
    /// <summary>How tall an em should be, in pixels, for a window of a given height.</summary>
    /// <param name="framebufferHeight">How tall the window is.</param>
    /// <param name="menu">Whether this is the menu rather than the room's interface.</param>
    /// <param name="scale">The player's text size, one for the automatic one.</param>
    /// <returns>The em size to draw the outline font at.</returns>
    public static int Em(int framebufferHeight, bool menu, float scale = 1f)
    {
        int automatic = Math.Clamp(
            (int)MathF.Round(Math.Max(1, framebufferHeight) / (menu ? 26f : 33f)),
            menu ? 16 : 12,
            menu ? 36 : 30);

        return Math.Clamp((int)MathF.Round(automatic * Sane(scale)), 8, 64);
    }

    /// <summary>Which rung of GK3's own font ladder to ask for.</summary>
    /// <param name="framebufferHeight">The framebuffer's height in pixels.</param>
    /// <param name="scale">The player's text size, one for the automatic one.</param>
    /// <returns>A wanted glyph height, which the ladder is matched against.</returns>
    public static int Sheet(int framebufferHeight, float scale = 1f) =>
        Math.Max(12, (int)MathF.Round(Math.Max(1, framebufferHeight) * 0.028f * Sane(scale)));

    /// <summary>How much to magnify a bitmap sheet by to draw the menu with it.</summary>
    /// <param name="framebufferHeight">How tall the window is, in pixels.</param>
    /// <param name="glyphHeight">How tall the sheet's letters are.</param>
    /// <param name="scale">The player's text size, one for the automatic one.</param>
    /// <returns>A whole-number magnification, one or more.</returns>
    public static int MenuMagnification(int framebufferHeight, int glyphHeight, float scale = 1f) =>
        Math.Max(
            1,
            (int)MathF.Round(framebufferHeight * Sane(scale) / 22f / Math.Max(1, glyphHeight)));

    /// <summary>The player's text size, as a number these rules can be trusted with.</summary>
    /// <param name="scale">What the settings say, or whatever was in the file.</param>
    /// <returns>A finite multiplier between the slider's two ends.</returns>
    public static float Sane(float scale) => float.IsFinite(scale)
        ? Math.Clamp(scale, Settings.SmallestText, Settings.LargestText)
        : 1f;
}
