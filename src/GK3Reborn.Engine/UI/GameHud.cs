using System.Globalization;
using System.Numerics;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI;

/// <summary>What the interface is being asked to show this frame.</summary>
/// <param name="Noun">What the pointer is over, or null.</param>
/// <param name="Verbs">What that answers to, most likely first.</param>
/// <param name="Verb">The verb a plain click would perform.</param>
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
    string Place);

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

    private readonly List<(string Verb, Vector4 Bounds)> _rows = [];
    private readonly List<(string Item, Vector4 Bounds)> _slots = [];

    /// <summary>Creates the interface over an overlay.</summary>
    /// <param name="overlay">Where it draws.</param>
    public GameHud(Overlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        Overlay = overlay;
    }

    /// <summary>The display list it fills in.</summary>
    public Overlay Overlay { get; }

    /// <summary>How tall the inventory strip is.</summary>
    public float InventoryHeight => Overlay.LineHeight + 14f;

    /// <summary>Lays the interface out.</summary>
    /// <param name="state">What to show.</param>
    /// <param name="width">Width of the surface, in pixels.</param>
    /// <param name="height">Height of the surface, in pixels.</param>
    public void Build(HudState state, int width, int height)
    {
        Overlay.Begin(width, height);
        _rows.Clear();
        _slots.Clear();

        Where(state, width);
        Inventory(state, width, height);
        Captions(state, width, height);

        // Last, so it is over everything: it is attached to the pointer and the pointer is
        // in front of the game by definition.
        if (state.MenuOpen && state.Verbs.Count > 0)
        {
            Menu(state, width, height);
        }
        else
        {
            Pointing(state, width, height);
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

    /// <summary>The corner that says where you are.</summary>
    private void Where(HudState state, int width)
    {
        float height = Overlay.LineHeight + 10f;

        Overlay.Rect(0, 0, width, height, Panel);
        Overlay.Rect(0, height - 1, width, 1, Rule);
        Overlay.Text(state.Place, 12, 5, Dim);

        string hint = "right-click for everything it answers to";
        Overlay.Text(hint, width - Overlay.Measure(hint) - 12, 5, Dim);
    }

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
        float w = Overlay.Measure(subject) + 16f;

        if (action is not null)
        {
            w += Overlay.Measure(action) + Overlay.Measure("  ");
        }

        float h = Overlay.LineHeight + 10f;

        // Kept on screen: a label that runs off the right edge is worse than one that stops
        // following the pointer for the last few pixels.
        float x = Math.Clamp(state.At.X + 18, 0, Math.Max(0, width - w));
        float y = Math.Clamp(state.At.Y + 18, 0, Math.Max(0, height - h));

        Overlay.Rect(x, y, w, h, Panel);
        Overlay.Rect(x, y, 2, h, action is not null ? Accent : Rule);

        float pen = Overlay.Text(subject, x + 10, y + 5, action is not null ? Ink : Dim);

        if (action is not null)
        {
            Overlay.Text(action, pen + Overlay.Measure("  "), y + 5, Accent);
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
        const float Padding = 8f;
        float row = Overlay.LineHeight + 8f;
        float w = 0;

        foreach (string verb in state.Verbs)
        {
            w = Math.Max(w, Overlay.Measure(Pretty(verb)));
        }

        w += Padding * 2;

        float title = Overlay.LineHeight + 8f;
        float h = title + (row * state.Verbs.Count) + Padding;
        float x = Math.Clamp(state.MenuAt.X, 0, Math.Max(0, width - w));
        float y = Math.Clamp(state.MenuAt.Y, 0, Math.Max(0, height - h));

        Overlay.Rect(x, y, w, h, PanelLit);
        Overlay.Text(Pretty(state.Noun ?? string.Empty), x + Padding, y + 4, Accent);
        Overlay.Rect(x, y + title, w, 1, Rule);

        for (int i = 0; i < state.Verbs.Count; i++)
        {
            float top = y + title + (row * i);
            var bounds = new Vector4(x, top, w, row);

            bool chosen = i == state.MenuIndex;

            if (chosen)
            {
                Overlay.Rect(x, top, w, row, new Vector4(0.28f, 0.31f, 0.37f, 1f));
                Overlay.Rect(x, top, 2, row, Accent);
            }

            Overlay.Text(Pretty(state.Verbs[i]), x + Padding, top + 4, chosen ? Accent : Ink);
            _rows.Add((state.Verbs[i], bounds));
        }
    }

    /// <summary>
    /// The strip along the bottom.
    /// </summary>
    /// <remarks>
    /// Always there, never a screen of its own. The original put the inventory behind a
    /// mode change, which meant checking what you were carrying cost you the view of the
    /// room you were carrying it in.
    /// </remarks>
    private void Inventory(HudState state, int width, int height)
    {
        float h = InventoryHeight;
        float y = height - h;

        Overlay.Rect(0, y, width, h, Panel);
        Overlay.Rect(0, y, width, 1, Rule);

        string label = state.Inventory.Count == 0
            ? "carrying nothing"
            : string.Create(CultureInfo.InvariantCulture, $"carrying {state.Inventory.Count}");

        Overlay.Text(label, 12, y + 7, Dim);

        float x = Overlay.Measure(label) + 28;

        foreach (string item in state.Inventory)
        {
            string name = Pretty(item);
            float w = Overlay.Measure(name) + 16;

            if (x + w > width - 12)
            {
                break;
            }

            bool held = string.Equals(item, state.Held, StringComparison.OrdinalIgnoreCase);
            var bounds = new Vector4(x, y + 4, w, h - 8);

            Overlay.Rect(x, y + 4, w, h - 8, held ? PanelLit : Panel);
            Overlay.Rect(x, y + 4, w, 1, held ? Accent : Rule);
            Overlay.Text(name, x + 8, y + 7, held ? Accent : Ink);

            _slots.Add((item, bounds));
            x += w + 6;
        }
    }

    /// <summary>What is being said, along the bottom above the inventory.</summary>
    private void Captions(HudState state, int width, int height)
    {
        if (state.Caption is not { Length: > 0 } caption)
        {
            return;
        }

        float row = Overlay.LineHeight;
        float margin = 48f;
        float usable = Math.Max(64f, width - (margin * 2) - 24f);

        List<string> lines = Wrap(caption, usable);
        float h = (row * lines.Count) + 20f;
        float y = height - InventoryHeight - h - 12f;

        Overlay.Rect(margin, y, width - (margin * 2), h, Panel);
        Overlay.Rect(margin, y, 3, h, Accent);

        for (int i = 0; i < lines.Count; i++)
        {
            Overlay.Text(lines[i], margin + 14, y + 10 + (row * i), Ink);
        }

        // GK3 writes UNKNOWN for a line with nobody on screen saying it — Gabriel's own
        // narration, mostly. Writing "Unknown" over it is worse than writing nothing.
        if (state.Speaker is { Length: > 0 } speaker &&
            !speaker.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            Overlay.Text(Pretty(speaker), margin + 14, y - row - 2, Accent);
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

        string text = name.Replace('_', ' ').Trim();

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
