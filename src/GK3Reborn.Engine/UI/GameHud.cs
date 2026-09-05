using System.Globalization;
using System.Numerics;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI;

/// <summary>What the interface is being asked to show this frame.</summary>
/// <param name="Noun">What the pointer is over, or null.</param>
/// <param name="Verbs">What that answers to, most likely first.</param>
/// <param name="Verb">The verb a plain click would perform.</param>
/// <param name="Hotspots">
/// Every noun in the room and where it is on screen, while the player is holding the key
/// that asks. Empty the rest of the time.
/// </param>
/// <param name="At">Where the pointer is, in pixels.</param>
/// <param name="MenuOpen">Whether the player asked for the full list of verbs.</param>
/// <param name="MenuIndex">
/// Which verb is chosen. The wheel moves it, the pointer moves it by being over a row, and
/// a click takes it — one selection with three ways to move it, rather than a hover
/// highlight that the wheel cannot reach.
/// </param>
/// <param name="MenuAt">
/// Where the pointer was when the list was asked for. Separate from <paramref name="At"/>
/// on purpose: a menu anchored to the live pointer slides away from whoever is reaching
/// for it, so there is nothing to click.
/// </param>
/// <param name="Speaker">Who is talking, or null.</param>
/// <param name="Caption">What they are saying, or null.</param>
/// <param name="Inventory">What the player is carrying.</param>
/// <param name="Held">Which of it is in hand, or null.</param>
/// <param name="InventoryOpen">Whether the inventory is showing.</param>
/// <param name="Place">Where this is, for the corner.</param>
/// <param name="Console">The developer console, when it is showing.</param>
/// <param name="Score">What the player has scored, already written out, or null.</param>
/// <param name="Items">
/// The things in the bag this noun answers to, which the menu offers behind one row rather
/// than listing beside the verbs. An action file writes "use the wallet on Buthane" as a
/// rule whose verb is <c>WALLET</c>, so without somewhere to put them the menu is a list of
/// verbs with a player's whole inventory shuffled into it in no particular order.
/// </param>
/// <param name="Icons">
/// The game's own picture of an item, by item name, for the column that lists them. Null
/// when nothing has been loaded, and an item may answer with nothing.
/// </param>
/// <param name="VerbIcons">
/// The game's own picture of a verb, by verb and by whether that verb is the one picked
/// out. The original's ring was these icons and no words at all, so they are the shape a
/// player who has played the game before is already looking for. Null when nothing has
/// been loaded, and a verb may answer with nothing — three of the 287 name no picture.
/// </param>
/// <param name="Gps">
/// What the handheld GPS is showing, when Grace has one out and switched on. Null in every
/// room but the three that lend her one.
/// </param>
/// <param name="Pictures">
/// The game's own art by file name, for the pieces of the interface that are a picture
/// rather than a drawing.
/// </param>
/// <param name="RadioWorn">
/// Whether Gabriel has the headset on, which is the whole of what decides that the button
/// is drawn. True for one timeblock in the game; see <see cref="Game.Radio"/>.
/// </param>
/// <param name="Radio">
/// What the room will answer to over the radio, here and now. Empty while he is wearing it
/// with nothing to say, which draws the button dim rather than taking it away.
/// </param>
/// <param name="RadioOpen">Whether the list of topics is showing.</param>
/// <param name="RadioIndex">Which of them is picked out.</param>
/// <param name="Prompt">
/// The one control the room itself is asking for, drawn large under the picture. Null in
/// every room but the ones whose mechanism has a move the player cannot find by pointing at
/// the room; see <see cref="Game.Mechanisms.SceneMechanism.Offers"/>.
/// </param>
public readonly record struct HudState(
    string? Noun,
    IReadOnlyList<string> Verbs,
    string? Verb,
    Vector2 At,
    bool MenuOpen,
    int MenuIndex,
    Vector2 MenuAt,
    string? Speaker,
    string? Caption,
    IReadOnlyList<string> Inventory,
    string? Held,
    bool InventoryOpen,
    string Place,
    GameConsole? Console = null,
    string? Score = null,
    IReadOnlyList<string>? Items = null,
    IReadOnlyList<(string Noun, Vector2 At)>? Hotspots = null,
    Func<string, ItemIcon>? Icons = null,
    Func<string, bool, ItemIcon>? VerbIcons = null,
    Game.Mechanisms.GpsReading? Gps = null,
    Func<string, ItemIcon>? Pictures = null,
    bool RadioWorn = false,
    IReadOnlyList<Game.RadioTopic>? Radio = null,
    bool RadioOpen = false,
    int RadioIndex = 0,
    Game.Mechanisms.MechanismButton? Prompt = null);

/// <summary>
/// The game's interface, laid out fresh every frame.
/// </summary>
/// <remarks>
/// <para>
/// The brief this is written against is in <c>docs/screens.md</c> and comes down to one
/// thing: a player should never have to learn the interface to use it. The original made
/// you hold a button to raise a verb ring, pick from icons whose meaning you had to
/// discover, and go through a separate screen to look in your own pockets. So here the
/// noun under the pointer says what a click will do, in words, before the click happens;
/// the full list is one right-click away and is a plain list of verbs; and the inventory
/// is a strip along the bottom that is always visible and always one click from being
/// used.
/// </para>
/// <para>
/// Nothing here is retained. It is a function from what the game is doing to a list of
/// rectangles, so there is no widget tree to keep in step with the world and no way for
/// the interface to be showing something that stopped being true.
/// </para>
/// <para>
/// Hit testing is the same layout run twice: <see cref="Build"/> lays the verb menu out
/// and remembers where each row went, and <see cref="VerbAt"/> reads that back. Deriving
/// both from one pass is what keeps the thing you click and the thing you see from
/// drifting apart.
/// </para>
/// </remarks>
public sealed class GameHud
{
    private static readonly Vector4 Panel = new(0.06f, 0.07f, 0.09f, 0.82f);
    private static readonly Vector4 PanelLit = new(0.16f, 0.18f, 0.22f, 0.92f);
    private static readonly Vector4 Ink = new(0.88f, 0.87f, 0.83f, 1f);
    private static readonly Vector4 Dim = new(0.55f, 0.55f, 0.52f, 1f);
    private static readonly Vector4 Accent = new(0.95f, 0.76f, 0.35f, 1f);
    private static readonly Vector4 Rule = new(0.30f, 0.32f, 0.36f, 0.8f);

    /// <summary>The console's own ground, darker and more opaque than the rest.</summary>
    /// <remarks>
    /// Nearly solid on purpose. Everything else here is a label over a room the player is
    /// looking at; this is a surface being read line by line, and the room behind it is a
    /// distraction rather than context.
    /// </remarks>
    private static readonly Vector4 Console = new(0.03f, 0.04f, 0.06f, 0.97f);

    private static readonly Vector4 Complaint = new(0.92f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 Answer = new(0.60f, 0.85f, 0.70f, 1f);

    private readonly List<(string Verb, Vector4 Bounds)> _rows = [];
    private readonly List<(string Item, Vector4 Bounds)> _slots = [];

    private Vector4 _strip;

    /// <summary>Creates the interface over an overlay.</summary>
    /// <param name="overlay">Where it draws.</param>
    public GameHud(Overlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        Overlay = overlay;
    }

    /// <summary>The display list it fills in.</summary>
    public Overlay Overlay { get; private set; }

    /// <summary>
    /// What the game calls the player's things, in the player's own language.
    /// </summary>
    /// <remarks>
    /// Handed over rather than opened here, because which string table is read is a fact
    /// about the language the game was started in and the interface has no business opening
    /// archives. <see cref="Game.GameStrings.None"/> when there is no table, which is what
    /// this drew before it existed: the identifier, tidied up.
    /// </remarks>
    public Game.GameStrings Names { get; set; } = Game.GameStrings.None;

    /// <summary>
    /// How much bigger everything is than the layout was written against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every measurement here — padding, panel heights, the gap between inventory slots —
    /// is written in the units of a nineteen-pixel line, which is what <c>F_ARIAL_T12</c>
    /// gives, and multiplied by this. So changing the font changes the whole interface
    /// together rather than leaving 1999-sized gaps around 2026-sized letters.
    /// </para>
    /// <para>
    /// It is derived from the font rather than set, because a bitmap font has exactly one
    /// size: there is no scaling a sheet, and drawing a seventeen-pixel one at thirty-four
    /// pixels is a blurry seventeen-pixel one. Making the text bigger means picking a
    /// bigger sheet, and everything else follows from which sheet was picked.
    /// </para>
    /// </remarks>
    public float Scale => Math.Max(1f, Overlay.LineHeight / 19f);

    /// <summary>Draws the interface with a different font.</summary>
    /// <param name="atlas">The new atlas.</param>
    /// <remarks>
    /// For a window that changed size enough to want a different rung of the font ladder.
    /// The interface object survives it, because the room loop holds one and hands it the
    /// same instance every frame.
    /// </remarks>
    public void Retarget(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);

        Overlay = new Overlay(atlas);
        _rows.Clear();
        _slots.Clear();
    }

    /// <summary>How much of the foot of the screen the interface takes.</summary>
    /// <remarks>
    /// Nothing, now that the inventory strip is gone. Kept as a name because the captions
    /// sit above it and would otherwise have to learn that there is no longer an "it" — and
    /// because a strip may yet come back as something the player can turn on.
    /// </remarks>
    public static float InventoryHeight => 0f;

    /// <summary>Lays the interface out.</summary>
    /// <param name="state">What to show.</param>
    /// <param name="width">Width of the surface, in pixels.</param>
    /// <param name="height">Height of the surface, in pixels.</param>
    public void Build(HudState state, int width, int height)
    {
        Overlay.Begin(width, height);
        _rows.Clear();
        _slots.Clear();
        _buttons.Clear();
        _topics.Clear();

        Where(state, width);
        Headset(state);
        Gps(state, height);
        Hotspots(state, width, height);
        // The bar of what the player is carrying used to live along the foot of the screen.
        // It is gone: the right-click menu already says which of your things a noun will
        // take, so the strip listed the same items a second time and did it across exactly
        // the part of the screen where the floor at the player's feet is drawn — every click
        // there had to be tested against it first. The pockets are a key away and a screen of
        // their own, which is where a list of twelve things belongs.
        _strip = default;

        // Before the captions, which are laid out from the foot of the screen upwards and
        // have to start above it rather than under it.
        Prompt(state, width, height);
        Captions(state, width, height);

        // Last, so it is over everything: it is attached to the pointer and the pointer is
        // in front of the game by definition.
        if (state.MenuOpen && state.Verbs.Count > 0)
        {
            Menu(state, width, height);
        }
        else if (!state.RadioOpen)
        {
            // And not while the radio's list is up, for the reason the verb menu is not
            // drawn under one either: the label follows the pointer, the pointer is on the
            // list, and what it names is whatever is behind the list rather than the row
            // being pointed at.
            Pointing(state, width, height);
        }

        // The radio's own list, over the room and under the console. It hangs from a
        // button rather than from the pointer, so it does not compete with the verb menu
        // above and cannot be open at the same time as one.
        Radio(state, width, height);

        // Later still. The console is a different mode rather than a part of the interface,
        // and while it is up it is what the player is looking at.
        if (state.Console is { Open: true } console)
        {
            Terminal(console, width, height);
        }
    }

    /// <summary>
    /// The handheld GPS, when Grace has switched it on.
    /// </summary>
    /// <param name="state">What the game is doing.</param>
    /// <param name="height">Window height, which is what decides how big it is drawn.</param>
    /// <remarks>
    /// <para>
    /// <b>The picture is the device.</b> <c>GPSLER_L.BMP</c> is the whole handheld — bezel,
    /// green contour screen, and the words <c>LNG:</c> and <c>LAT:</c> printed on its face —
    /// so all this adds is the cross showing where Grace is standing and the two readings
    /// beside the labels the artists left room for. Everything is placed in the picture's own
    /// 222 by 332 pixels and scaled by one number, so the parts cannot drift apart.
    /// </para>
    /// <para>
    /// <b>Over the room, not in front of it.</b> The original's is the same: the player walks
    /// about with it up, watching the numbers change, which is how the cave is found. Top
    /// left, where nothing else in this interface goes.
    /// </para>
    /// </remarks>
    private void Gps(HudState state, int height)
    {
        if (state.Gps is not { } reading ||
            state.Pictures?.Invoke(reading.Map) is not { Drawn: true } device)
        {
            return;
        }

        // A third of the window's height, which is about what the original's takes on its
        // 480 lines. Grown where it has to be: the two readings are lettered in the
        // interface's own face rather than in the bitmap font the original shipped for
        // them, so the device is made big enough that they fit the space its artists left
        // between the printed labels and the right-hand edge.
        float room = MathF.Max(1f, device.Width - reading.Reading.X - 8f);
        float scale = MathF.Max(
            height / 3.2f / device.Height,
            Math.Max(
                Overlay.Measure(reading.Latitude),
                Overlay.Measure(reading.Longitude)) / room);

        // Under the top bar rather than over it: the bar says where the player is and what
        // they have scored, and the device is up for as long as they are walking about
        // with it.
        float margin = 8 * Scale;
        float top = Overlay.LineHeight + (10f * Scale) + margin;

        Overlay.Picture(
            device.Picture,
            margin,
            top,
            device.Width * scale,
            device.Height * scale,
            Vector4.One);

        // Where Grace is, as a cross the width of the screen and a box around the middle of
        // it — the original draws the same three pieces out of three tiny bitmaps.
        float x = margin + (reading.Across * scale);
        float y = top + (reading.Down * scale);
        float box = 10 * scale;

        Overlay.Rect(x, top + (10 * scale), 1, 205 * scale, Reticle);
        Overlay.Rect(margin + (11 * scale), y, 205 * scale, 1, Reticle);
        Overlay.Rect(x - box, y - box, box * 2, 1, Reticle);
        Overlay.Rect(x - box, y + box, box * 2, 1, Reticle);
        Overlay.Rect(x - box, y - box, 1, box * 2, Reticle);
        Overlay.Rect(x + box, y - box, 1, box * 2, Reticle);

        // And the two readings, beside the labels printed on the device itself.
        float text = margin + (reading.Reading.X * scale);

        Overlay.Text(reading.Longitude, text, top + (reading.Reading.Longitude * scale), Screen);
        Overlay.Text(reading.Latitude, text, top + (reading.Reading.Latitude * scale), Screen);
    }

    /// <summary>The cross on the GPS screen: dark, because the screen behind it is green.</summary>
    private static readonly Vector4 Reticle = new(0.05f, 0.16f, 0.07f, 0.85f);

    /// <summary>And what is written on it, in the same ink.</summary>
    private static readonly Vector4 Screen = new(0.04f, 0.13f, 0.06f, 1f);

    /// <summary>
    /// The developer console.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Across the top rather than the bottom, because the inventory strip and the captions
    /// both live along the bottom edge and a console over either of them would hide the
    /// thing a command was about to change.
    /// </para>
    /// <para>
    /// The completion list hangs below the input line and is the reason the console is worth
    /// having: the command language is the game's own 139 Sheep functions, and nobody can be
    /// expected to know their names. Each row carries its prototype, so the arguments are
    /// visible before they are typed rather than after they are wrong.
    /// </para>
    /// </remarks>
    private void Terminal(GameConsole console, int width, int height)
    {
        float unit = Scale;
        float row = Overlay.LineHeight;
        float margin = 10f * unit;

        // Enough for the scrollback, an input line and a full completion list, or half the
        // screen — whichever is less. A console covering the room it is being used on is a
        // console that has to be closed to see what it did.
        float panel = Math.Min(height * 0.5f, (row * 14) + (24f * unit));
        float input = panel - row - (10f * unit);

        Overlay.Rect(0, 0, width, panel, Console);
        Overlay.Rect(0, panel - 1, width, 1, Accent);

        // The scrollback, newest at the bottom against the input line, which is where the
        // eye already is.
        int fits = Math.Max(0, (int)((input - margin) / row));
        int from = Math.Max(0, console.Lines.Count - fits);

        for (int i = from; i < console.Lines.Count; i++)
        {
            ConsoleLine line = console.Lines[i];

            Overlay.Text(
                line.Kind == ConsoleLineKind.Echo ? "> " + line.Text : line.Text,
                margin + (4 * unit),
                margin + (row * (i - from)),
                line.Kind switch
                {
                    ConsoleLineKind.Complaint => Complaint,
                    ConsoleLineKind.Result => Answer,
                    ConsoleLineKind.Echo => Ink,
                    _ => Dim,
                });
        }

        Overlay.Rect(0, input - (2 * unit), width, 1, Rule);

        float caret = Overlay.Text("> ", margin + (4 * unit), input + (4 * unit), Accent);
        caret = Overlay.Text(console.Typed, caret, input + (4 * unit), Ink);

        // A block rather than a bar, and not blinking. There is no clock in this layer —
        // ADR 0004 keeps wall time in the platform — and a caret that blinks would need
        // one.
        Overlay.Rect(caret + 1, input + (4 * unit), 8 * unit, row - (6 * unit), Accent);

        if (console.Completions.Count == 0)
        {
            return;
        }

        // Widest prototype plus a margin, so the list is a column rather than a ragged edge.
        float widest = 0;

        foreach (Completion completion in console.Completions)
        {
            widest = Math.Max(widest, Overlay.Measure(completion.Signature));
        }

        float listWidth = Math.Min(width - (margin * 2), widest + (28f * unit));
        float listHeight = (row * console.Completions.Count) + (10f * unit);
        float listY = panel + (2 * unit);

        Overlay.Rect(margin, listY, listWidth, listHeight, Console);
        Overlay.Rect(margin, listY, 3 * unit, listHeight, Accent);

        for (int i = 0; i < console.Completions.Count; i++)
        {
            float y = listY + (5 * unit) + (row * i);
            bool chosen = i == console.Chosen;

            if (chosen)
            {
                Overlay.Rect(
                    margin + (3 * unit), y - (2 * unit), listWidth - (3 * unit), row, PanelLit);
            }

            Overlay.Text(
                console.Completions[i].Signature,
                margin + (12 * unit),
                y,
                chosen ? Accent : Dim);
        }
    }

    /// <summary>Which verb is at a point, if the menu is open.</summary>
    /// <param name="point">Where the player clicked, in pixels.</param>
    /// <returns>The verb, or null.</returns>
    public string? VerbAt(Vector2 point)
    {
        foreach ((string verb, Vector4 bounds) in _rows)
        {
            if (Inside(point, bounds))
            {
                return verb;
            }
        }

        return null;
    }

    /// <summary>Which row of the open menu a point is on.</summary>
    /// <param name="point">Where the pointer is, in pixels.</param>
    /// <returns>The row's index, or -1 when the point is not on one.</returns>
    /// <remarks>
    /// So that moving the pointer over a row can move the selection the wheel also moves.
    /// Both end up pointing at the same thing, which is what stops the highlight and the
    /// click from disagreeing.
    /// </remarks>
    public int RowAt(Vector2 point)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (Inside(point, _rows[i].Bounds))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>What the row at an index is, or null when there is no such row.</summary>
    /// <param name="index">Which row.</param>
    /// <returns>Its verb, its item, or <see cref="UseRow"/>.</returns>
    /// <remarks>
    /// The menu is no longer one row per verb: it carries a row that opens the bag and a
    /// row for each thing in it. So what a row means has to be asked rather than looked up
    /// by position in the verb list, which was only ever right while the two agreed.
    /// </remarks>
    public string? RowNamed(int index) =>
        index >= 0 && index < _rows.Count ? _rows[index].Verb : null;

    /// <summary>How many rows the menu is showing.</summary>
    public int RowCount => _rows.Count;

    /// <summary>The middle of a menu row, in pixels.</summary>
    /// <param name="index">Which row.</param>
    /// <returns>Its centre, or the origin when there is no such row.</returns>
    /// <remarks>
    /// So that "what was drawn" and "what answers to a click" can be checked against one
    /// another without a mouse. Nothing in the game calls it.
    /// </remarks>
    public Vector2 RowMiddle(int index) =>
        index >= 0 && index < _rows.Count ? Middle(_rows[index].Bounds) : Vector2.Zero;

    /// <summary>The middle of an inventory slot, in pixels.</summary>
    /// <param name="item">The item it holds.</param>
    /// <returns>Its centre, or the origin when the strip is not showing it.</returns>
    public Vector2 SlotMiddle(string item)
    {
        ArgumentNullException.ThrowIfNull(item);

        foreach ((string held, Vector4 bounds) in _slots)
        {
            if (held.Equals(item, StringComparison.OrdinalIgnoreCase))
            {
                return Middle(bounds);
            }
        }

        return Vector2.Zero;
    }

    private static Vector2 Middle(Vector4 bounds) =>
        new(bounds.X + (bounds.Z / 2f), bounds.Y + (bounds.W / 2f));

    /// <summary>Whether a point is on the interface rather than on the room behind it.</summary>
    /// <param name="point">Where the player clicked, in pixels.</param>
    /// <returns>True when the interface is what was clicked.</returns>
    /// <remarks>
    /// The inventory strip is an opaque bar across the foot of the screen and it is always
    /// there. Without this a click on it goes through to whatever the ray finds behind it,
    /// which is nearly always the floor at the player's feet — so putting the pointer on
    /// the interface would walk them.
    /// </remarks>
    public bool OverInterface(Vector2 point) =>
        Inside(point, _strip) ||
        ButtonAt(point) is { Length: > 0 } ||
        TopicAt(point) >= 0;

    /// <summary>Which inventory item is at a point.</summary>
    /// <param name="point">Where the player clicked, in pixels.</param>
    /// <returns>The item, or null.</returns>
    public string? ItemAt(Vector2 point)
    {
        foreach ((string item, Vector4 bounds) in _slots)
        {
            if (Inside(point, bounds))
            {
                return item;
            }
        }

        return null;
    }

    private static bool Inside(Vector2 point, Vector4 bounds) =>
        point.X >= bounds.X && point.X <= bounds.X + bounds.Z &&
        point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.W;

    /// <summary>The corner that says where you are, and what you have scored.</summary>
    /// <remarks>
    /// The score goes at the other end of the same bar rather than in a corner of its own.
    /// It is the game's own line — <c>ScoreText = Score: %03d of %03d</c> out of the string
    /// table — so it reads the way the original's toolbar read.
    /// </remarks>
    private void Where(HudState state, int width)
    {
        float unit = Scale;
        float height = Overlay.LineHeight + (10f * unit);

        Overlay.Rect(0, 0, width, height, Panel);
        Overlay.Rect(0, height - 1, width, 1, Rule);

        Overlay.Text(state.Place, 12 * unit, 5 * unit, Dim);

        float right = width - (12 * unit);

        if (state.Score is { Length: > 0 } score)
        {
            right -= Overlay.Measure(score);
            Overlay.Text(score, right, 5 * unit, Dim);
            right -= 20 * unit;
        }

        // The two screens a player opens by hand, where the eye already goes for the score.
        // Both have a key — I and J — and a key nobody is told about is a key nobody presses,
        // which is how the quest log came to be a feature with no way in.
        right = Button(state, "Journal", right, height, unit, "open:journal");
        Button(state, "Pockets", right, height, unit, "open:inventory");
    }

    /// <summary>One word in the top bar that answers to a click.</summary>
    /// <returns>Where the next one to its left should end.</returns>
    private float Button(
        HudState state, string label, float right, float height, float unit, string id)
    {
        float wide = Overlay.Measure(label) + (16 * unit);
        var bounds = new Vector4(right - wide, 2 * unit, wide, height - (5 * unit));

        bool under = Inside(state.At, bounds);

        Overlay.Rect(bounds.X, bounds.Y, bounds.Z, bounds.W, under ? PanelLit : Panel);
        Overlay.Text(label, bounds.X + (8 * unit), 5 * unit, under ? Accent : Dim);

        _buttons.Add((id, bounds));

        return bounds.X - (8 * unit);
    }

    private readonly List<(string Id, Vector4 Bounds)> _buttons = [];

    private readonly List<(string Noun, Vector4 Bounds)> _topics = [];

    /// <summary>What the headset button is called when it is clicked.</summary>
    public const string RadioButton = "open:radio";

    /// <summary>And what the room's own button is called.</summary>
    public const string PromptButton = "do:mechanism";

    /// <summary>How much of the foot of the screen the room's button took.</summary>
    private float _reserved;

    /// <summary>
    /// The one thing the room is asking the player to do, across the foot of the picture.
    /// </summary>
    /// <param name="state">What the game is doing.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <remarks>
    /// <para>
    /// <b>Deliberately the largest thing on the screen.</b> Every other control here is a
    /// label the player finds by pointing at the room; this one exists because there is
    /// something to do that pointing at the room will not find, so it goes where the eye
    /// already is at a size that cannot be missed — centred, low, and as wide as its own
    /// words plus a wide margin.
    /// </para>
    /// <para>
    /// Dim while it may not be pressed, and registered as a button either way: a control
    /// that vanishes between presses reads as a bug, and one that quietly swallows the
    /// press reads as a worse one. The mechanism decides which state it is in and
    /// <see cref="Game.Mechanisms.SceneMechanism.Press"/> decides what a press does.
    /// </para>
    /// </remarks>
    private void Prompt(HudState state, int width, int height)
    {
        _reserved = 0f;

        if (state.Prompt is not { Verb.Length: > 0 } asked)
        {
            return;
        }

        float unit = Scale;
        float row = Overlay.LineHeight;
        float h = row + (22f * unit);
        float w = Math.Min(
            width - (48f * unit),
            Math.Max(220f * unit, Overlay.Measure(asked.Verb) + (72f * unit)));

        float x = (width - w) / 2f;
        float y = height - InventoryHeight - h - (24f * unit);

        bool under = Inside(state.At, new Vector4(x, y, w, h));

        Overlay.Rect(x, y, w, h, asked.Ready && under ? PanelLit : Panel);

        // A rule along the top edge rather than a border all the way round: the same accent
        // the caption panel wears, so the two read as one interface rather than as a game
        // with a dialog box over it.
        Overlay.Rect(x, y, w, 3 * unit, asked.Ready ? Accent : Rule);

        Overlay.Text(
            asked.Verb,
            x + ((w - Overlay.Measure(asked.Verb)) / 2f),
            y + (11f * unit),
            asked.Ready ? Accent : Dim);

        _buttons.Add((PromptButton, new Vector4(x, y, w, h)));

        // What the captions have to keep clear of, the gap under it included.
        _reserved = h + (24f * unit);
    }

    /// <summary>How big the headset is drawn, in units of a line.</summary>
    /// <remarks>
    /// Half again the height of the bar it hangs under. It was inside the bar first, at the
    /// height of a row, and was too easy to miss - a control the player has to notice is
    /// there at all cannot be the smallest thing on the screen. The art is a 32-pixel
    /// square, so this is a mild enlargement at the smallest font rung and native size or
    /// better at every rung above it.
    /// </remarks>
    private const float HeadsetSide = 44f;

    /// <summary>Where the headset sits, whether or not it is drawn.</summary>
    /// <param name="unit">The scale everything here is measured in.</param>
    /// <returns>Its square.</returns>
    /// <remarks>
    /// Under the bar rather than in it. The bar is a row of words about where the player is;
    /// this is a thing Gabriel is wearing that they can pick up and use, so it stands over
    /// the room on the room's own margin, at a size that says it can be pressed.
    /// </remarks>
    private Vector4 HeadsetBounds(float unit)
    {
        float bar = Overlay.LineHeight + (10f * unit);
        float side = HeadsetSide * unit;

        return new Vector4(12 * unit, bar + (10 * unit), side, side);
    }

    /// <summary>
    /// The headset Gabriel wears in the temple, under the top bar at the left.
    /// </summary>
    /// <param name="state">What the game is doing.</param>
    /// <remarks>
    /// <para>
    /// <b>The picture is the original's.</b> <c>RC_RADIO_STD</c> and its hover, down and
    /// disabled states are the four the game's own option bar used for this button -
    /// <c>RC_LAYOUT.TXT</c> names them - so a returning player is looking at the thing they
    /// already know. It is a headset with a boom microphone, drawn at a size that is a
    /// function of the font, which is a function of the window.
    /// </para>
    /// <para>
    /// <b>Under the bar and half again its height.</b> Inside it, beside the room's name, it
    /// read as another label rather than as something to press, and at a row's height it was
    /// the smallest thing on the screen.
    /// </para>
    /// <para>
    /// Dim rather than absent when there is nothing to ask. The headset is on his head for
    /// the whole hour whatever room he is in, and a button that came and went would be
    /// telling the player which rooms have something in them worth asking about.
    /// </para>
    /// </remarks>
    private void Headset(HudState state)
    {
        if (!state.RadioWorn)
        {
            return;
        }

        float unit = Scale;
        Vector4 bounds = HeadsetBounds(unit);

        bool ready = state.Radio is { Count: > 0 };
        bool under = Inside(state.At, bounds);

        // A ground behind it, because it stands over the room rather than over the bar, and
        // the room it stands over is dark stone under a picture of mostly dark stone.
        float pad = 3 * unit;

        Overlay.Rect(
            bounds.X - pad,
            bounds.Y - pad,
            bounds.Z + (pad * 2),
            bounds.W + (pad * 2),
            under && ready ? PanelLit : Panel);

        // Named with the extension, because that is what reads a file out of the archives.
        string art = !ready ? "RC_RADIO_DIS.BMP"
            : state.RadioOpen ? "RC_RADIO_DWN.BMP"
            : under ? "RC_RADIO_HOV.BMP"
            : "RC_RADIO_STD.BMP";

        if (state.Pictures?.Invoke(art) is { Drawn: true } picture)
        {
            Vector4 at = picture.Fit(bounds.X, bounds.Y, bounds.Z);

            Overlay.Picture(picture.Picture, at.X, at.Y, at.Z, at.W, Vector4.One);
        }
        else
        {
            // The art is in every copy of the game, so this is not a fallback anybody should
            // see. It is here because a button nobody can find is worse than an ugly one,
            // and because the archives are the player's rather than ours.
            Overlay.Text(
                "Grace",
                bounds.X + (4 * unit),
                bounds.Y + ((bounds.W - Overlay.LineHeight) / 2),
                ready ? Ink : Dim);
        }

        // Registered whether or not there is anything to say. A dim button that answers a
        // click with an empty list is a button; one that swallows the click is a bug.
        _buttons.Add((RadioButton, bounds));
    }

    /// <summary>
    /// The things Gabriel can raise with Grace, under the headset that opens them.
    /// </summary>
    /// <param name="state">What the game is doing.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <remarks>
    /// Laid out like the verb menu and hit-tested the same way, because it is the same
    /// gesture: a short list of things to do, one click to take one. It hangs from the
    /// button rather than from the pointer — the button is where the player just clicked and
    /// a list that opened somewhere else would be a list they have to go and find.
    /// </remarks>
    private void Radio(HudState state, int width, int height)
    {
        if (!state.RadioOpen || state.Radio is not { Count: > 0 } topics)
        {
            return;
        }

        float unit = Scale;
        float padding = 8f * unit;
        Vector4 button = HeadsetBounds(unit);
        float bar = button.Y + button.W + (6 * unit);

        const string Heading = "Grace";
        float w = Overlay.Measure(Heading);

        foreach (Game.RadioTopic topic in topics)
        {
            w = Math.Max(w, Overlay.Measure(Pretty(topic.Label)));
        }

        w += padding * 2;

        float row = Overlay.LineHeight + (8f * unit);
        float title = Overlay.LineHeight + (8f * unit);
        float h = title + (row * topics.Count) + padding;

        // The same answer the verb menu gives a character with thirty topics: shorter rows
        // rather than rows under the bottom of the screen, where they cannot be clicked.
        if (h > height - bar && topics.Count > 0)
        {
            row = Math.Max(
                Overlay.LineHeight, (height - bar - title - padding) / topics.Count);

            h = title + (row * topics.Count) + padding;
        }

        float x = Math.Clamp(button.X, 0, Math.Max(0, width - w));
        float y = bar;

        Overlay.Rect(x, y, w, h, PanelLit);
        Overlay.Text(Heading, x + padding, y + (4 * unit), Accent);
        Overlay.Rect(x, y + title, w, 1, Rule);

        for (int i = 0; i < topics.Count; i++)
        {
            float top = y + title + (row * i);
            var bounds = new Vector4(x, top, w, row);

            bool chosen = i == state.RadioIndex;

            if (chosen)
            {
                Overlay.Rect(x, top, w, row, new Vector4(0.28f, 0.31f, 0.37f, 1f));
                Overlay.Rect(x, top, 2 * unit, row, Accent);
            }

            Overlay.Text(
                Pretty(topics[i].Label),
                x + padding,
                top + ((row - Overlay.LineHeight) / 2),
                chosen ? Accent : Ink);

            _topics.Add((topics[i].Noun, bounds));
        }
    }

    /// <summary>Which radio topic is at a point.</summary>
    /// <param name="point">Where the pointer is.</param>
    /// <returns>Its index, or -1.</returns>
    public int TopicAt(Vector2 point)
    {
        for (int i = 0; i < _topics.Count; i++)
        {
            if (Inside(point, _topics[i].Bounds))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>How many topics the list drew.</summary>
    public int TopicCount => _topics.Count;

    /// <summary>Which of the top bar's buttons is at a point, if any.</summary>
    /// <param name="point">Where the pointer is.</param>
    /// <returns>What it opens, or null.</returns>
    public string? ButtonAt(Vector2 point)
    {
        foreach ((string id, Vector4 bounds) in _buttons)
        {
            if (Inside(point, bounds))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Every hotspot in the room at once, while the key that asks is held.
    /// </summary>
    /// <param name="state">What the game is doing, including where each noun is.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <remarks>
    /// <para>
    /// A 1999 adventure game hides what can be clicked and expects the player to sweep the
    /// pointer across the furniture until something lights up. Holding a key answers the
    /// question outright, which is what <c>Plan/03</c> section 3 means by an interface easier
    /// than the original's.
    /// </para>
    /// <para>
    /// <b>Laid out so that no two labels overlap.</b> Rooms put a dozen nouns within a few
    /// degrees of each other — a desk, its drawer, the register on it, the bell beside it —
    /// and a heap of labels on the same spot answers nothing. Each is pushed down until it
    /// clears the ones already placed, in order of depth so the nearest keeps its own place
    /// and the ones behind give way. A label with nowhere left to go is dropped rather than
    /// stacked, because a wrong label is worse than a missing one.
    /// </para>
    /// </remarks>
    private void Hotspots(HudState state, int width, int height)
    {
        if (state.Hotspots is not { Count: > 0 } spots)
        {
            return;
        }

        float unit = Scale;
        float row = Overlay.LineHeight + (6 * unit);
        float bar = Overlay.LineHeight + (14f * unit);

        List<Vector4> taken = [];

        foreach ((string noun, Vector2 at) in spots)
        {
            string label = Pretty(noun);
            float wide = Overlay.Measure(label) + (12 * unit);

            float x = Math.Clamp(at.X - (wide / 2), 0, Math.Max(0, width - wide));
            float y = Math.Clamp(at.Y - (row / 2), bar, Math.Max(bar, height - row));

            // Down until it clears everything already placed. Down rather than sideways
            // because a label that moves along the wall stops pointing at the thing.
            bool room = true;

            while (taken.Exists(b => Overlaps(b, x, y, wide, row)))
            {
                y += row + (2 * unit);

                if (y + row > height)
                {
                    room = false;
                    break;
                }
            }

            if (!room)
            {
                continue;
            }

            taken.Add(new Vector4(x, y, wide, row));

            Overlay.Rect(x, y, wide, row, Panel);
            Overlay.Rect(x, y, wide, 1, Rule);
            Overlay.Text(label, x + (6 * unit), y + (3 * unit), Ink);
        }
    }

    /// <summary>Whether a proposed label would sit on one already placed.</summary>
    private static bool Overlaps(Vector4 placed, float x, float y, float wide, float tall) =>
        x < placed.X + placed.Z && x + wide > placed.X &&
        y < placed.Y + placed.W && y + tall > placed.Y;

    /// <summary>
    /// The label that follows the pointer.
    /// </summary>
    /// <remarks>
    /// The one piece of the interface that has to be right. It says, in words, what a click
    /// will do, before the click — which is the whole difference between this and hunting
    /// for a hotspot.
    /// </remarks>
    private void Pointing(HudState state, int width, int height)
    {
        if (state.Noun is not { Length: > 0 } noun)
        {
            return;
        }

        string subject = Pretty(noun);
        string? action = state.Verb is { Length: > 0 } verb ? Pretty(verb) : null;

        // Two runs rather than one string with a separator in it. GK3's fonts are Latin-1
        // and have no dash beyond the hyphen, so anything typographic comes out as the
        // font's box-shaped stand-in — and colour separates them better than punctuation
        // would anyway.
        float unit = Scale;
        float w = Overlay.Measure(subject) + (16f * unit);

        if (action is not null)
        {
            w += Overlay.Measure(action) + Overlay.Measure("  ");
        }

        float h = Overlay.LineHeight + (10f * unit);

        // Kept on screen: a label that runs off the right edge is worse than one that stops
        // following the pointer for the last few pixels.
        float x = Math.Clamp(state.At.X + (18 * unit), 0, Math.Max(0, width - w));
        // Below the top bar, never over it. The bar carries the place, the score and the two
        // buttons, and a label that lands on them hides all three at once.
        float bar = Overlay.LineHeight + (14f * unit);

        float y = Math.Clamp(state.At.Y + (18 * unit), bar, Math.Max(bar, height - h));

        Overlay.Rect(x, y, w, h, Panel);
        Overlay.Rect(x, y, 2 * unit, h, action is not null ? Accent : Rule);

        float pen = Overlay.Text(
            subject, x + (10 * unit), y + (5 * unit), action is not null ? Ink : Dim);

        if (action is not null)
        {
            Overlay.Text(action, pen + Overlay.Measure("  "), y + (5 * unit), Accent);
        }
    }

    /// <summary>
    /// The full list of verbs, where the pointer was when they were asked for.
    /// </summary>
    /// <remarks>
    /// Anchored to <see cref="HudState.MenuAt"/> and not to where the pointer is now. The
    /// two are only the same on the frame it opened, and using the live position means the
    /// menu moves with the hand reaching for it and can never be clicked.
    /// </remarks>
    private void Menu(HudState state, int width, int height)
    {
        float unit = Scale;
        float padding = 8f * unit;

        // The heading counts. It is the noun the player right-clicked, and a noun is very
        // often longer than any verb offered for it — "Coffee Pot" over Look and Pour —
        // so measuring only the verbs sizes the panel to the wrong thing and the heading
        // runs off the end of its own background.
        string heading = Pretty(state.Noun ?? string.Empty);
        float w = Overlay.Measure(heading);

        // The verbs, and then one row standing for everything in the bag that this noun
        // answers to. Those are items rather than verbs and there can be thirty of them, so
        // they go in a column of their own that opens when the row is selected.
        IReadOnlyList<string> items = state.Items ?? [];
        List<string> rows = [.. state.Verbs];

        if (items.Count > 0)
        {
            rows.Add(UseRow);
        }

        bool opening = items.Count > 0 && state.MenuIndex >= rows.Count - 1;

        // The original's verb icons are 32 pixels square, so a row with room for one at the
        // size it was painted is the one arrangement that does not resample them. A window
        // big enough to want a larger font gets larger icons with it, because everything
        // here is measured in units of a line and half-sized art beside doubled letters
        // reads as a mistake rather than as a choice.
        float badge = state.VerbIcons is null ? 0 : 32f * unit;
        float row = Math.Max(Overlay.LineHeight + (8f * unit), badge + (6f * unit));
        float title = Overlay.LineHeight + (8f * unit);
        float h = title + (row * rows.Count) + padding;

        // Somebody with thirty topics to raise gets smaller icons rather than a list whose
        // last rows are under the bottom of the screen, where they cannot be clicked at all.
        if (h > height && rows.Count > 0)
        {
            row = Math.Max(
                Overlay.LineHeight + (8f * unit), (height - title - padding) / rows.Count);
            badge = Math.Min(badge, Math.Max(0, row - (6f * unit)));
            h = title + (row * rows.Count) + padding;
        }

        // How far the words are pushed in to clear the picture. Every row shares it,
        // including the ones with no picture to draw, so the verbs read as a column.
        float indent = badge > 0 ? badge + (6f * unit) : 0;

        foreach (string verb in rows)
        {
            w = Math.Max(w, indent + Overlay.Measure(Label(verb)));
        }

        // The same padding either side of whatever turned out to be widest.
        w += padding * 2;

        float x = Math.Clamp(state.MenuAt.X, 0, Math.Max(0, width - w));
        float y = Math.Clamp(state.MenuAt.Y, 0, Math.Max(0, height - h));

        Overlay.Rect(x, y, w, h, PanelLit);
        Overlay.Text(heading, x + padding, y + (4 * unit), Accent);
        Overlay.Rect(x, y + title, w, 1, Rule);

        for (int i = 0; i < rows.Count; i++)
        {
            float top = y + title + (row * i);
            var bounds = new Vector4(x, top, w, row);

            bool chosen = i == state.MenuIndex;

            if (chosen)
            {
                Overlay.Rect(x, top, w, row, new Vector4(0.28f, 0.31f, 0.37f, 1f));
                Overlay.Rect(x, top, 2 * unit, row, Accent);
            }

            // Lit art for the row the player has picked out. It is the second thing the
            // original's ring did with these pictures, and it says which row a click takes
            // in the icons themselves rather than only in the bar behind them.
            if (badge > 0 &&
                state.VerbIcons?.Invoke(rows[i], chosen) is { Drawn: true } picture)
            {
                Vector4 at = picture.Fit(x + padding, top + ((row - badge) / 2), badge);

                Overlay.Picture(picture.Picture, at.X, at.Y, at.Z, at.W, Vector4.One);
            }

            Overlay.Text(
                Label(rows[i]),
                x + padding + indent,
                top + ((row - Overlay.LineHeight) / 2),
                chosen ? Accent : Ink);
            _rows.Add((rows[i], bounds));
        }

        if (!opening)
        {
            return;
        }

        // The second column, beside the first rather than over it, so the row that opened
        // it stays visible and the player can see what they are choosing between.
        // A picture beside each name, at the height of its own row. The column is where the
        // player picks which of their things to use, and a name alone asks them to remember
        // what "Coordinate Fixing Device" looks like.
        float art = state.Icons is null ? 0 : row - (6 * unit);
        float itemWidth = padding * 2;

        foreach (string item in items)
        {
            itemWidth = Math.Max(itemWidth, Overlay.Measure(Owned(item)) + (padding * 2) + art);
        }

        float itemHeight = (row * items.Count) + padding;
        float itemX = Math.Clamp(x + w + (2 * unit), 0, Math.Max(0, width - itemWidth));
        float itemY = Math.Clamp(
            y + title + (row * (rows.Count - 1)), 0, Math.Max(0, height - itemHeight));

        Overlay.Rect(itemX, itemY, itemWidth, itemHeight, PanelLit);

        for (int i = 0; i < items.Count; i++)
        {
            float top = itemY + (row * i);
            var bounds = new Vector4(itemX, top, itemWidth, row);

            bool chosen = rows.Count + i == state.MenuIndex;

            if (chosen)
            {
                Overlay.Rect(itemX, top, itemWidth, row, new Vector4(0.28f, 0.31f, 0.37f, 1f));
                Overlay.Rect(itemX, top, 2 * unit, row, Accent);
            }

            if (state.Icons?.Invoke(items[i]) is { Drawn: true } icon)
            {
                Vector4 at = icon.Fit(itemX + padding, top + (3 * unit), art);

                Overlay.Picture(icon.Picture, at.X, at.Y, at.Z, at.W, Vector4.One);
            }

            Overlay.Text(
                Owned(items[i]),
                itemX + padding + art,
                top + ((row - Overlay.LineHeight) / 2),
                chosen ? Accent : Ink);

            _rows.Add((items[i], bounds));
        }
    }

    /// <summary>The row that stands for "use something on this".</summary>
    /// <remarks>
    /// A sentinel rather than a verb, because no verb in the game is spelt with a control
    /// character and this one must never be mistaken for something the player can perform.
    /// </remarks>
    public const string UseRow = "\u0001use";

    /// <summary>What a menu row reads as.</summary>
    private static string Label(string verb) =>
        verb == UseRow ? "Use..." : Pretty(verb);

    /// <summary>
    /// What one of the player's things reads as.
    /// </summary>
    /// <remarks>
    /// The game's own name for it where there is one — "Tape of Abbé's phone call" rather
    /// than "Abbe Tape", and "Jumelles" rather than "Binoculars" in a French game — and the
    /// tidied identifier otherwise. It is the only per-object text GK3 ever localised; see
    /// <see cref="Game.GameStrings.Item"/>.
    /// </remarks>
    private string Owned(string item) => Names.Item(item) ?? Pretty(item);

    /// <summary>
    /// The strip along the bottom, which is no longer drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It duplicated the right-click menu, which already says which of the player's things a
    /// noun will take, and it lay across the foot of the screen — exactly where the floor at
    /// the player's feet is drawn, so every click on the ground in front of you had to be
    /// tested against it first and a good many were swallowed.
    /// </para>
    /// <para>
    /// Kept rather than deleted. It is the layout for a strip if one is ever wanted as
    /// something the player can turn on, and deleting it to write it again would be the
    /// worse trade.
    /// </para>
    /// </remarks>
    private void Inventory(HudState state, int width, int height)
    {
        float unit = Scale;
        float h = InventoryHeight;
        float y = height - h;

        Overlay.Rect(0, y, width, h, Panel);
        Overlay.Rect(0, y, width, 1, Rule);

        _strip = new Vector4(0, y, width, h);

        // No count and no "carrying nothing": the row of items says both, and an empty
        // row says the empty case better than a sentence about it does.
        float x = 12 * unit;

        foreach (string item in state.Inventory)
        {
            string name = Owned(item);
            float w = Overlay.Measure(name) + (16 * unit);

            if (x + w > width - (12 * unit))
            {
                break;
            }

            bool held = string.Equals(item, state.Held, StringComparison.OrdinalIgnoreCase);
            var bounds = new Vector4(x, y + (4 * unit), w, h - (8 * unit));

            Overlay.Rect(x, y + (4 * unit), w, h - (8 * unit), held ? PanelLit : Panel);
            Overlay.Rect(x, y + (4 * unit), w, 1, held ? Accent : Rule);
            Overlay.Text(name, x + (8 * unit), y + (7 * unit), held ? Accent : Ink);

            _slots.Add((item, bounds));
            x += w + (6 * unit);
        }
    }

    /// <summary>What is being said, along the bottom above the inventory.</summary>
    private void Captions(HudState state, int width, int height)
    {
        if (state.Caption is not { Length: > 0 } caption)
        {
            return;
        }

        float unit = Scale;
        float row = Overlay.LineHeight;
        float margin = 48f * unit;
        float usable = Math.Max(64f, width - (margin * 2) - (24f * unit));

        List<string> lines = Wrap(caption, usable);
        float h = (row * lines.Count) + (20f * unit);
        float y = height - InventoryHeight - _reserved - h - (12f * unit);

        Overlay.Rect(margin, y, width - (margin * 2), h, Panel);
        Overlay.Rect(margin, y, 3 * unit, h, Accent);

        for (int i = 0; i < lines.Count; i++)
        {
            Overlay.Text(lines[i], margin + (14 * unit), y + (10 * unit) + (row * i), Ink);
        }

        // GK3 writes UNKNOWN for a line with nobody on screen saying it — Gabriel's own
        // narration, mostly. Writing "Unknown" over it is worse than writing nothing.
        if (state.Speaker is { Length: > 0 } speaker &&
            !speaker.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            Overlay.Text(Pretty(speaker), margin + (14 * unit), y - row - (2 * unit), Accent);
        }
    }

    /// <summary>Breaks a line of dialogue to fit the width it is given.</summary>
    /// <remarks>
    /// On spaces only. GK3's captions are ordinary prose and a word long enough to need
    /// breaking mid-word does not occur in them; splitting one would look worse than
    /// letting it overhang.
    /// </remarks>
    private List<string> Wrap(string text, float width)
    {
        List<string> lines = [];
        string current = string.Empty;

        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;

            if (Overlay.Measure(candidate) > width && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines.Count > 0 ? lines : [text];
    }

    /// <summary>
    /// Turns an internal name into something a player can read.
    /// </summary>
    /// <remarks>
    /// The action files are written in shouting with underscores —
    /// <c>BATHROOM_DOOR</c>, <c>T_GABRIEL</c> — because they are identifiers. Showing them
    /// raw is the single loudest way an interface can say it was built for its authors.
    /// </remarks>
    private static string Pretty(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        // Small talk, which the data spells Z_CHAT so that it sorts to the end of a list.
        // Left alone it reads as "Z Chat" beside "Talk", which is two ways of saying the
        // same thing and one of them nonsense.
        if (name.Equals("Z_CHAT", StringComparison.OrdinalIgnoreCase))
        {
            return "Chat";
        }

        string text = name.Replace('_', ' ').Trim();

        // Something already written for a person to read is left exactly as it is. The
        // nouns and verbs in the data are shouted — FRONT_DOOR, GO_UP — and want title
        // case; a name out of the game's own string table is not, and recasing
        // "Rennes-le-Chateau: Outside Church" gives it a lower-case C in the middle of a
        // place name.
        if (text.Any(char.IsLower))
        {
            // Its own casing is kept, because recasing "Rennes-le-Chateau: Outside Church"
            // gives it a lower-case C in the middle of a place name. Only the first letter
            // is decided here, and only when the data left it lower: the string table has
            // "bed" in it, and a label that reads "bed" looks like a mistake whatever the
            // reason for it.
            return char.IsLower(text[0])
                ? char.ToUpperInvariant(text[0]) + text[1..]
                : text;
        }

        // A topic is a thing to talk about rather than a thing to do.
        if (text.StartsWith("T ", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
        {
            text = "ask about " + text[2..];
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            bool start = true;

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];

                span[i] = start ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
                start = c == ' ';
            }
        });
    }
}
