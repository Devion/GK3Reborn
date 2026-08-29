using GK3Reborn.Game;

namespace GK3Reborn.UI;

/// <summary>
/// How tall the interface's letters should be for a given window.
/// </summary>
/// <remarks>
/// <para>
/// One rule, in one place, because there are three things that have to agree about it: the
/// atlas the room's captions are cut at, the larger one the menu is cut at, and the whole
/// number a bitmap sheet has to be magnified by to reach either. Two of those used to be
/// private to the host, which made the one question a player can actually ask about them —
/// "why is the menu this size on my monitor" — impossible to answer by test.
/// </para>
/// <para>
/// Nothing here touches a device, a font or an atlas. It is arithmetic on a window height
/// and a preference, which is the whole reason it can be checked.
/// </para>
/// </remarks>
public static class TextSizing
{
    /// <summary>How tall an em should be, in pixels, for a window of a given height.</summary>
    /// <param name="framebufferHeight">How tall the window is.</param>
    /// <param name="menu">Whether this is the menu rather than the room's interface.</param>
    /// <param name="scale">The player's text size, one for the automatic one.</param>
    /// <returns>The em size to draw the outline font at.</returns>
    /// <remarks>
    /// <para>
    /// A share of the window rather than a fixed size, so the interface is the same
    /// apparent size on every display. The menu is drawn larger than the room's captions
    /// on purpose: captions must not cover the room, and a menu is the only thing on
    /// screen.
    /// </para>
    /// <para>
    /// <b>And capped.</b> A share of the window is the right rule up to about a 1440-line
    /// display and stops being right above it: a twenty-sixth of a 4K screen is 83 pixels,
    /// which is nobody's idea of a settings page. A window is made bigger to see more of
    /// the game, not to have the menu grow with it. Thirty-six pixels is roughly where a
    /// line of a settings page stops getting easier to read and starts crowding the page —
    /// which, with a picture page now carrying a dozen rows, it visibly did.
    /// </para>
    /// <para>
    /// <b>Then the player's own correction, after the cap rather than before it.</b> The
    /// cap is what decides a large window's size, so a multiplier applied ahead of it would
    /// be swallowed whole on exactly the displays somebody would be dragging that row on:
    /// three quarters of a twenty-sixth of 4K is 62 pixels, which caps to the same 36 the
    /// player was complaining about. The result is bounded again, because the ends of the
    /// slider times the ends of the cap still have to be a size a face can be cut at.
    /// </para>
    /// </remarks>
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
    /// <remarks>
    /// Proportional to the display rather than fixed, because a bitmap font does not scale:
    /// the same 17-pixel sheet that is comfortable on a 480-line screen is a third the
    /// apparent size on a 1440-line one, which is exactly the complaint. 2.8% puts a
    /// 1080-line display on the 26-point rung and anything above it on the 26-point rung
    /// too, that being the largest the game shipped.
    /// </remarks>
    public static int Sheet(int framebufferHeight, float scale = 1f) =>
        Math.Max(12, (int)MathF.Round(Math.Max(1, framebufferHeight) * 0.028f * Sane(scale)));

    /// <summary>How much to magnify a bitmap sheet by to draw the menu with it.</summary>
    /// <param name="framebufferHeight">How tall the window is, in pixels.</param>
    /// <param name="glyphHeight">How tall the sheet's letters are.</param>
    /// <param name="scale">The player's text size, one for the automatic one.</param>
    /// <returns>A whole-number magnification, one or more.</returns>
    /// <remarks>
    /// <para>
    /// A menu is not a caption. Captions are sized to be readable without covering the
    /// room; a menu is the only thing on screen, and one drawn at caption size on a large
    /// display reads as a dialogue box from another decade. A row comes out at about a
    /// twenty-second of the window's height, which is roughly what the original's own
    /// buttons were on the screen they were drawn for.
    /// </para>
    /// <para>
    /// The text size moves that coarsely: a sheet can only be magnified by whole numbers,
    /// so under <c>--bitmap-font</c> the row is a step rather than a slider. It is still
    /// the right place for it — the alternative is a setting that silently does nothing at
    /// all on the one font mode where the letters cannot be re-cut.
    /// </para>
    /// </remarks>
    public static int MenuMagnification(int framebufferHeight, int glyphHeight, float scale = 1f) =>
        Math.Max(
            1,
            (int)MathF.Round(framebufferHeight * Sane(scale) / 22f / Math.Max(1, glyphHeight)));

    /// <summary>The player's text size, as a number these rules can be trusted with.</summary>
    /// <param name="scale">What the settings say, or whatever was in the file.</param>
    /// <returns>A finite multiplier between the slider's two ends.</returns>
    /// <remarks>
    /// <see cref="Settings.Sane"/> clamps this on the way in and on the way out, so a sane
    /// run never needs this. A run started with a hand-written settings file does, and the
    /// failure without it is an atlas cut at nought pixels — a game with no interface at
    /// all rather than one with an ugly menu.
    /// </remarks>
    public static float Sane(float scale) => float.IsFinite(scale)
        ? Math.Clamp(scale, Settings.SmallestText, Settings.LargestText)
        : 1f;
}
