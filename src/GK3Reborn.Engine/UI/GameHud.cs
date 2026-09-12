using System.Globalization;
using System.Numerics;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI;

/// <summary>What the interface is being asked to show this frame.</summary>
/// <param name="Noun">What the pointer is over, or null.</param>
/// <param name="Verbs">What that answers to, most likely first.</param>
/// <param name="Verb">The verb a plain click would perform.</param>
/// <param name="Hotspots">Every noun in the room and where it is on screen, while the player is holding the key that asks.</param>
/// <param name="At">Where the pointer is, in pixels.</param>
/// <param name="MenuOpen">Whether the player asked for the full list of verbs.</param>
/// <param name="MenuIndex">Which verb is chosen.</param>
/// <param name="MenuAt">Where the pointer was when the list was asked for.</param>
/// <param name="Speaker">Who is talking, or null.</param>
/// <param name="Caption">What they are saying, or null.</param>
/// <param name="Inventory">What the player is carrying.</param>
/// <param name="Held">Which of it is in hand, or null.</param>
/// <param name="InventoryOpen">Whether the inventory is showing.</param>
/// <param name="Place">Where this is, for the corner.</param>
/// <param name="Console">The developer console, when it is showing.</param>
/// <param name="Score">What the player has scored, already written out, or null.</param>
/// <param name="Items">The things in the bag this noun answers to, which the menu offers behind one row rather than listing beside the.</param>
/// <param name="Icons">The game's own picture of an item, by item name, for the column that lists them.</param>
/// <param name="VerbIcons">The game's own picture of a verb, by verb and by whether that verb is the one picked out.</param>
/// <param name="Gps">What the handheld GPS is showing, when Grace has one out and switched on.</param>
/// <param name="Pictures">The game's own art by file name, for the pieces of the interface that are a picture rather than a drawing.</param>
/// <param name="RadioWorn">Whether Gabriel has the headset on, which is the whole of what decides that the button is drawn.</param>
/// <param name="Radio">What the room will answer to over the radio, here and now.</param>
/// <param name="RadioOpen">Whether the list of topics is showing.</param>
/// <param name="RadioIndex">Which of them is picked out.</param>
/// <param name="Prompt">The one control the room itself is asking for, drawn large under the picture.</param>
/// <param name="Crosshair">Whether to mark the middle of the screen, which is what the player is looking at and so what a click acts on.</param>
public readonly record struct HudState( string? Noun, IReadOnlyList<string> Verbs, string? Verb, Vector2 At, bool MenuOpen, int MenuIndex,
    Vector2 MenuAt, string? Speaker, string? Caption, IReadOnlyList<string> Inventory, string? Held, bool InventoryOpen, string Place,
    GameConsole? Console = null, string? Score = null, IReadOnlyList<string>? Items = null, IReadOnlyList<(string Noun, Vector2 At)>? Hotspots = null,
    Func<string, ItemIcon>? Icons = null, Func<string, bool, ItemIcon>? VerbIcons = null, Game.Mechanisms.GpsReading? Gps = null,
    Func<string, ItemIcon>? Pictures = null, bool RadioWorn = false, IReadOnlyList<Game.RadioTopic>? Radio = null, bool RadioOpen = false,
    int RadioIndex = 0, Game.Mechanisms.MechanismButton? Prompt = null, bool Crosshair = false);

/// <summary>The game's interface, laid out fresh every frame.</summary>
public sealed class GameHud
{
    private static readonly Vector4 Panel = new(0.06f, 0.07f, 0.09f, 0.82f);
    private static readonly Vector4 PanelLit = new(0.16f, 0.18f, 0.22f, 0.92f);
    private static readonly Vector4 Ink = new(0.88f, 0.87f, 0.83f, 1f);
    private static readonly Vector4 Dim = new(0.55f, 0.55f, 0.52f, 1f);
    private static readonly Vector4 Accent = new(0.95f, 0.76f, 0.35f, 1f);
    private static readonly Vector4 Rule = new(0.30f, 0.32f, 0.36f, 0.8f);

    /// <summary>What the crosshair is outlined in, so it survives a light wall.</summary>
    private static readonly Vector4 Shadow = new(0.02f, 0.02f, 0.03f, 0.65f);

    /// <summary>The console's own ground, darker and more opaque than the rest.</summary>
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

    /// <summary>What the game calls the player's things, in the player's own language.</summary>
    public Game.GameStrings Names { get; set; } = Game.GameStrings.None;

    /// <summary>How much bigger everything is than the layout was written against.</summary>
    public float Scale => Math.Max(1f, Overlay.LineHeight / 19f);

    /// <summary>Draws the interface with a different font.</summary>
    /// <param name="atlas">The new atlas.</param>
    public void Retarget(OverlayAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);

        Overlay = new Overlay(atlas);
        _rows.Clear();
        _slots.Clear();
    }

    /// <summary>How much of the foot of the screen the interface takes.</summary>
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
        Crosshair(state, width, height);
        // The bar of what the player is carrying used to live along the foot of the screen.
        _strip = default;

        // Before the captions, which are laid out from the foot of the screen upwards and have to start above it rather than under it.
        Prompt(state, width, height);
        Captions(state, width, height);

        // Last, so it is over everything: it is attached to the pointer and the pointer is in front of the game by definition.
        if (state.MenuOpen && state.Verbs.Count > 0)
        {
            Menu(state, width, height);
        }
        else if (!state.RadioOpen)
        {
            // And not while the radio's list is up, for the reason the verb menu is not drawn under one either: the label follows the pointer, the.
            Pointing(state, width, height);
        }

        // The radio's own list, over the room and under the console.
        Radio(state, width, height);

        // Later still.
        if (state.Console is { Open: true } console)
        {
            Terminal(console, width, height);
        }
    }

    /// <summary>The handheld GPS, when Grace has switched it on.</summary>
    /// <param name="state">What the game is doing.</param>
    /// <param name="height">Window height, which is what decides how big it is drawn.</param>
    private void Gps(HudState state, int height)
    {
        if (state.Gps is not { } reading || state.Pictures?.Invoke(reading.Map) is not { Drawn: true } device)
        {
            return;
        }

        // A third of the window's height, which is about what the original's takes on its 480 lines.
        float room = MathF.Max(1f, device.Width - reading.Reading.X - 8f);
        float scale = MathF.Max( height / 3.2f / device.Height, Math.Max( Overlay.Measure(reading.Latitude),
                Overlay.Measure(reading.Longitude)) / room);

        // Under the top bar rather than over it: the bar says where the player is and what they have scored, and the device is up for as long as.
        float margin = 8 * Scale;
        float top = Overlay.LineHeight + (10f * Scale) + margin;

        Overlay.Picture( device.Picture, margin, top, device.Width * scale, device.Height * scale, Vector4.One);

        // Where Grace is, as a cross the width of the screen and a box around the middle of it — the original draws the same three pieces out of.
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

    /// <summary>The developer console.</summary>
    private void Terminal(GameConsole console, int width, int height)
    {
        float unit = Scale;
        float row = Overlay.LineHeight;
        float margin = 10f * unit;

        // Enough for the scrollback, an input line and a full completion list, or half the screen — whichever is less.
        float panel = Math.Min(height * 0.5f, (row * 14) + (24f * unit));
        float input = panel - row - (10f * unit);

        Overlay.Rect(0, 0, width, panel, Console);
        Overlay.Rect(0, panel - 1, width, 1, Accent);

        // The scrollback, newest at the bottom against the input line, which is where the eye already is.
        int fits = Math.Max(0, (int)((input - margin) / row));
        int from = Math.Max(0, console.Lines.Count - fits);

        for (int i = from; i < console.Lines.Count; i++)
        {
            ConsoleLine line = console.Lines[i];

            Overlay.Text( line.Kind == ConsoleLineKind.Echo ? "> " + line.Text : line.Text, margin + (4 * unit), margin + (row * (i - from)),
                line.Kind switch
                {
                    ConsoleLineKind.Complaint => Complaint, ConsoleLineKind.Result => Answer, ConsoleLineKind.Echo => Ink, _ => Dim,
                });
        }

        Overlay.Rect(0, input - (2 * unit), width, 1, Rule);

        float caret = Overlay.Text("> ", margin + (4 * unit), input + (4 * unit), Accent);
        caret = Overlay.Text(console.Typed, caret, input + (4 * unit), Ink);

        // A block rather than a bar, and not blinking.
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
                Overlay.Rect( margin + (3 * unit), y - (2 * unit), listWidth - (3 * unit), row, PanelLit);
            }

            Overlay.Text( console.Completions[i].Signature, margin + (12 * unit), y, chosen ? Accent : Dim);
        }
    }

    /// <summary>Which verb is at a point, if the menu is open.</summary>
    /// <returns>The verb, or null.</returns>
    /// <param name="point">Where the player clicked, in pixels.</param>
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
    /// <returns>The row's index, or -1 when the point is not on one.</returns>
    /// <param name="point">Where the pointer is, in pixels.</param>
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
    /// <returns>Its verb, its item, or .</returns>
    /// <param name="index">Which row.</param>
    public string? RowNamed(int index) => index >= 0 && index < _rows.Count ? _rows[index].Verb : null;

    /// <summary>How many rows the menu is showing.</summary>
    public int RowCount => _rows.Count;

    /// <summary>The middle of a menu row, in pixels.</summary>
    /// <returns>Its centre, or the origin when there is no such row.</returns>
    /// <param name="index">Which row.</param>
    public Vector2 RowMiddle(int index) => index >= 0 && index < _rows.Count ? Middle(_rows[index].Bounds) : Vector2.Zero;

    /// <summary>The middle of an inventory slot, in pixels.</summary>
    /// <returns>Its centre, or the origin when the strip is not showing it.</returns>
    /// <param name="item">The item it holds.</param>
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

    private static Vector2 Middle(Vector4 bounds) => new(bounds.X + (bounds.Z / 2f), bounds.Y + (bounds.W / 2f));

    /// <summary>Whether a point is on the interface rather than on the room behind it.</summary>
    /// <returns>True when the interface is what was clicked.</returns>
    /// <param name="point">Where the player clicked, in pixels.</param>
    public bool OverInterface(Vector2 point) => Inside(point, _strip) || ButtonAt(point) is { Length: > 0 } || TopicAt(point) >= 0;

    /// <summary>Which inventory item is at a point.</summary>
    /// <returns>The item, or null.</returns>
    /// <param name="point">Where the player clicked, in pixels.</param>
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

    private static bool Inside(Vector2 point, Vector4 bounds) => point.X >= bounds.X && point.X <= bounds.X + bounds.Z &&
        point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.W;

    /// <summary>The corner that says where you are, and what you have scored.</summary>
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
        right = Button( state, Text.Say("hud.journal", "Journal"), right, height, unit, "open:journal");

        Button( state, Text.Say("hud.pockets", "Pockets"), right, height, unit, "open:inventory");
    }

    /// <summary>One word in the top bar that answers to a click.</summary>
    /// <returns>Where the next one to its left should end.</returns>
    private float Button( HudState state, string label, float right, float height, float unit, string id)
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

    /// <summary>The one thing the room is asking the player to do, across the foot of the picture.</summary>
    /// <param name="state">What the game is doing.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
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
        float w = Math.Min( width - (48f * unit), Math.Max(220f * unit, Overlay.Measure(asked.Verb) + (72f * unit)));

        float x = (width - w) / 2f;
        float y = height - InventoryHeight - h - (24f * unit);

        bool under = Inside(state.At, new Vector4(x, y, w, h));

        Overlay.Rect(x, y, w, h, asked.Ready && under ? PanelLit : Panel);

        // A rule along the top edge rather than a border all the way round: the same accent the caption panel wears, so the two read as one.
        Overlay.Rect(x, y, w, 3 * unit, asked.Ready ? Accent : Rule);

        Overlay.Text( asked.Verb, x + ((w - Overlay.Measure(asked.Verb)) / 2f), y + (11f * unit), asked.Ready ? Accent : Dim);

        _buttons.Add((PromptButton, new Vector4(x, y, w, h)));

        // What the captions have to keep clear of, the gap under it included.
        _reserved = h + (24f * unit);
    }

    /// <summary>How big the headset is drawn, in units of a line.</summary>
    private const float HeadsetSide = 44f;

    /// <summary>Where the headset sits, whether or not it is drawn.</summary>
    /// <returns>Its square.</returns>
    /// <param name="unit">The scale everything here is measured in.</param>
    private Vector4 HeadsetBounds(float unit)
    {
        float bar = Overlay.LineHeight + (10f * unit);
        float side = HeadsetSide * unit;

        return new Vector4(12 * unit, bar + (10 * unit), side, side);
    }

    /// <summary>The headset Gabriel wears in the temple, under the top bar at the left.</summary>
    /// <param name="state">What the game is doing.</param>
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

        // A ground behind it, because it stands over the room rather than over the bar, and the room it stands over is dark stone under a picture of.
        float pad = 3 * unit;

        Overlay.Rect( bounds.X - pad, bounds.Y - pad, bounds.Z + (pad * 2), bounds.W + (pad * 2), under && ready ? PanelLit : Panel);

        // Named with the extension, because that is what reads a file out of the archives.
        string art = !ready ? "RC_RADIO_DIS.BMP" : state.RadioOpen ? "RC_RADIO_DWN.BMP" : under ? "RC_RADIO_HOV.BMP" : "RC_RADIO_STD.BMP";

        if (state.Pictures?.Invoke(art) is { Drawn: true } picture)
        {
            Vector4 at = picture.Fit(bounds.X, bounds.Y, bounds.Z);

            Overlay.Picture(picture.Picture, at.X, at.Y, at.Z, at.W, Vector4.One);
        }
        else
        {
            // The art is in every copy of the game, so this is not a fallback anybody should see.
            Overlay.Text( "Grace", bounds.X + (4 * unit), bounds.Y + ((bounds.W - Overlay.LineHeight) / 2), ready ? Ink : Dim);
        }

        // Registered whether or not there is anything to say.
        _buttons.Add((RadioButton, bounds));
    }

    /// <summary>The things Gabriel can raise with Grace, under the headset that opens them.</summary>
    /// <param name="state">What the game is doing.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
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
            w = Math.Max(w, Overlay.Measure(Thing(topic.Label)));
        }

        w += padding * 2;

        float row = Overlay.LineHeight + (8f * unit);
        float title = Overlay.LineHeight + (8f * unit);
        float h = title + (row * topics.Count) + padding;

        // The same answer the verb menu gives a character with thirty topics: shorter rows rather than rows under the bottom of the screen, where.
        if (h > height - bar && topics.Count > 0)
        {
            row = Math.Max( Overlay.LineHeight, (height - bar - title - padding) / topics.Count);

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

            Overlay.Text( Thing(topics[i].Label), x + padding, top + ((row - Overlay.LineHeight) / 2), chosen ? Accent : Ink);

            _topics.Add((topics[i].Noun, bounds));
        }
    }

    /// <summary>Which radio topic is at a point.</summary>
    /// <returns>Its index, or -1.</returns>
    /// <param name="point">Where the pointer is.</param>
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
    /// <returns>What it opens, or null.</returns>
    /// <param name="point">Where the pointer is.</param>
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

    /// <summary>Every hotspot in the room at once, while the key that asks is held.</summary>
    /// <param name="state">What the game is doing, including where each noun is.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    private void Crosshair(HudState state, int width, int height)
    {
        if (!state.Crosshair)
        {
            return;
        }

        float unit = MathF.Max(1f, MathF.Round(Scale));

        // Grown and lit when there is something under it, which is the only feedback a player gets that turning another degree would put them on it.
        bool over = state.Noun is { Length: > 0 };
        float dot = (over ? 4f : 3f) * unit;
        float edge = unit;

        float x = MathF.Round((width - dot) / 2f);
        float y = MathF.Round((height - dot) / 2f);

        // A dark square under a light one, so the mark survives a white wall and a night sky.
        Overlay.Rect(x - edge, y - edge, dot + (2 * edge), dot + (2 * edge), Shadow);
        Overlay.Rect(x, y, dot, dot, over ? Accent : Ink);
    }

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
            string label = Thing(noun);
            float wide = Overlay.Measure(label) + (12 * unit);

            float x = Math.Clamp(at.X - (wide / 2), 0, Math.Max(0, width - wide));
            float y = Math.Clamp(at.Y - (row / 2), bar, Math.Max(bar, height - row));

            // Down until it clears everything already placed.
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
    private static bool Overlaps(Vector4 placed, float x, float y, float wide, float tall) => x < placed.X + placed.Z && x + wide > placed.X &&
        y < placed.Y + placed.W && y + tall > placed.Y;

    /// <summary>The label that follows the pointer.</summary>
    private void Pointing(HudState state, int width, int height)
    {
        if (state.Noun is not { Length: > 0 } noun)
        {
            return;
        }

        string subject = Thing(noun);
        string? action = state.Verb is { Length: > 0 } verb ? Verb(verb) : null;

        // Two runs rather than one string with a separator in it.
        float unit = Scale;
        float w = Overlay.Measure(subject) + (16f * unit);

        if (action is not null)
        {
            w += Overlay.Measure(action) + Overlay.Measure("  ");
        }

        float h = Overlay.LineHeight + (10f * unit);

        // Kept on screen: a label that runs off the right edge is worse than one that stops following the pointer for the last few pixels.
        float x = Math.Clamp(state.At.X + (18 * unit), 0, Math.Max(0, width - w));
        // Below the top bar, never over it.
        float bar = Overlay.LineHeight + (14f * unit);

        float y = Math.Clamp(state.At.Y + (18 * unit), bar, Math.Max(bar, height - h));

        Overlay.Rect(x, y, w, h, Panel);
        Overlay.Rect(x, y, 2 * unit, h, action is not null ? Accent : Rule);

        float pen = Overlay.Text( subject, x + (10 * unit), y + (5 * unit), action is not null ? Ink : Dim);

        if (action is not null)
        {
            Overlay.Text(action, pen + Overlay.Measure("  "), y + (5 * unit), Accent);
        }
    }

    /// <summary>The full list of verbs, where the pointer was when they were asked for.</summary>
    private void Menu(HudState state, int width, int height)
    {
        float unit = Scale;
        float padding = 8f * unit;

        // The heading counts.
        string heading = Thing(state.Noun ?? string.Empty);
        float w = Overlay.Measure(heading);

        // The verbs, and then one row standing for everything in the bag that this noun answers to.
        IReadOnlyList<string> items = state.Items ?? [];
        List<string> rows = [.. state.Verbs];

        if (items.Count > 0)
        {
            rows.Add(UseRow);
        }

        bool opening = items.Count > 0 && state.MenuIndex >= rows.Count - 1;

        // The original's verb icons are 32 pixels square, so a row with room for one at the size it was painted is the one arrangement that does not.
        float badge = state.VerbIcons is null ? 0 : 32f * unit;
        float row = Math.Max(Overlay.LineHeight + (8f * unit), badge + (6f * unit));
        float title = Overlay.LineHeight + (8f * unit);
        float h = title + (row * rows.Count) + padding;

        // Somebody with thirty topics to raise gets smaller icons rather than a list whose last rows are under the bottom of the screen, where they.
        if (h > height && rows.Count > 0)
        {
            row = Math.Max( Overlay.LineHeight + (8f * unit), (height - title - padding) / rows.Count);
            badge = Math.Min(badge, Math.Max(0, row - (6f * unit)));
            h = title + (row * rows.Count) + padding;
        }

        // How far the words are pushed in to clear the picture.
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

            // Lit art for the row the player has picked out.
            if (badge > 0 && state.VerbIcons?.Invoke(rows[i], chosen) is { Drawn: true } picture)
            {
                Vector4 at = picture.Fit(x + padding, top + ((row - badge) / 2), badge);

                Overlay.Picture(picture.Picture, at.X, at.Y, at.Z, at.W, Vector4.One);
            }

            Overlay.Text( Label(rows[i]), x + padding + indent, top + ((row - Overlay.LineHeight) / 2), chosen ? Accent : Ink);
            _rows.Add((rows[i], bounds));
        }

        if (!opening)
        {
            return;
        }

        // The second column, beside the first rather than over it, so the row that opened it stays visible and the player can see what they are.
        float art = state.Icons is null ? 0 : row - (6 * unit);
        float itemWidth = padding * 2;

        foreach (string item in items)
        {
            itemWidth = Math.Max(itemWidth, Overlay.Measure(Owned(item)) + (padding * 2) + art);
        }

        float itemHeight = (row * items.Count) + padding;
        float itemX = Math.Clamp(x + w + (2 * unit), 0, Math.Max(0, width - itemWidth));
        float itemY = Math.Clamp( y + title + (row * (rows.Count - 1)), 0, Math.Max(0, height - itemHeight));

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

            Overlay.Text( Owned(items[i]), itemX + padding + art, top + ((row - Overlay.LineHeight) / 2), chosen ? Accent : Ink);

            _rows.Add((items[i], bounds));
        }
    }

    /// <summary>The row that stands for "use something on this".</summary>
    public const string UseRow = "\u0001use";

    /// <summary>What a menu row reads as.</summary>
    private string Label(string verb) => verb == UseRow ? Text.Say("verb.use", "Use...") : Verb(verb);

    /// <summary>What one of the player's things reads as.</summary>
    public UiText Text { get; set; } = UiText.English;

    private string Owned(string item) => Thing(item);

    /// <summary>What a thing in the room is called.</summary>
    /// <returns>The game's own name for it, or the tidied identifier.</returns>
    /// <param name="noun">Its noun, as the action files spell it: MASKING_TAPE.</param>
    private string Thing(string noun) => Names.Item(noun) ?? Text.Say("noun." + noun.ToUpperInvariant(), Pretty(noun));

    /// <summary>What a verb is called under the cursor and in the menu.</summary>
    /// <returns>The word, in the player's own language where there is one.</returns>
    /// <param name="verb">Its noun, as the action files spell it: PICK_UP.</param>
    private string Verb(string verb) => verb.Length == 0 ? verb : Text.Say("verb." + verb.ToUpperInvariant(), Pretty(verb));

    /// <summary>The strip along the bottom, which is no longer drawn.</summary>
    private void Inventory(HudState state, int width, int height)
    {
        float unit = Scale;
        float h = InventoryHeight;
        float y = height - h;

        Overlay.Rect(0, y, width, h, Panel);
        Overlay.Rect(0, y, width, 1, Rule);

        _strip = new Vector4(0, y, width, h);

        // No count and no "carrying nothing": the row of items says both, and an empty row says the empty case better than a sentence about it does.
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

        // GK3 writes UNKNOWN for a line with nobody on screen saying it — Gabriel's own narration, mostly.
        if (state.Speaker is { Length: > 0 } speaker && !speaker.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            Overlay.Text(Pretty(speaker), margin + (14 * unit), y - row - (2 * unit), Accent);
        }
    }

    /// <summary>Breaks a line of dialogue to fit the width it is given.</summary>
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

    /// <summary>Turns an internal name into something a player can read.</summary>
    private static string Pretty(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        // Small talk, which the data spells Z_CHAT so that it sorts to the end of a list.
        if (name.Equals("Z_CHAT", StringComparison.OrdinalIgnoreCase))
        {
            return "Chat";
        }

        string text = name.Replace('_', ' ').Trim();

        // Something already written for a person to read is left exactly as it is.
        if (text.Any(char.IsLower))
        {
            // Its own casing is kept, because recasing "Rennes-le-Chateau: Outside Church" gives it a lower-case C in the middle of a place name.
            return char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
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
