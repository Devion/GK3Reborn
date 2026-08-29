using System.Globalization;
using System.Numerics;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI;

/// <summary>What is behind a page of menu.</summary>
public enum MenuBehind
{
    /// <summary>A picture of its own, which must not be washed over.</summary>
    Picture,

    /// <summary>Nothing, so the page draws its own screen.</summary>
    Nothing,

    /// <summary>The room the player is in, dimmed enough to read the page against.</summary>
    Room,
}

/// <summary>What kind of thing a row on a menu page is.</summary>
public enum MenuItemKind
{
    /// <summary>Press it and something happens.</summary>
    Button,

    /// <summary>On or off.</summary>
    Toggle,

    /// <summary>One of a short list, stepped left and right.</summary>
    Choice,

    /// <summary>A number between two others, dragged.</summary>
    Slider,

    /// <summary>Not selectable: a heading or a line of explanation.</summary>
    Label,
}

/// <summary>One row of a menu page.</summary>
/// <param name="Id">What the page's owner calls it.</param>
/// <param name="Kind">What sort of row it is.</param>
/// <param name="Text">Its label.</param>
/// <param name="Value">
/// What it currently reads: "On", "High", "70%". Empty for a button or a label.
/// </param>
/// <param name="Fraction">
/// Where a slider sits, from zero to one. Ignored by everything else.
/// </param>
/// <param name="Enabled">
/// Whether it can be used. A disabled row is drawn and skipped, which is how the menu says
/// "this exists and is not available" rather than hiding it and leaving the player looking.
/// </param>
/// <param name="Picture">
/// The interface's number for a picture drawn beside the row, or nought for none. What a
/// save slot shows of the room it was written in.
/// </param>
public readonly record struct MenuItem(
    string Id,
    MenuItemKind Kind,
    string Text,
    string Value = "",
    float Fraction = 0f,
    bool Enabled = true,
    int Picture = 0)
{
    /// <summary>A row that does something.</summary>
    public static MenuItem Button(string id, string text, bool enabled = true) =>
        new(id, MenuItemKind.Button, text, Enabled: enabled);

    /// <summary>A row that is on or off.</summary>
    public static MenuItem Toggle(string id, string text, bool on) =>
        new(id, MenuItemKind.Toggle, text, on ? "On" : "Off");

    /// <summary>A row that steps through a list.</summary>
    public static MenuItem Choice(string id, string text, string value) =>
        new(id, MenuItemKind.Choice, text, value);

    /// <summary>A row that is a number.</summary>
    public static MenuItem Slider(string id, string text, float fraction, string value) =>
        new(id, MenuItemKind.Slider, text, value, Math.Clamp(fraction, 0f, 1f));

    /// <summary>A row that is only words.</summary>
    public static MenuItem Label(string text) =>
        new(string.Empty, MenuItemKind.Label, text, Enabled: false);

    /// <summary>Whether the player can land on this row.</summary>
    public bool Selectable => Kind != MenuItemKind.Label && Enabled;
}

/// <summary>What the player did to a menu.</summary>
/// <param name="Id">Which row, or empty for none.</param>
/// <param name="Step">
/// -1 or 1 when a choice or slider was moved, 0 when a row was simply chosen.
/// </param>
/// <param name="Fraction">Where a slider was dragged to, or -1 when it was not dragged.</param>
public readonly record struct MenuAction(string Id, int Step = 0, float Fraction = -1f)
{
    /// <summary>Nothing happened.</summary>
    public static MenuAction None => new(string.Empty);

    /// <summary>Whether anything happened.</summary>
    public bool Happened => Id.Length > 0;

    /// <summary>Whether a slider was dragged rather than stepped.</summary>
    public bool Dragged => Fraction >= 0f;
}

/// <summary>
/// A page of menu, drawn from rectangles and text rather than from the game's own art.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here uses GK3's interface bitmaps.</b> They are 640x480 art with the button
/// labels painted into them in one language, at one size, with the pressed and hovered
/// states baked as separate images. Drawing the interface instead means it is sharp at any
/// resolution, it grows with the caption ladder like the rest of the interface, it can say
/// things the original never had a button for — a volume, a walking pace — and it needs no
/// new art to add a row.
/// </para>
/// <para>
/// The layout and the hit test come from one pass, in the same way <see cref="GameHud"/>
/// does its verb menu: a row is drawn and its rectangle remembered, so what the player
/// clicks is necessarily what they saw. The two cannot drift apart because there is only
/// one of them.
/// </para>
/// <para>
/// Every measurement is in <see cref="Overlay.LineHeight"/>, so the whole page scales with
/// the font the interface picked for the window. Nothing is in pixels.
/// </para>
/// </remarks>
public sealed class MenuPage
{
    /// <summary>The panel behind the page.</summary>
    private static readonly Vector4 Panel = new(0.06f, 0.07f, 0.09f, 0.93f);

    /// <summary>A row the pointer is over or the keyboard has landed on.</summary>
    private static readonly Vector4 Chosen = new(0.20f, 0.23f, 0.29f, 1f);

    /// <summary>The bar down the side of the chosen row, and the title.</summary>
    private static readonly Vector4 Accent = new(0.85f, 0.68f, 0.36f, 1f);

    private static readonly Vector4 Ink = new(0.86f, 0.87f, 0.90f, 1f);
    private static readonly Vector4 Dim = new(0.52f, 0.55f, 0.60f, 1f);
    private static readonly Vector4 Rule = new(0.24f, 0.26f, 0.31f, 1f);
    private static readonly Vector4 Track = new(0.16f, 0.18f, 0.22f, 1f);

    /// <summary>A band to read light letters against, over a painting of any colour.</summary>
    private static readonly Vector4 Shade = new(0f, 0f, 0f, 0.62f);

    /// <summary>
    /// Where each drawn row went, and which item it was.
    /// </summary>
    /// <remarks>
    /// The item's own index is kept because a page that scrolls does not draw its first
    /// row first: the pointer lands on the fourth thing drawn and that may be the
    /// seventeenth setting.
    /// </remarks>
    private readonly List<(int At, string Id, Vector4 Bounds, MenuItemKind Kind)> _rows = [];

    /// <summary>Each label's text, already broken to fit the panel.</summary>
    private readonly List<string[]> _wrapped = [];

    private Vector4 _panel;

    /// <summary>Which item is the first one drawn, when the page is taller than the window.</summary>
    private int _scroll;

    /// <summary>Creates a page.</summary>
    /// <param name="overlay">Where it draws.</param>
    public MenuPage(Overlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        Overlay = overlay;
    }

    /// <summary>The display list it draws into.</summary>
    public Overlay Overlay { get; private set; }

    /// <summary>Draws with a different sheet of letters.</summary>
    /// <param name="atlas">The new one.</param>
    /// <remarks>
    /// For a window that changed size. An outline font is re-cut at the new size rather
    /// than magnified, which is the whole reason for having one.
    /// </remarks>
    public void Retarget(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);

        Overlay = new Overlay(atlas);
        _rows.Clear();
    }

    /// <summary>Which row is chosen, by index into the items last drawn.</summary>
    public int Index { get; private set; }

    /// <summary>How many rows the last page had.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// What is behind the page, and therefore what it has to draw itself.
    /// </summary>
    /// <remarks>
    /// The game's own title art is a picture and is left alone: a wash over it to make the
    /// rows readable would be a wash over the thing the player came to look at, and the
    /// panel behind the rows already carries that. Without the art there is nothing at all
    /// behind the page, so it draws its own screen from rectangles. Over a room, a dim.
    /// </remarks>
    public MenuBehind Behind { get; set; } = MenuBehind.Room;

    /// <summary>
    /// Where down the window the page sits, from zero at the top to one at the bottom.
    /// </summary>
    /// <remarks>
    /// Low over the title art, which has the game's name across the middle of it and should
    /// not be covered by its own menu. Centred everywhere else.
    /// </remarks>
    public float Down { get; set; } = 0.5f;

    /// <summary>
    /// Where across the window the page sits, from zero at the left to one at the right.
    /// </summary>
    /// <remarks>
    /// Left over the title art, whose lettering is to the right of the angel. A menu that
    /// covers the name of the game it is the menu for is not a title screen.
    /// </remarks>
    public float Across { get; set; } = 0.5f;

    /// <summary>One row's height, which is what everything else is measured in.</summary>
    private float Row => Overlay.LineHeight * (Overlay.Atlas.Scalable ? 1.5f : 1.9f);

    /// <summary>How much of the window a page's preview takes across.</summary>
    /// <remarks>
    /// A third of it. The first attempt drew a picture the height of a line of text beside
    /// every row, which is small enough to be worse than nothing — a room is recognised by
    /// its shape and its colour, and neither survives being a centimetre wide. One picture,
    /// of the row the player is on, big enough to answer "is this the save I mean".
    /// </remarks>
    private const float PreviewWide = 0.34f;

    /// <summary>Draws a page and remembers where every row went.</summary>
    /// <param name="title">The heading.</param>
    /// <param name="items">The rows.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <param name="at">Where the pointer is, for the hover.</param>
    /// <remarks>
    /// There is no line along the bottom saying which keys work. A menu that tells the
    /// player what an arrow key does is a menu that thinks they have not used one.
    /// </remarks>
    public void Build(
        string title,
        IReadOnlyList<MenuItem> items,
        int width,
        int height,
        Vector2 at)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(items);

        Overlay.Begin(width, height);

        _rows.Clear();
        Count = items.Count;
        Index = Math.Clamp(Index, 0, Math.Max(0, items.Count - 1));

        Screen(width, height);

        float unit = Overlay.LineHeight;
        float pad = unit;
        float row = Row;

        // The game's own name, on the screen it opens with, at twice the size of a row.
        // A title screen whose title is the same size as its buttons does not read as one.
        int ink = Overlay.Magnify;
        int large = Behind == MenuBehind.Room ? ink : ink * 2;
        float titleUnit = unit * large / ink;

        // Wide enough for the longest thing on the page, and never wider than the window
        // has room for. A page of short labels should not be a page-wide slab.
        //
        // Labels are deliberately not measured here. They are sentences of explanation and
        // the longest of them would make the panel as wide as the window on every page that
        // has one; they are wrapped to whatever width the rows settle on instead.
        float widest = title.Length > 0 ? Overlay.Measure(title) * large / (float)ink : 0;

        bool illustrated = false;

        foreach (MenuItem item in items)
        {
            if (item.Kind != MenuItemKind.Label)
            {
                widest = Math.Max(
                    widest,
                    Overlay.Measure(item.Text) + Overlay.Measure("    ") +
                    Overlay.Measure(item.Value));
            }

            illustrated |= item.Picture > 0;
        }

        // A page whose rows carry pictures shows one of them, large, beside the list, so
        // the panel moves out of the middle to leave room for it.
        float across = illustrated ? 0.30f : Math.Clamp(Across, 0f, 1f);

        // A page with a heading is at least wide enough to look like one; a page without
        // is a column of short words and should be no wider than it needs.
        float least = (title.Length > 0 ? 22 : 12) * unit;

        // Never wider than the window, and never so wide it becomes the window: a settings
        // panel that reaches both edges of a 4K monitor is a page whose rows are a metre
        // apart. Forty-four ems is about the width a line of prose is comfortable at.
        float panelWidth = Math.Min(
            Math.Min(width - (4 * unit), 44 * unit),
            Math.Max(widest + (6 * unit), least));

        panelWidth = Math.Max(panelWidth, Math.Min(width - (4 * unit), least));

        // No heading where the art already carries the game's name. Drawing "Gabriel
        // Knight 3" over a picture that says Gabriel Knight is how a menu looks like a
        // placeholder.
        float titleHeight = title.Length > 0 ? row + (titleUnit - unit) : pad / 2f;

        // The explanations, broken to the width the panel came out at. Done before the
        // heights are worked out, because a sentence that takes two lines takes two lines
        // of room.
        float inner = panelWidth - (2 * pad);
        float labelLine = unit * 1.25f;

        _wrapped.Clear();

        foreach (MenuItem item in items)
        {
            _wrapped.Add(
                item.Kind == MenuItemKind.Label ? Wrap(item.Text, inner) : []);
        }

        // How tall each row is. Everything is one row high except an explanation, which is
        // as tall as the number of lines it broke into — so a page never has a sentence
        // running off the side of it, and never has one silently truncated either.
        float[] heights = new float[items.Count];

        for (int i = 0; i < items.Count; i++)
        {
            heights[i] = items[i].Kind == MenuItemKind.Label
                ? MathF.Max(1, _wrapped[i].Length) * labelLine
                : row;
        }

        // What is left of the window once the panel's margins and its heading are taken
        // off. The page is fitted into this rather than allowed to run past it.
        float available = Math.Max(row, height - (4 * unit) - titleHeight - pad);

        // Which rows are drawn. Everything, when everything fits; otherwise a window of
        // them that contains the one the player is on.
        //
        // This used to squeeze the rows instead — closing the spacing until the whole page
        // fitted — which works for a save menu of fifteen short rows and does not work for
        // a picture page with a dozen settings and six lines of explanation. Past a certain
        // number of rows there is no spacing at which they fit, and the page simply ran off
        // the bottom of the window with the last few settings unreachable.
        (int first, int shown, float used) = Window(heights, available, Index);

        _scroll = first;

        bool scrolls = shown < items.Count;
        float panelHeight = titleHeight + used + pad;

        float x = MathF.Round(Math.Clamp(
            (width * across) - (panelWidth / 2f),
            unit,
            Math.Max(unit, width - panelWidth - unit)));

        // Kept on the screen whatever it was asked for: a page taller than the window it
        // was told to sit low in would otherwise run off the bottom.
        float y = MathF.Round(Math.Clamp(
            (height * Math.Clamp(Down, 0f, 1f)) - (panelHeight / 2f),
            unit,
            Math.Max(unit, height - panelHeight - unit)));

        _panel = new Vector4(x, y, panelWidth, panelHeight);

        Overlay.Rect(x, y, panelWidth, panelHeight, Panel);
        Overlay.Rect(x, y, panelWidth, 2, Accent);

        if (title.Length > 0)
        {
            Overlay.Magnify = large;

            Overlay.Text(
                title,
                MathF.Round(x + ((panelWidth - Overlay.Measure(title)) / 2f)),
                MathF.Round(y + ((titleHeight - titleUnit) / 2f)),
                Accent);

            Overlay.Magnify = ink;
            Overlay.Rect(x, y + titleHeight, panelWidth, 1, Rule);
        }

        // The pointer picks the row before anything is drawn, so hovering and the keyboard
        // land on the same row and the drawn highlight is the one that will be clicked.
        int hovered = RowUnder(
            at, x, y + titleHeight, panelWidth, heights, items, first, shown);

        if (hovered >= 0)
        {
            Index = hovered;
        }

        // The picture belonging to the row the player is on, large, beside the list. One of
        // them rather than one per row: a room is recognised by its shape and its colour, and
        // neither survives being drawn the height of a line of text.
        if (illustrated &&
            Index >= 0 && Index < items.Count &&
            items[Index].Picture > 0)
        {
            float wide = MathF.Round(width * PreviewWide);
            float tall = MathF.Round(wide * 3f / 4f);

            float left = MathF.Round(Math.Min(
                x + panelWidth + (unit * 2f), width - wide - (unit * 2f)));

            float topOf = MathF.Round(y + titleHeight);

            // A border, so a dark room reads as a picture rather than as a hole.
            Overlay.Rect(left - 2, topOf - 2, wide + 4, tall + 4, Rule);
            Overlay.Picture(items[Index].Picture, left, topOf, wide, tall, Vector4.One);
        }

        float top = y + titleHeight;

        for (int i = first; i < first + shown && i < items.Count; i++)
        {
            MenuItem item = items[i];

            Draw(item, i, x, top, panelWidth, heights[i], unit, labelLine, _wrapped[i]);

            _rows.Add((i, item.Id, new Vector4(x, top, panelWidth, heights[i]), item.Kind));

            top += heights[i];
        }

        if (scrolls)
        {
            Scrollbar(x, y + titleHeight, panelWidth, used, items.Count, first, shown);
        }
    }

    /// <summary>
    /// Which run of rows to draw, so that the page fits and the chosen row is on it.
    /// </summary>
    /// <param name="heights">How tall each row is.</param>
    /// <param name="available">How much room there is below the heading.</param>
    /// <param name="index">The row the player is on.</param>
    /// <returns>The first row drawn, how many, and how much room they take.</returns>
    /// <remarks>
    /// Grows the window downwards from the chosen row's position and then backwards, which
    /// is what keeps the selection from sitting against the top edge as soon as the player
    /// steps past the middle of a long list.
    /// </remarks>
    private static (int First, int Shown, float Used) Window(
        float[] heights, float available, int index)
    {
        if (heights.Length == 0)
        {
            return (0, 0, 0f);
        }

        float total = 0f;

        foreach (float height in heights)
        {
            total += height;
        }

        if (total <= available)
        {
            return (0, heights.Length, total);
        }

        int at = Math.Clamp(index, 0, heights.Length - 1);
        int first = at;
        int last = at;
        float used = heights[at];

        // Outwards from the chosen row, downwards first, so that stepping down a list
        // reveals what is coming rather than what has been passed.
        bool grew = true;

        while (grew)
        {
            grew = false;

            if (last + 1 < heights.Length && used + heights[last + 1] <= available)
            {
                used += heights[++last];
                grew = true;
            }

            if (first - 1 >= 0 && used + heights[first - 1] <= available)
            {
                used += heights[--first];
                grew = true;
            }
        }

        return (first, last - first + 1, used);
    }

    /// <summary>Draws the bar that says how much of a long page is showing.</summary>
    /// <remarks>
    /// A bar rather than arrows, because it says two things at once — that there is more,
    /// and how much more — and because it needs no target to click on: this page is stepped
    /// with the keyboard and the wheel, and a scroll bar nobody drags is a scroll bar that
    /// only has to be read.
    /// </remarks>
    private void Scrollbar(
        float x, float top, float panelWidth, float used, int count, int first, int shown)
    {
        float unit = Overlay.LineHeight;
        float thick = MathF.Max(2f, unit / 4f);
        float left = x + panelWidth - thick - (unit / 3f);

        Overlay.Rect(left, top, thick, used, Track);

        float part = shown / (float)Math.Max(1, count);
        float start = first / (float)Math.Max(1, count);

        Overlay.Rect(
            left,
            MathF.Round(top + (used * start)),
            thick,
            MathF.Max(unit / 2f, used * part),
            Accent);
    }

    /// <summary>Breaks a sentence into lines no wider than the panel.</summary>
    /// <param name="text">The sentence.</param>
    /// <param name="wide">How much room there is across.</param>
    /// <returns>The lines.</returns>
    /// <remarks>
    /// On spaces, and never mid-word: a word broken across two lines of a settings page is
    /// harder to read than a page that is one line taller. A single word wider than the
    /// panel is left to overhang, which cannot happen with any language this game is in and
    /// is a better failure than dropping it.
    /// </remarks>
    private string[] Wrap(string text, float wide)
    {
        if (text.Length == 0 || wide <= 0)
        {
            return [text];
        }

        if (Overlay.Measure(text) <= wide)
        {
            return [text];
        }

        List<string> lines = [];
        var line = new System.Text.StringBuilder();

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;

            if (line.Length > 0 && Overlay.Measure(candidate) > wide)
            {
                lines.Add(line.ToString());
                line.Clear();
                line.Append(word);
                continue;
            }

            line.Clear();
            line.Append(candidate);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines.Count > 0 ? [.. lines] : [text];
    }

    /// <summary>Moves the selection, skipping anything that cannot be landed on.</summary>
    /// <param name="items">The rows as last drawn.</param>
    /// <param name="by">-1 for up, 1 for down.</param>
    public void Move(IReadOnlyList<MenuItem> items, int by)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0 || by == 0)
        {
            return;
        }

        // Round the page rather than stopping at the ends: a menu is a ring, and a player
        // holding down a key should not have to notice where it started.
        for (int step = 0; step < items.Count; step++)
        {
            Index = ((Index + by) % items.Count + items.Count) % items.Count;

            if (items[Index].Selectable)
            {
                return;
            }
        }
    }

    /// <summary>Puts the selection on the first row that can be landed on.</summary>
    /// <param name="items">The rows.</param>
    public void Reset(IReadOnlyList<MenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Index = 0;

        if (items.Count > 0 && !items[0].Selectable)
        {
            Move(items, 1);
        }
    }

    /// <summary>What a click at a point means.</summary>
    /// <param name="point">Where the pointer is.</param>
    /// <param name="items">The rows as last drawn.</param>
    /// <returns>The action, or <see cref="MenuAction.None"/>.</returns>
    /// <remarks>
    /// A click on a slider is a drag to that position, because a player who clicks halfway
    /// along a volume bar means half. A click anywhere on a choice steps it forward, which
    /// makes the whole row a target rather than two small arrows.
    /// </remarks>
    public MenuAction Click(Vector2 point, IReadOnlyList<MenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach ((int at, string id, Vector4 bounds, MenuItemKind kind) in _rows)
        {
            if (at >= items.Count || !Inside(point, bounds) || !items[at].Selectable)
            {
                continue;
            }

            if (kind == MenuItemKind.Slider)
            {
                return new MenuAction(id, Fraction: FractionAt(point.X, bounds));
            }

            return new MenuAction(id, kind == MenuItemKind.Choice ? 1 : 0);
        }

        return MenuAction.None;
    }

    /// <summary>Where a drag across a slider row has reached.</summary>
    /// <param name="point">Where the pointer is.</param>
    /// <param name="items">The rows as last drawn.</param>
    /// <returns>The action, or none when the pointer is not on a slider.</returns>
    public MenuAction Drag(Vector2 point, IReadOnlyList<MenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (Index < 0 || Index >= items.Count)
        {
            return MenuAction.None;
        }

        // Found by item rather than taken at the same position, because a page that scrolls
        // draws the eleventh setting fourth. Dragging a slider off the top of a scrolled
        // page then moved a different one.
        foreach ((int at, string id, Vector4 bounds, MenuItemKind kind) in _rows)
        {
            if (at != Index)
            {
                continue;
            }

            return kind == MenuItemKind.Slider && items[Index].Selectable
                ? new MenuAction(id, Fraction: FractionAt(point.X, bounds))
                : MenuAction.None;
        }

        return MenuAction.None;
    }

    /// <summary>What choosing the current row means.</summary>
    /// <param name="items">The rows as last drawn.</param>
    /// <param name="step">-1 or 1 to step a choice or slider, 0 to press.</param>
    /// <returns>The action.</returns>
    public MenuAction Chose(IReadOnlyList<MenuItem> items, int step = 0)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (Index < 0 || Index >= items.Count || !items[Index].Selectable)
        {
            return MenuAction.None;
        }

        MenuItem item = items[Index];

        // Pressing a choice or a slider steps it forward, so the keyboard needs no separate
        // idea of what Enter does on a row that has no press.
        if (step == 0 && item.Kind is MenuItemKind.Choice or MenuItemKind.Slider)
        {
            step = 1;
        }

        return new MenuAction(item.Id, step);
    }

    /// <summary>
    /// Draws what a film says about being skipped.
    /// </summary>
    /// <param name="text">How to skip it, said once at the start.</param>
    /// <param name="part">
    /// How far through the hold the player is, zero to one. Zero draws the words instead.
    /// </param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <remarks>
    /// A hold rather than a click, because a click is what a player does by accident and
    /// losing the opening of the game to a stray mouse is worse than holding a button for
    /// half a second. A hold with nothing on screen is indistinguishable from a hold that
    /// is not working, so it fills a bar while it counts.
    /// </remarks>
    public void Skipping(string text, float part, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(text);

        Overlay.Begin(width, height);

        float unit = Overlay.LineHeight;
        float bar = unit * 10f;
        float y = MathF.Round(height - (unit * 3f));

        if (part <= 0f)
        {
            // Low and faint: it is over the opening of the game, and somebody watching it
            // should be able to ignore it.
            Overlay.Text(
                text, MathF.Round((width - Overlay.Measure(text)) / 2f), y, Dim);

            return;
        }

        float x = MathF.Round((width - bar) / 2f);
        float thick = MathF.Max(2f, unit / 3f);

        Overlay.Rect(x, y, bar, thick, Track);
        Overlay.Rect(x, y, bar * Math.Clamp(part, 0f, 1f), thick, Accent);
    }

    /// <summary>
    /// Names the part of the day the story has moved on to, across the middle of the screen.
    /// </summary>
    /// <param name="text">What this part of the day is called.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <remarks>
    /// Big, centred and over a band, because the whole point of it is to be impossible to
    /// miss: two hours of the story have just gone by. The band is there for the paintings
    /// with a bright sky in the middle of them, where white letters on their own would be
    /// unreadable.
    /// </remarks>
    public void Announcing(string text, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(text);

        Overlay.Begin(width, height);

        int was = Overlay.Magnify;

        // A line about a fifteenth of the screen tall, which is two to four times the size
        // the sheets were cut at. The point of the card is to be unmissable, and the menu's
        // own size is a size for reading a list of settings.
        Overlay.Magnify = Math.Max(
            2, (int)MathF.Round(height / 15f / Math.Max(1, Overlay.Atlas.Height)));

        float unit = Overlay.LineHeight;
        float wide = Overlay.Measure(text);
        float y = MathF.Round((height - unit) / 2f);

        Overlay.Rect(0, MathF.Round(y - (unit * 0.6f)), width, unit * 2.2f, Shade);
        Overlay.Text(text, MathF.Round((width - wide) / 2f), y, Accent);

        Overlay.Magnify = was;
    }

    /// <summary>Whether a point is on the page at all.</summary>
    /// <param name="point">Where the pointer is.</param>
    /// <returns>True when it is over the panel.</returns>
    public bool Covers(Vector2 point) => Inside(point, _panel);

    /// <summary>Fills the screen behind the page, where anything is wanted there.</summary>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <remarks>
    /// A gradient in bands rather than a picture: sixteen rectangles cost nothing, there is
    /// no bitmap to ship, and it is right at any size. Over a room it is a single dark wash
    /// instead, so the player can still see where they are while the game is paused. Over
    /// the title art it is nothing at all.
    /// </remarks>
    private void Screen(int width, int height)
    {
        if (Behind == MenuBehind.Picture)
        {
            return;
        }

        if (Behind == MenuBehind.Room)
        {
            // Paused over a room: dark enough to read the panel against, light enough to
            // see what was on screen.
            Overlay.Rect(0, 0, width, height, new Vector4(0f, 0f, 0f, 0.55f));
            return;
        }

        const int Bands = 16;

        for (int i = 0; i < Bands; i++)
        {
            float part = i / (float)(Bands - 1);
            float top = height * i / (float)Bands;

            // Near black at the top, a little blue at the bottom: the game is set at night
            // in a small French town, and a menu is the first thing it says.
            Overlay.Rect(
                0,
                MathF.Floor(top),
                width,
                MathF.Ceiling((height / (float)Bands) + 1),
                new Vector4(
                    0.03f + (0.03f * part),
                    0.035f + (0.045f * part),
                    0.05f + (0.075f * part),
                    1f));
        }
    }

    private void Draw(
        MenuItem item,
        int index,
        float x,
        float top,
        float width,
        float row,
        float unit,
        float labelLine,
        string[] wrapped)
    {
        float pad = unit;
        bool chosen = index == Index && item.Selectable;

        if (chosen)
        {
            Overlay.Rect(x, top, width, row, Chosen);
            Overlay.Rect(x, top, unit / 4f, row, Accent);
        }

        Vector4 ink = item.Enabled ? (chosen ? Accent : Ink) : Dim;
        float text = MathF.Round(top + ((row - unit) / 2f));

        if (item.Kind == MenuItemKind.Label)
        {
            // As many lines as the sentence needed, tightly spaced: an explanation belongs
            // to the row above it, and spacing it like a row of its own would separate the
            // two.
            float line = MathF.Round(top + ((labelLine - unit) / 2f));

            foreach (string part in wrapped.Length > 0 ? wrapped : [item.Text])
            {
                Overlay.Text(part, MathF.Round(x + pad), line, Dim);
                line += labelLine;
            }

            return;
        }

        Overlay.Text(item.Text, MathF.Round(x + pad), text, ink);

        if (item.Kind == MenuItemKind.Slider)
        {
            // The bar occupies the right half of the row, with the reading beyond it.
            float reading = Overlay.Measure(item.Value);
            float barRight = x + width - pad - reading - unit;
            float barLeft = x + (width / 2f);
            float barWidth = Math.Max(unit * 4f, barRight - barLeft);
            float barTop = MathF.Round(top + (row / 2f) - (unit / 6f));
            float barHeight = MathF.Max(2f, unit / 3f);

            Overlay.Rect(barLeft, barTop, barWidth, barHeight, Track);
            Overlay.Rect(barLeft, barTop, barWidth * item.Fraction, barHeight, chosen ? Accent : Ink);

            Overlay.Text(item.Value, MathF.Round(x + width - pad - reading), text, ink);
            return;
        }

        if (item.Value.Length > 0)
        {
            // Choices carry their arrows so the row says it can be stepped. A toggle does
            // not: there is nowhere else for it to go.
            string value = item.Kind == MenuItemKind.Choice ? $"< {item.Value} >" : item.Value;

            Overlay.Text(
                value, MathF.Round(x + width - pad - Overlay.Measure(value)), text, ink);
        }
    }

    /// <summary>Which row the pointer is over, or -1.</summary>
    /// <remarks>
    /// Walked rather than divided, because rows are no longer all the same height: an
    /// explanation that wrapped onto three lines is three lines tall, and dividing by one
    /// row's height would put the pointer on the wrong setting for the rest of the page.
    /// </remarks>
    private static int RowUnder(
        Vector2 point,
        float x,
        float top,
        float width,
        float[] heights,
        IReadOnlyList<MenuItem> items,
        int first,
        int shown)
    {
        if (point.X < x || point.X > x + width)
        {
            return -1;
        }

        float at = top;

        for (int i = first; i < first + shown && i < items.Count; i++)
        {
            if (point.Y >= at && point.Y < at + heights[i])
            {
                return items[i].Selectable ? i : -1;
            }

            at += heights[i];
        }

        return -1;
    }

    private float FractionAt(float pointerX, Vector4 bounds)
    {
        float unit = Overlay.LineHeight;
        float left = bounds.X + (bounds.Z / 2f);
        float right = bounds.X + bounds.Z - unit;

        return Math.Clamp((pointerX - left) / MathF.Max(right - left, 1f), 0f, 1f);
    }

    private static bool Inside(Vector2 point, Vector4 bounds) =>
        point.X >= bounds.X && point.X <= bounds.X + bounds.Z &&
        point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.W;

    /// <summary>A percentage, for a slider's reading.</summary>
    /// <param name="fraction">Zero to one.</param>
    /// <returns>The text.</returns>
    public static string Percent(float fraction) => string.Create(
        CultureInfo.InvariantCulture, $"{Math.Clamp(fraction, 0f, 1f) * 100:F0}%");
}
