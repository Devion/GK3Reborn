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

    /// <summary>
    /// Not selectable: the name of a group of settings, with a rule across the page.
    /// </summary>
    /// <remarks>
    /// Apart from <see cref="Label"/> because the two are opposites of each other. A label
    /// is an afterthought under the row it belongs to and is drawn small and dim; a heading
    /// introduces the rows below it and has to be found at a glance. A page laid out in two
    /// columns needs them: without a heading the reader has no way to tell where one group
    /// of settings stopped and the next began, because the eye no longer has a single
    /// column to follow.
    /// </remarks>
    Heading,
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

    /// <summary>
    /// A row that shows what something is bound to, and is pressed to change it.
    /// </summary>
    /// <param name="id">What the page's owner calls it.</param>
    /// <param name="text">What the binding is for.</param>
    /// <param name="value">What it currently answers to.</param>
    /// <returns>The row.</returns>
    /// <remarks>
    /// A button with a reading rather than a choice, and the difference is the arrows. A
    /// choice draws <c>&lt; this &gt;</c> because it can be stepped left and right through a
    /// short list; a binding cannot be stepped at all — there are a hundred keys and the way
    /// to pick one is to press it — so arrows would be the row lying about what it does.
    /// </remarks>
    public static MenuItem Binding(string id, string text, string value) =>
        new(id, MenuItemKind.Button, text, value);

    /// <summary>A row that is only words.</summary>
    public static MenuItem Label(string text) =>
        new(string.Empty, MenuItemKind.Label, text, Enabled: false);

    /// <summary>The name of the group of settings that follows.</summary>
    public static MenuItem Heading(string text) =>
        new(string.Empty, MenuItemKind.Heading, text, Enabled: false);

    /// <summary>Whether the player can land on this row.</summary>
    public bool Selectable =>
        Kind is not (MenuItemKind.Label or MenuItemKind.Heading) && Enabled;

    /// <summary>
    /// Whether this row takes the whole width of the page rather than a column of it.
    /// </summary>
    /// <remarks>
    /// A heading introduces everything under it, and an explanation is a sentence: neither
    /// makes any sense confined to half the page while the other half carries an unrelated
    /// setting. Everything else pairs up.
    /// </remarks>
    public bool Spans => Kind is MenuItemKind.Label or MenuItemKind.Heading;
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

/// <summary>One entry in the list of sections down the side of the settings screen.</summary>
/// <param name="Id">
/// What choosing it means, reported back as a <c>tab:</c> action so that the front end
/// decides what a section is rather than the page that draws it.
/// </param>
/// <param name="Text">Its caption.</param>
public readonly record struct MenuSection(string Id, string Text);

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

    /// <summary>The bands the rows were laid into, and where each one sits in the page.</summary>
    /// <remarks>
    /// A band is one line across the content: either a single row spanning the whole width
    /// — a heading, an explanation — or a left-hand row and its right-hand partner. It is
    /// the unit the page scrolls in and the unit a selection is revealed in, because both
    /// of those questions are about a line of the page rather than about a setting.
    /// </remarks>
    private readonly List<Band> _bands = [];

    /// <summary>Which band each item ended up in.</summary>
    private int[] _bandOf = [];

    /// <summary>How wide each row's label and reading come to together.</summary>
    /// <remarks>
    /// Kept because it decides two things and they are decided in different places: how
    /// wide a column wants to be, and whether a particular row will fit in one.
    /// </remarks>
    private float[] _widths = [];

    /// <summary>Where each of the sidebar's entries was drawn.</summary>
    private readonly List<(string Id, Vector4 Bounds)> _tabs = [];

    /// <summary>
    /// How much room the sliders on the page need for their labels and their readings.
    /// </summary>
    /// <remarks>
    /// One pair of numbers for the whole page rather than one per row, because a bar is
    /// drawn in the same place on every slider of a page: five volumes whose bars each
    /// began after their own label would read as five unrelated rows instead of as one
    /// list. Taken from the longest label and the longest reading on the page, so the bar
    /// starts clear of the worst of them.
    /// </remarks>
    private float _sliderLabel;

    /// <summary>How wide a slider's reading is allowed to be. See <see cref="_sliderLabel"/>.</summary>
    private float _sliderValue;

    private Vector4 _panel;

    /// <summary>The rectangle the rows are confined to, below the heading.</summary>
    /// <remarks>
    /// Kept because a scrolled page draws rows that are partly outside it, and a click on
    /// the sliver of a row hanging past the bottom edge should do nothing: what the player
    /// pointed at is what they could see.
    /// </remarks>
    private Vector4 _content;

    /// <summary>
    /// How far down the page has scrolled, in pixels, and where it is heading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two numbers rather than one, and both in pixels rather than in rows.</b> The
    /// first attempt kept a single row index and recomputed it from the selection every
    /// frame, growing a window outwards from the chosen row — which meant the chosen row
    /// was always in the middle, so <em>every</em> step of the selection scrolled the whole
    /// page by one row. The list moved under the player instead of the cursor moving down
    /// it, which is what "it loves to immediately jump" describes.
    /// </para>
    /// <para>
    /// Now the page only moves when the selection would otherwise leave it, it moves by the
    /// least it can, and it takes a few frames to get there. A row in the middle of the
    /// page can be stepped past without the page moving at all.
    /// </para>
    /// </remarks>
    private float _scroll;

    /// <summary>Where the scroll is going.</summary>
    private float _scrollTarget;

    /// <summary>The furthest down it may go, from the last page laid out.</summary>
    private float _scrollMax;

    /// <summary>The selection the page was last revealed for.</summary>
    /// <remarks>
    /// So that revealing only happens when the selection <em>moves</em>. Without this the
    /// wheel would be undone on the very next frame: the page would scroll away from the
    /// chosen row and be dragged straight back to it.
    /// </remarks>
    private int _revealed = -1;

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
        _bands.Clear();
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
    /// The sections listed down the left of the panel, or none for a page that is one list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by whoever owns the pages, because which sections there are is a fact about the
    /// settings and not about how they are drawn. Empty is the ordinary case: the title
    /// screen, the pause menu and the save slots are each a single list.
    /// </para>
    /// <para>
    /// A page with sections is also a page that lays its rows in two columns and that is
    /// wide enough to. The two go together — the sidebar costs width, and what pays for it
    /// is not having to walk into a page and back out again for every group of settings.
    /// </para>
    /// </remarks>
    public IReadOnlyList<MenuSection> Sections { get; set; } = [];

    /// <summary>Which of them is showing.</summary>
    public int Section { get; set; }

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

    /// <summary>How many rows a page needs before it is worth splitting into two columns.</summary>
    /// <remarks>
    /// Below this a second column is worse than no second column: the eye has to cross the
    /// page to find three rows that would have been under its nose. Above it the page stops
    /// scrolling, which is the whole reason for the columns.
    /// </remarks>
    private const int TwoColumnsAbove = 6;

    /// <summary>How quickly the page catches up with where it is scrolling to.</summary>
    /// <remarks>
    /// An exponential approach rather than a constant speed, so a small correction is quick
    /// and a jump to the far end of a long page still takes about a fifth of a second. High
    /// enough that nobody waits for it; low enough that the eye can follow which way the
    /// page went, which is the entire point of animating it at all.
    /// </remarks>
    private const float ScrollRate = 18f;

    /// <summary>
    /// The narrowest one column of settings may be, in ems.
    /// </summary>
    /// <remarks>
    /// Below this a column is too narrow for an ordinary label and its reading, and two of
    /// them are worse than one of twice the width. Fourteen ems is about "Higher-resolution
    /// textures  On" at the size the interface cuts its letters for a 1080-line display,
    /// which is the case this number exists to admit.
    /// </remarks>
    private const float NarrowestColumn = 14f;

    /// <summary>
    /// The narrowest a slider's bar may be, in ems.
    /// </summary>
    /// <remarks>
    /// Six, which is about forty steps of a volume at the size the interface cuts its
    /// letters at. It is here rather than only in the drawing because it is part of how
    /// wide a slider's row <em>is</em>: a bar that is not measured is a bar that is drawn
    /// over whatever was measured instead.
    /// </remarks>
    private const float BarLeast = 6f;

    /// <summary>
    /// The widest reading a slider is expected to carry.
    /// </summary>
    /// <remarks>
    /// Room is kept for this even while every slider on the page reads something shorter,
    /// because the alternative is a bar whose right-hand end moves while it is being
    /// dragged: the readings are what decide where the bars stop, and "100%" is four
    /// characters where "9%" is two. Every reading these pages use — a percentage, a
    /// multiplier — is at its widest four characters.
    /// </remarks>
    private const string WidestReading = "100%";

    /// <summary>
    /// One in how many of a page's rows may overflow its column before the columns are
    /// given up.
    /// </summary>
    /// <remarks>
    /// A third. Below that the long rows read as the exceptions they are; above it the page
    /// is mostly full-width rows with the occasional pair, which looks like a grid that has
    /// failed rather than like a list.
    /// </remarks>
    private const int TooRagged = 3;

    /// <summary>One line across the content: a spanning row, or a left row and its partner.</summary>
    /// <param name="Left">The item in the left column, or the spanning one.</param>
    /// <param name="Right">The item in the right column, or -1 when there is none.</param>
    /// <param name="Top">How far down the whole page this band starts.</param>
    /// <param name="Height">How tall it is.</param>
    /// <param name="Full">
    /// Whether <see cref="Left"/> takes the whole width rather than one column of it.
    /// Recorded rather than worked out again at drawing time, because there are two reasons
    /// a row is full-width — it is a heading or a sentence, or it is a row too wide for a
    /// column — and three places that would otherwise have to ask both questions the same
    /// way.
    /// </param>
    private readonly record struct Band(
        int Left, int Right, float Top, float Height, bool Full);

    /// <summary>Draws a page and remembers where every row went.</summary>
    /// <param name="title">The heading.</param>
    /// <param name="items">The rows.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <param name="at">Where the pointer is, for the hover.</param>
    /// <param name="seconds">
    /// How long since the page was last drawn, which is what the scrolling is animated
    /// over. Nought snaps, which is what a test and a photograph both want: neither has a
    /// second frame for the page to have settled on.
    /// </param>
    /// <remarks>
    /// There is no line along the bottom saying which keys work. A menu that tells the
    /// player what an arrow key does is a menu that thinks they have not used one.
    /// </remarks>
    public void Build(
        string title,
        IReadOnlyList<MenuItem> items,
        int width,
        int height,
        Vector2 at,
        float seconds = 0f)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(items);

        Overlay.Begin(width, height);

        _rows.Clear();
        _tabs.Clear();
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

        // The list of sections down the left, where there is one. Its width is the longest
        // name in it, because a sidebar that clips its own captions is worse than no
        // sidebar, and at least ten ems so that a page of short words still reads as a
        // column rather than as a margin.
        float sidebar = 0f;

        foreach (MenuSection section in Sections)
        {
            sidebar = MathF.Max(sidebar, Overlay.Measure(section.Text));
        }

        if (Sections.Count > 0)
        {
            sidebar = MathF.Max(sidebar + (2.5f * unit), 8f * unit);
        }

        // Wide enough for the longest thing on the page, and never wider than the window
        // has room for. A page of short labels should not be a page-wide slab.
        //
        // Spanning rows are deliberately not measured here. They are sentences of
        // explanation and headings, and the longest of them would make the panel as wide as
        // the window on every page that has one; they are wrapped to whatever width the
        // rows settle on instead.
        //
        // Neither is the heading, which is measured separately below. It is centred over the
        // whole panel and has nothing to do with how wide one column of settings should be.
        float widest = 0f;

        bool illustrated = false;
        int settings = 0;

        if (_widths.Length < items.Count)
        {
            _widths = new float[items.Count];
        }

        // What the sliders on the page want between them, before any row is measured,
        // because each of them is measured against the same pair of numbers.
        _sliderLabel = 0f;
        _sliderValue = 0f;

        foreach (MenuItem item in items)
        {
            if (item.Kind == MenuItemKind.Slider)
            {
                _sliderLabel = MathF.Max(_sliderLabel, Overlay.Measure(item.Text));
                _sliderValue = MathF.Max(
                    _sliderValue,
                    MathF.Max(Overlay.Measure(item.Value), Overlay.Measure(WidestReading)));
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            MenuItem item = items[i];

            _widths[i] = 0f;

            if (!item.Spans)
            {
                // A choice carries its arrows, so it is wider than its value alone. The
                // first version measured the value and every choice on the page was cut
                // four characters short of its own reading.
                string reading = item.Kind == MenuItemKind.Choice
                    ? $"< {item.Value} >"
                    : item.Value;

                // A slider is three things across a row and not two, and the bar in the
                // middle of it used to be measured as nothing at all: the row asked for its
                // label and its reading, the bar was drawn across the middle of whatever it
                // got, and on the Sound page the middle of the row was the middle of "Music
                // and cutscenes". Every slider asks for the same width, because every
                // slider's bar is drawn in the same place.
                _widths[i] = item.Kind == MenuItemKind.Slider
                    ? _sliderLabel + (BarLeast * unit) + _sliderValue + (2f * unit)
                    : Overlay.Measure(item.Text) + Overlay.Measure("  ") +
                        Overlay.Measure(reading);

                widest = Math.Max(widest, _widths[i]);

                settings++;
            }

            illustrated |= item.Picture > 0;
        }

        // A page whose rows carry pictures shows one of them, large, beside the list, so
        // the panel moves out of the middle to leave room for it.
        float across = illustrated ? 0.30f : Math.Clamp(Across, 0f, 1f);

        // A page with a heading is at least wide enough to look like one; a page without
        // is a column of short words and should be no wider than it needs.
        float least = MathF.Max(
            (title.Length > 0 ? 22 : 12) * unit,
            title.Length > 0 ? (Overlay.Measure(title) * large / (float)ink) + (4f * unit) : 0f);

        // What one column of settings wants: the widest row on the page, and room either
        // side of it.
        float wanted = widest + (2f * unit);

        int columns = 1;
        float panelWidth;

        if (Sections.Count > 0)
        {
            // The settings screen, which takes the width it is allowed rather than the
            // width the section that happens to be showing would like.
            //
            // Fitted to its own rows it was a different size on every section — Sound is
            // six short rows and Picture is thirty long ones — and, being a panel centred
            // in the window, it moved as it changed size. Clicking a name in the sidebar
            // pulled the sidebar out from under the pointer, which is the one thing a list
            // of tabs must not do. A settings screen is one window with five pages in it,
            // not five windows.
            float cap = width - (4f * unit);

            // How wide a column comes out with two of them. The column decision is made
            // against this rather than against what the widest row wants, because those are
            // different questions and answering the second one kept the page in a single
            // column on the commonest monitor there is: one DLSS row whose *value* reads
            // "Preset L (transformer, steadiest)" is 791 pixels wide on its own, and asking
            // for two columns that wide is asking for more window than a 1080-line display
            // has.
            float room = (cap - sidebar) / 2f;

            columns = settings >= TwoColumnsAbove && room >= NarrowestColumn * unit ? 2 : 1;

            // A row wider than its column takes the whole width instead — see Lay — which
            // costs that row a line and leaves the rest of the page in two columns. That is
            // the right answer for one or two rows and the wrong one for a page of them:
            // Playing is nine sentences, most of which do not fit a column, and a grid where
            // most rows span reads as a layout that has gone wrong rather than as a grid.
            //
            // So the page counts them, and goes back to one wide column when too many of
            // its rows are long. Nothing is tuned per page: the same rule gives Picture and
            // Controls two columns and gives Playing one, because that is what those pages
            // are actually like. How many columns a section is laid in still varies from
            // section to section; how big the panel around them is does not.
            if (columns == 2 && Ragged(items, room - (2f * pad)) * TooRagged > settings)
            {
                columns = 1;
            }

            panelWidth = cap;
        }
        else
        {
            // Everything else: the title screen, the pause menu, the save slots. One
            // column, and forty-four ems is about the width a line of prose is comfortable
            // at.
            panelWidth = Math.Min(
                Math.Min(width - (4 * unit), 44 * unit),
                Math.Max(wanted + (1 * unit), least));

            panelWidth = Math.Max(panelWidth, Math.Min(width - (4 * unit), least));
        }

        float contentWidth = panelWidth - sidebar;
        float columnWidth = contentWidth / columns;

        // No heading where the art already carries the game's name. Drawing "Gabriel
        // Knight 3" over a picture that says Gabriel Knight is how a menu looks like a
        // placeholder.
        float titleHeight = title.Length > 0 ? row + (titleUnit - unit) : pad / 2f;

        // The explanations, broken to the width of the content they will be drawn across.
        // Done before the heights are worked out, because a sentence that takes two lines
        // takes two lines of room.
        float inner = contentWidth - (2 * pad);
        float labelLine = unit * 1.25f;

        _wrapped.Clear();

        foreach (MenuItem item in items)
        {
            _wrapped.Add(item.Kind == MenuItemKind.Label ? Wrap(item.Text, inner) : []);
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

        float total = Lay(items, heights, columns, row, columnWidth, pad);

        // What is left of the window once the panel's margins and its heading are taken
        // off. The page is fitted into this rather than allowed to run past it.
        float available = MathF.Max(row, height - (4 * unit) - titleHeight - pad);

        // The settings screen keeps the whole of it whatever section is showing, for the
        // same reason it keeps the whole width: so that the panel, the heading and the
        // sidebar are in the same place on every one of them.
        //
        // The sidebar is the other half of that argument rather than an afterthought to it.
        // It is as long as there are sections, and a section whose own rows came to less
        // than that used to have the last of the section names drawn below the bottom edge
        // of the panel they belong to.
        float viewport = Sections.Count > 0 ? available : MathF.Min(total, available);

        _scrollMax = MathF.Max(0f, total - viewport);

        // Only when the selection has moved. A page that reveals its selection every frame
        // cannot be scrolled with the wheel: it would be dragged straight back to whatever
        // row the keyboard was last on.
        if (Index != _revealed)
        {
            Reveal(Index, viewport);
            _revealed = Index;
        }

        _scrollTarget = Math.Clamp(_scrollTarget, 0f, _scrollMax);
        _scroll = Approach(_scroll, _scrollTarget, seconds);

        float panelHeight = titleHeight + viewport + pad;

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

        float top = y + titleHeight;
        float left = x + sidebar;

        _content = new Vector4(left, top, contentWidth, viewport);

        Aside(x, top, sidebar, viewport, unit, row, pad);

        // The pointer picks the row before anything is drawn, so hovering and the keyboard
        // land on the same row and the drawn highlight is the one that will be clicked.
        int hovered = ItemUnder(at, left, top, columnWidth, columns, items);

        if (hovered >= 0)
        {
            Index = hovered;
        }

        Preview(items, x + panelWidth, top, width, unit);

        // Confined to the content, because a page that scrolls by pixels draws rows that
        // are half outside it. Clipped as the quads are added, so a row cut off at the
        // bottom edge is drawn as the top half of a row rather than as a whole one
        // squashed into what is left.
        Overlay.PushClip(_content);

        foreach (Band band in _bands)
        {
            float bandTop = top + band.Top - _scroll;

            if (bandTop + band.Height <= top || bandTop >= top + viewport)
            {
                continue;
            }

            Place(items, band, left, bandTop, contentWidth, columnWidth, unit, labelLine);
        }

        Overlay.PopClip();

        if (_scrollMax > 0f)
        {
            Scrollbar(x + panelWidth, top, viewport, total);
        }
    }

    /// <summary>How many of a page's rows are too wide for a column of a given width.</summary>
    /// <param name="items">The rows.</param>
    /// <param name="fits">How much room a row has inside its column.</param>
    /// <returns>The count.</returns>
    private int Ragged(IReadOnlyList<MenuItem> items, float fits)
    {
        int wide = 0;

        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].Spans && _widths[i] > fits)
            {
                wide++;
            }
        }

        return wide;
    }

    /// <summary>
    /// Lays the rows into bands, and says how tall the whole page came out.
    /// </summary>
    /// <param name="items">The rows.</param>
    /// <param name="heights">How tall each one is.</param>
    /// <param name="columns">How many columns to fill.</param>
    /// <param name="row">One row's height, for a band of settings.</param>
    /// <param name="columnWidth">How wide one column came out.</param>
    /// <param name="pad">The room either side of a row inside its column.</param>
    /// <returns>The height of the whole page.</returns>
    /// <remarks>
    /// <para>
    /// <b>Down one column and then down the next, not across.</b> A grid filled across
    /// would put the second setting to the right of the first, so pressing Down would skip
    /// every other row and there would be no key left that moved between the columns — Left
    /// and Right belong to the values. Filled downwards, the order the keyboard walks is
    /// the order the eye reads, and the only surprise is one step from the foot of one
    /// column to the head of the next.
    /// </para>
    /// <para>
    /// Headings and explanations break the run they are in, so a group of settings is
    /// balanced over its own two columns rather than over the whole page. That is what
    /// keeps a heading meaning the rows underneath it.
    /// </para>
    /// </remarks>
    private float Lay(
        IReadOnlyList<MenuItem> items,
        float[] heights,
        int columns,
        float row,
        float columnWidth,
        float pad)
    {
        _bands.Clear();

        if (_bandOf.Length < items.Count)
        {
            _bandOf = new int[items.Count];
        }

        float total = 0f;
        int at = 0;

        // What a row has to fit in to be allowed half a line. A row wider than this would
        // have its reading drawn over its own label, so it takes the whole width instead —
        // which breaks the run it is in, exactly as a heading does.
        float fits = columns > 1 ? columnWidth - (2f * pad) : float.MaxValue;

        bool Wide(int i) => items[i].Spans || _widths[i] > fits;

        while (at < items.Count)
        {
            if (Wide(at))
            {
                _bandOf[at] = _bands.Count;
                _bands.Add(new Band(at, -1, total, heights[at], true));
                total += heights[at];
                at++;

                continue;
            }

            int start = at;

            while (at < items.Count && !Wide(at))
            {
                at++;
            }

            int count = at - start;
            int down = (count + columns - 1) / columns;

            for (int k = 0; k < down; k++)
            {
                int first = start + k;
                int second = columns > 1 && start + k + down < at ? start + k + down : -1;

                _bandOf[first] = _bands.Count;

                if (second >= 0)
                {
                    _bandOf[second] = _bands.Count;
                }

                _bands.Add(new Band(first, second, total, row, false));
                total += row;
            }
        }

        return total;
    }

    /// <summary>Scrolls the page the least it can to put a row on it.</summary>
    /// <param name="index">The row.</param>
    /// <param name="viewport">How much of the page is showing.</param>
    /// <remarks>
    /// With a row's lead above and below, so that stepping onto the last visible row shows
    /// what is coming rather than leaving the selection against the edge with no warning
    /// that the page has more on it.
    /// </remarks>
    private void Reveal(int index, float viewport)
    {
        if (_bands.Count == 0 || index < 0 || index >= _bandOf.Length)
        {
            return;
        }

        Band band = _bands[_bandOf[index]];
        float margin = MathF.Min(Row, viewport / 4f);

        if (band.Top - margin < _scrollTarget)
        {
            _scrollTarget = band.Top - margin;
        }

        if (band.Top + band.Height + margin > _scrollTarget + viewport)
        {
            _scrollTarget = band.Top + band.Height + margin - viewport;
        }
    }

    /// <summary>Moves a scroll position towards where it is going.</summary>
    /// <param name="from">Where it is.</param>
    /// <param name="to">Where it is heading.</param>
    /// <param name="seconds">How long since the last frame.</param>
    /// <returns>Where it has reached.</returns>
    /// <remarks>
    /// Framerate-independent: the fraction closed is taken from an exponential of the
    /// elapsed time rather than being a constant per frame, so the page settles at the same
    /// speed at 30 and at 300 frames a second. Snapped once it is within half a pixel,
    /// because an asymptote never arrives and a page that is forever a third of a pixel
    /// short of where it is going is a page that redraws forever.
    /// </remarks>
    private static float Approach(float from, float to, float seconds)
    {
        float difference = to - from;

        if (seconds <= 0f || MathF.Abs(difference) < 0.5f)
        {
            return to;
        }

        return from + (difference * (1f - MathF.Exp(-seconds * ScrollRate)));
    }

    /// <summary>Draws the list of sections down the left of the panel.</summary>
    /// <remarks>
    /// Only where there is one. The title screen, the pause menu and the save slots are one
    /// list each and have nothing to put in a sidebar; the settings are five lists and used
    /// to be reached by walking into a page and back out of it for every one of them.
    /// </remarks>
    private void Aside(
        float x, float top, float sidebar, float viewport, float unit, float row, float pad)
    {
        if (Sections.Count == 0)
        {
            return;
        }

        Overlay.Rect(MathF.Round(x + sidebar) - 1, top, 1, viewport, Rule);

        float at = top + (pad / 2f);

        for (int i = 0; i < Sections.Count; i++)
        {
            bool here = i == Section;

            if (here)
            {
                Overlay.Rect(x, at, sidebar, row, Chosen);
                Overlay.Rect(x, at, unit / 4f, row, Accent);
            }

            Overlay.Text(
                Sections[i].Text,
                MathF.Round(x + pad),
                MathF.Round(at + ((row - unit) / 2f)),
                here ? Accent : Ink);

            _tabs.Add((Sections[i].Id, new Vector4(x, at, sidebar, row)));

            at += row;
        }
    }

    /// <summary>Draws the picture belonging to the row the player is on, beside the list.</summary>
    /// <remarks>
    /// One of them rather than one per row: a room is recognised by its shape and its
    /// colour, and neither survives being drawn the height of a line of text.
    /// </remarks>
    private void Preview(
        IReadOnlyList<MenuItem> items, float right, float top, float width, float unit)
    {
        if (Index < 0 || Index >= items.Count || items[Index].Picture <= 0)
        {
            return;
        }

        float wide = MathF.Round(width * PreviewWide);
        float tall = MathF.Round(wide * 3f / 4f);
        float left = MathF.Round(Math.Min(right + (unit * 2f), width - wide - (unit * 2f)));
        float topOf = MathF.Round(top);

        // A border, so a dark room reads as a picture rather than as a hole.
        Overlay.Rect(left - 2, topOf - 2, wide + 4, tall + 4, Rule);
        Overlay.Picture(items[Index].Picture, left, topOf, wide, tall, Vector4.One);
    }

    /// <summary>Draws one band, and remembers where each of its rows went.</summary>
    private void Place(
        IReadOnlyList<MenuItem> items,
        Band band,
        float left,
        float top,
        float contentWidth,
        float columnWidth,
        float unit,
        float labelLine)
    {
        MenuItem spanning = items[band.Left];

        if (band.Full)
        {
            Draw(
                spanning, band.Left, left, top, contentWidth, band.Height, unit,
                labelLine, _wrapped[band.Left]);

            _rows.Add((
                band.Left,
                spanning.Id,
                new Vector4(left, top, contentWidth, band.Height),
                spanning.Kind));

            return;
        }

        Column(items, band.Left, left, top, columnWidth, band.Height, unit, labelLine);

        if (band.Right >= 0)
        {
            Column(
                items, band.Right, left + columnWidth, top, columnWidth, band.Height,
                unit, labelLine);
        }
    }

    /// <summary>Draws one row into one column of a band.</summary>
    private void Column(
        IReadOnlyList<MenuItem> items,
        int index,
        float x,
        float top,
        float width,
        float height,
        float unit,
        float labelLine)
    {
        MenuItem item = items[index];

        Draw(item, index, x, top, width, height, unit, labelLine, _wrapped[index]);

        _rows.Add((index, item.Id, new Vector4(x, top, width, height), item.Kind));
    }

    /// <summary>Which item the pointer is over, or -1.</summary>
    /// <remarks>
    /// Walked over the bands rather than divided, because rows are not all the same height
    /// and the page no longer starts at its first row. A point outside the content is over
    /// nothing at all, which is what keeps a click on the sliver of a row hanging past the
    /// bottom edge from landing on a setting the player cannot see.
    /// </remarks>
    private int ItemUnder(
        Vector2 point,
        float left,
        float top,
        float columnWidth,
        int columns,
        IReadOnlyList<MenuItem> items)
    {
        if (!Inside(point, _content))
        {
            return -1;
        }

        foreach (Band band in _bands)
        {
            float bandTop = top + band.Top - _scroll;

            if (point.Y < bandTop || point.Y >= bandTop + band.Height)
            {
                continue;
            }

            if (items[band.Left].Spans)
            {
                return -1;
            }

            // A full band is one row however many columns the page has, so the whole band
            // is that row and there is no right-hand half to land in.
            int which = columns > 1 && !band.Full && point.X >= left + columnWidth
                ? band.Right
                : band.Left;

            return which >= 0 && which < items.Count && items[which].Selectable ? which : -1;
        }

        return -1;
    }

    /// <summary>Draws the bar that says how much of a long page is showing.</summary>
    /// <remarks>
    /// A bar rather than arrows, because it says two things at once — that there is more,
    /// and how much more — and because it needs no target to click on: this page is stepped
    /// with the keyboard and the wheel, and a scroll bar nobody drags is a scroll bar that
    /// only has to be read.
    /// </remarks>
    private void Scrollbar(float right, float top, float viewport, float total)
    {
        float unit = Overlay.LineHeight;
        float thick = MathF.Max(2f, unit / 4f);
        float left = right - thick - (unit / 3f);

        Overlay.Rect(left, top, thick, viewport, Track);

        float part = viewport / MathF.Max(1f, total);
        float start = _scroll / MathF.Max(1f, total);

        Overlay.Rect(
            left,
            MathF.Round(top + (viewport * start)),
            thick,
            MathF.Max(unit / 2f, viewport * part),
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
    /// <remarks>
    /// And puts the page back to the top, without animating it there. This is called when
    /// the page has <em>changed</em> — a different section, a different screen — and sliding
    /// a page that is not the page the player was looking at is an animation of nothing.
    /// </remarks>
    public void Reset(IReadOnlyList<MenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Index = 0;
        _scroll = 0f;
        _scrollTarget = 0f;
        _revealed = -1;

        if (items.Count > 0 && !items[0].Selectable)
        {
            Move(items, 1);
        }
    }

    /// <summary>Scrolls the page by the wheel.</summary>
    /// <param name="notches">How far it turned; positive is away from the player.</param>
    /// <remarks>
    /// <para>
    /// The page and not the selection. Turning the wheel over a long settings page to see
    /// what is on it should not change what pressing Enter would do, and a wheel that
    /// stepped the selection could not reach the bottom of a page without walking through
    /// every row on the way — which is how the wheel used to behave everywhere else in this
    /// interface and is wrong here.
    /// </para>
    /// <para>
    /// Three rows a notch, which is what the rest of the desktop does.
    /// </para>
    /// </remarks>
    public void Wheel(int notches)
    {
        if (notches == 0 || _scrollMax <= 0f)
        {
            return;
        }

        _scrollTarget = Math.Clamp(
            _scrollTarget - (notches * Row * 3f), 0f, _scrollMax);
    }

    /// <summary>Whether the page has more on it than is showing.</summary>
    public bool Scrolls => _scrollMax > 0f;

    /// <summary>How far down the page has scrolled, in pixels.</summary>
    /// <remarks>
    /// Nought at the top. Read by tests, which is the only way to ask the question that
    /// matters here — whether the page moved — without reimplementing the layout to find
    /// out where a row should have been.
    /// </remarks>
    public float Scrolled => _scroll;

    /// <summary>
    /// Where a row was drawn, or null when it was not.
    /// </summary>
    /// <param name="index">Which row, by index into the items last drawn.</param>
    /// <returns>Its rectangle, as x, y, width, height.</returns>
    /// <remarks>
    /// The same list the pointer is hit-tested against, so anything that agrees with this
    /// agrees with what a click will do. A row scrolled off the page has no rectangle, which
    /// is the honest answer: it was not drawn and cannot be clicked.
    /// </remarks>
    public Vector4? Where(int index)
    {
        foreach ((int at, _, Vector4 bounds, _) in _rows)
        {
            if (at == index)
            {
                return bounds;
            }
        }

        return null;
    }

    /// <summary>
    /// The middle of one of the sidebar's entries, or null when it is not showing.
    /// </summary>
    /// <param name="id">Which section.</param>
    /// <returns>A point inside it.</returns>
    public Vector2? Aside(string id)
    {
        foreach ((string section, Vector4 bounds) in _tabs)
        {
            if (string.Equals(section, id, StringComparison.Ordinal))
            {
                return new Vector2(
                    bounds.X + (bounds.Z / 2f), bounds.Y + (bounds.W / 2f));
            }
        }

        return null;
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

        // The sidebar first, because it is outside the content and no row of settings can
        // be under the pointer at the same time.
        foreach ((string section, Vector4 where) in _tabs)
        {
            if (Inside(point, where))
            {
                return new MenuAction("tab:" + section);
            }
        }

        // Rows only where they can be seen. A page scrolled by pixels draws the top half of
        // a row against the bottom edge of the content, and the half hanging past it is
        // clipped away — so it must not be clickable either.
        if (!Inside(point, _content))
        {
            return MenuAction.None;
        }

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

        if (item.Kind == MenuItemKind.Heading)
        {
            // The caption, and a rule filling the rest of the line. A word alone over a
            // grid of settings does not read as a divider; a word with a line running out
            // of it does, and it costs one rectangle.
            float caption = Overlay.Measure(item.Text);
            float ruleLeft = MathF.Round(x + pad + caption + (unit / 2f));
            float ruleRight = MathF.Round(x + width - pad);

            Overlay.Text(item.Text, MathF.Round(x + pad), text, Accent);

            if (ruleRight > ruleLeft)
            {
                Overlay.Rect(
                    ruleLeft,
                    MathF.Round(top + (row / 2f)),
                    ruleRight - ruleLeft,
                    1,
                    Rule);
            }

            return;
        }

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
            (float barLeft, float barWidth) = Bar(x, width);

            float barTop = MathF.Round(top + (row / 2f) - (unit / 6f));
            float barHeight = MathF.Max(2f, unit / 3f);

            Overlay.Rect(barLeft, barTop, barWidth, barHeight, Track);
            Overlay.Rect(barLeft, barTop, barWidth * item.Fraction, barHeight, chosen ? Accent : Ink);

            Overlay.Text(
                item.Value,
                MathF.Round(x + width - pad - Overlay.Measure(item.Value)),
                text,
                ink);

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

    /// <summary>
    /// Where a slider's bar runs inside a row.
    /// </summary>
    /// <param name="x">The row's left edge.</param>
    /// <param name="width">How wide the row is.</param>
    /// <returns>Where the bar starts and how long it is.</returns>
    /// <remarks>
    /// <para>
    /// Halfway across the row, or clear of the longest label on the page where that is
    /// further — which is what stops the bar being drawn through the words. The far end is
    /// short of the widest reading, so a page of sliders has its bars starting and stopping
    /// in the same two places however long each row's own label happens to be.
    /// </para>
    /// <para>
    /// One place rather than two, because the bar the player drags has to be the bar they
    /// were shown: the drawing and the hit test used to work this out separately and only
    /// agreed by coincidence.
    /// </para>
    /// </remarks>
    private (float Left, float Width) Bar(float x, float width)
    {
        float unit = Overlay.LineHeight;
        float least = BarLeast * unit;
        float right = x + width - unit - _sliderValue - unit;

        // Never past the row's own left edge, however little room is left. A row too narrow
        // for a label, a bar and a reading takes the whole width instead — see Lay, which
        // is measured against the same three things — but a window can always be dragged
        // narrower than the last thing that fits, and a bar hanging out of the side of the
        // panel is a worse answer to that than a short one.
        float left = MathF.Max(
            x + unit,
            MathF.Min(
                MathF.Max(x + (width / 2f), x + unit + _sliderLabel + unit),
                right - least));

        return (left, MathF.Max(unit, right - left));
    }

    private float FractionAt(float pointerX, Vector4 bounds)
    {
        (float left, float wide) = Bar(bounds.X, bounds.Z);

        return Math.Clamp((pointerX - left) / MathF.Max(wide, 1f), 0f, 1f);
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
