// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Foundation;
using GK3Reborn.Rendering;

namespace GK3Reborn.UI;

/// <summary>One of the title screen's layers, once it is on the device.</summary>
/// <param name="Picture">Its number in the interface's picture list.</param>
/// <param name="Width">How wide the source is, in pixels.</param>
/// <param name="Height">How tall.</param>
/// <param name="Content">
/// The part of it that is not transparent, in texture coordinates: left, top, width and
/// height. Measured rather than assumed, because the lettering is painted in the middle of
/// a tall transparent sheet and laying that sheet out by its own size puts the words a long
/// way from where they were asked for.
/// </param>
public readonly record struct TitleLayer(int Picture, int Width, int Height, Vector4 Content)
{
    /// <summary>How wide the painted part is against how tall it is.</summary>
    public float Aspect =>
        Content.W > 0 && Height > 0
            ? Content.Z * Width / (Content.W * Height)
            : 1f;

    /// <summary>How wide the whole sheet is against how tall it is.</summary>
    public float SheetAspect => Height > 0 ? Width / (float)Height : 1f;
}

/// <summary>
/// The title screen the port opens with: a lit statue in front of a wall that moves.
/// </summary>
public sealed class TitleScene : IDisposable
{
    /// <summary>How far down the window the wall starts.</summary>
    private const float BandTop = 0.07f;

    /// <summary>Where it ends. What is under it is black, and the rows are drawn on it.</summary>
    private const float BandBottom = 0.83f;

    /// <summary>How long the wall takes to travel its own width, in seconds.</summary>
    private const float WallSeconds = 260f;

    /// <summary>
    /// How long the light out of frame takes to swing across the statue and back.
    /// </summary>
    private const float LightSeconds = 600f;

    /// <summary>How many upright slices one width of the wall is drawn in.</summary>
    private const int WallSlices = 96;

    /// <summary>How many upright slices the light is drawn in.</summary>
    private const int LightSlices = 16;

    /// <summary>How many level slices the cast shadow is sheared in.</summary>
    private const int ShadowSlices = 256;

    /// <summary>The least the wall fades to at the sides of the window.</summary>
    private const float EdgeLeast = 0.42f;

    /// <summary>How much of the window each side of that fade takes.</summary>
    private const float EdgeWidth = 0.16f;

    /// <summary>
    /// How far the wall wanders up and down, as a part of the band's height.
    /// </summary>
    private const float WaveDepth = 0.013f;

    /// <summary>
    /// How much of the wall the drifting thin patches take away at their deepest.
    /// </summary>
    private const float CloudDepth = 0.34f;

    /// <summary>How far a sigil rises while it is up, as a part of the window's height.</summary>
    private const float SigilRise = 0.07f;

    /// <summary>How opaque a sigil ever gets. Never one: it is a stain, not a decal.</summary>
    private const float SigilOpacity = 0.55f;

    /// <summary>How long a sigil is on screen, in seconds, its fades included.</summary>
    private const float SigilSeconds = 14f;

    /// <summary>How long its fade in and its fade out each take.</summary>
    private const float SigilFade = 4f;

    /// <summary>The shortest wait between one sigil and the next, in seconds.</summary>
    private const float SigilWaitShortest = 15f;

    /// <summary>The longest.</summary>
    private const float SigilWaitLongest = 30f;

    /// <summary>How far a sigil turns over the whole of its appearance, in radians.</summary>
    private const float SigilTurn = 0.18f;

    /// <summary>What a sigil is burned into the wall in.</summary>
    private static readonly Vector3 SigilInk = new(0.30f, 0.18f, 0.16f);

    /// <summary>
    /// What the sigils' order is drawn from when nobody has said otherwise.
    /// </summary>
    private const int Seed = 0x6B3352;

    /// <summary>
    /// Which sigil comes next, and how long the wait before it is.
    /// </summary>
    private readonly DeterministicRandom _random;
    private readonly TitleLayer _statue;
    private readonly TitleLayer _wall;
    private readonly TitleLayer _name;
    private readonly TitleLayer[] _sigils;

    /// <summary>The wall as painted, kept for the party to hang behind its floor.</summary>
    private readonly DecodedImage? _wallPicture;

    /// <summary>The word being spelled on the title.</summary>
    private readonly DanceCode _code = new();

    /// <summary>When each lit letter was lit, by its index in the sheet's letters.</summary>
    private readonly Dictionary<int, float> _litAt = [];

    /// <summary>The party, once the word is spelled.</summary>
    private DiscoParty? _party;

    /// <summary>Whether the word was spelled and the party could not be built.</summary>
    private bool _partyFailed;

    /// <summary>How long the screen has been up, in seconds.</summary>
    private float _elapsed;

    /// <summary>Which sigil is up, or -1 when none is.</summary>
    private int _sigil = -1;

    /// <summary>Which one was up last, so that the next one is a different one.</summary>
    private int _shown = -1;

    /// <summary>How long the one that is up has been up, or how long the wait has run.</summary>
    private float _sigilAge;

    /// <summary>How long this wait is.</summary>
    private float _sigilWait;

    private TitleScene(
        TitleLayer statue,
        TitleLayer wall,
        TitleLayer name,
        TitleLayer[] sigils,
        int seed,
        DecodedImage? wallPicture)
    {
        _statue = statue;
        _wall = wall;
        _name = name;
        _sigils = sigils;
        _wallPicture = wallPicture;
        _random = new DeterministicRandom((ulong)seed);
        _sigilWait = Wait();
    }

    /// <summary>
    /// Builds the party when the word is spelled, or null when nothing can. Set by whoever
    /// has the archives and the device; left null, spelling the word lights the letters
    /// and nothing more.
    /// </summary>
    public Func<DecodedImage?, DiscoParty?>? PartyMaker { get; set; }

    /// <summary>The party, or null while the word is unspelled.</summary>
    public DiscoParty? Party => _party;

    /// <summary>Called once, the moment the word is spelled and the party is built.</summary>
    public Action? PartyStarted { get; set; }

    /// <summary>Which letters of the title are lit, as indices into <see cref="TitleLetters.All"/>.</summary>
    public IReadOnlyList<int> Lit => _code.Lit;

    /// <summary>Whether the word has been spelled.</summary>
    public bool Spelled => _code.Complete;

    /// <summary>The layers, in the order the set lists them.</summary>
    public IReadOnlyList<TitleLayer> Layers => [_statue, _wall, _name, .. _sigils];

    /// <summary>Which sigil is showing, or -1 when none is.</summary>
    public int Showing => _sigil;

    /// <summary>How long the screen has been up, in seconds.</summary>
    public float Elapsed => _elapsed;

    /// <summary>Puts the art on the device and builds the screen around it.</summary>
    /// <param name="art">The layers, which must be <see cref="MenuArt.Complete"/>.</param>
    /// <param name="upload">
    /// Puts one picture on the device under a name and gives back its number, or nought
    /// when the device would not take it.
    /// </param>
    /// <param name="seed">Which sigil order to run, for a test that wants a stated one.</param>
    /// <returns>
    /// The screen, or null when the set is incomplete or the device refused a layer. Null
    /// means the game opens on the 1999 title art, which is what a game with no packs does.
    /// </returns>
    public static TitleScene? Build(
        MenuArt art, Func<string, DecodedImage, int> upload, int seed = Seed)
    {
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(upload);

        if (!art.Complete)
        {
            return null;
        }

        var placed = new Dictionary<string, TitleLayer>(StringComparer.OrdinalIgnoreCase);

        foreach (string layer in MenuArt.Layers)
        {
            if (art[layer] is not { } picture)
            {
                return null;
            }

            int number = upload(Named(layer), picture);

            // All six or none. A device that took five of them and refused the sixth has a
            // full picture list, and the answer to that is the screen the game already had
            // rather than a wall with no statue in front of it.
            if (number <= 0)
            {
                return null;
            }

            placed[layer] = new TitleLayer(
                number, picture.Width, picture.Height, Painted(picture));
        }

        return new TitleScene(
            placed[MenuArt.Statue],
            placed[MenuArt.Wall],
            placed[MenuArt.Name],
            [.. MenuArt.Sigils.Select(sigil => placed[sigil])],
            seed,
            art[MenuArt.Wall]);
    }

    /// <summary>Takes a click that landed on no row of the menu.</summary>
    /// <param name="point">Where, in pixels.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>Whether the click landed on a letter of the title.</returns>
    public bool Click(Vector2 point, float width, float height)
    {
        if (width <= 0f || height <= 0f)
        {
            return false;
        }

        int letter = TitleLetters.At(point, Sheet(Name(width, height)));

        if (letter < 0)
        {
            return false;
        }

        if (_party is not null)
        {
            return true;
        }

        _code.Click(letter);

        // The letters lit are exactly the code's: a wrong letter took the others out.
        foreach (int lit in _litAt.Keys.Where(k => !_code.Lit.Contains(k)).ToList())
        {
            _litAt.Remove(lit);
        }

        foreach (int lit in _code.Lit)
        {
            _litAt.TryAdd(lit, _elapsed);
        }

        Throw();

        return true;
    }

    /// <summary>Spells the word outright, for a run that photographs the party.</summary>
    public void Spell()
    {
        if (_party is not null)
        {
            return;
        }

        foreach (char wanted in TitleLetters.Word)
        {
            for (int i = 0; i < TitleLetters.All.Count; i++)
            {
                if (TitleLetters.All[i].Character == wanted && _code.Click(i))
                {
                    _litAt.TryAdd(i, _elapsed);
                    break;
                }
            }
        }

        Throw();
    }

    /// <summary>Starts the party if the word has just been spelled.</summary>
    private void Throw()
    {
        if (!_code.Complete || _party is not null || _partyFailed)
        {
            return;
        }

        _party = PartyMaker?.Invoke(_wallPicture);
        _partyFailed = _party is null;

        if (_party is not null)
        {
            PartyStarted?.Invoke();
        }
    }

    /// <summary>Where one letter of the title is on the screen.</summary>
    /// <param name="letter">Its index in <see cref="TitleLetters.All"/>.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>Left, top, width and height, in pixels.</returns>
    public Vector4 LetterBox(int letter, float width, float height) =>
        TitleLetters.All[letter].On(Sheet(Name(width, height)));

    /// <summary>Where the whole lettering sheet is, given where its painted part is.</summary>
    private Vector4 Sheet(Vector4 painted)
    {
        float wide = painted.Z / MathF.Max(0.001f, _name.Content.Z);
        float tall = painted.W / MathF.Max(0.001f, _name.Content.W);

        return new Vector4(
            painted.X - (_name.Content.X * wide),
            painted.Y - (_name.Content.Y * tall),
            wide,
            tall);
    }

    /// <summary>What a layer is called on the device.</summary>
    /// <param name="layer">Its name in the set.</param>
    /// <returns>A name no other screen's picture can collide with.</returns>
    public static string Named(string layer) => "title:" + layer;

    /// <summary>Moves the screen on.</summary>
    /// <param name="seconds">How long the last frame took.</param>
    public void Advance(float seconds)
    {
        // Clamped, because the frame a film or a scene load ended on can be most of a
        // second, and a sigil that arrived and left between two frames is a flicker.
        float step = Math.Clamp(seconds, 0f, 0.1f);

        _elapsed += step;
        _sigilAge += step;

        _party?.Advance(step);

        if (_sigil >= 0)
        {
            if (_sigilAge >= SigilSeconds)
            {
                _sigil = -1;
                _sigilAge = 0f;
                _sigilWait = Wait();
            }

            return;
        }

        if (_sigilAge < _sigilWait)
        {
            return;
        }

        // Never the same one twice running: three sigils and a fifteen-second gap means a
        // repeat would be the thing the player noticed about them.
        int next = _random.NextInt32(0, _sigils.Length);

        if (_sigils.Length > 1 && next == _shown)
        {
            next = (next + 1 + _random.NextInt32(0, _sigils.Length - 1)) % _sigils.Length;
        }

        _sigil = next;
        _shown = next;
        _sigilAge = 0f;
    }

    /// <summary>Draws the whole screen into a display list.</summary>
    /// <param name="overlay">The list, already begun for this frame.</param>
    public void Draw(Overlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        float width = overlay.Width;
        float height = overlay.Height;

        if (width <= 0f || height <= 0f)
        {
            return;
        }

        float bandTop = MathF.Round(height * BandTop);
        float bandBottom = MathF.Round(height * BandBottom);
        float band = bandBottom - bandTop;

        if (band <= 1f)
        {
            return;
        }

        Vector4 statue = Statue(width, height);
        Vector4 name = Name(width, height);

        if (_party is { } party)
        {
            // The party is drawn by the renderer under this list, so there is no black and
            // no wall: what is here is the statue standing in front of it, the black the
            // rows sit on, and the name.
            overlay.Picture(_statue.Picture, statue.X, statue.Y, statue.Z, statue.W, Vector4.One);
            Strobe(overlay, statue, party);
            Feet(overlay, width, height, bandBottom);

            overlay.Picture(
                _name.Picture, name.X, name.Y, name.Z, name.W, Vector4.One, _name.Content);

            Glow(overlay, name, party);
            Say(overlay, width, height, party);

            return;
        }

        // Black, and the whole window, because everything after it is either screened onto
        // it or standing in front of it. The renderer clears to black as well; saying so
        // here is what makes the screen the display list's own rather than something that
        // depends on what the pass before it left behind.
        overlay.Rect(0f, 0f, width, height, new Vector4(0f, 0f, 0f, 1f));

        overlay.Picture(_statue.Picture, statue.X, statue.Y, statue.Z, statue.W, Vector4.One);

        Wall(overlay, width, bandTop, band);
        Shadow(overlay, statue, bandTop, band);
        Light(overlay, statue);
        Sigil(overlay, width, height, bandTop, band);
        Feet(overlay, width, height, bandBottom);

        overlay.Picture(
            _name.Picture, name.X, name.Y, name.Z, name.W, Vector4.One, _name.Content);

        Glow(overlay, name, null);
    }

    /// <summary>Lights the letters that have been clicked.</summary>
    private void Glow(Overlay overlay, Vector4 name, DiscoParty? party)
    {
        if (_code.Lit.Count == 0)
        {
            return;
        }

        Vector4 sheet = Sheet(name);

        for (int i = 0; i < _code.Lit.Count; i++)
        {
            int index = _code.Lit[i];
            TitleLetter letter = TitleLetters.All[index];
            Vector4 box = letter.On(sheet);

            // In over a third of a second, so a click is answered by a letter lighting
            // rather than by one that is suddenly lit.
            float age = _elapsed - (_litAt.TryGetValue(index, out float lit) ? lit : _elapsed);
            float on = Math.Clamp(age / 0.35f, 0f, 1f);

            float pulse = party is { Started: true }
                ? 0.75f + (0.25f * MathF.Max(0f, MathF.Cos(party.Beat * 2f * MathF.PI)))
                : 0.85f + (0.15f * MathF.Sin((_elapsed * 3f) + i));

            Vector3 colour = Rainbow((_elapsed * 0.25f) + (i * 0.2f));
            var centre = new Vector2(box.X + (box.Z / 2f), box.Y + (box.W / 2f));

            // The letter itself first, laid over in its colour: screened, a colour onto
            // white stays white, so the paint has to be covered rather than lit. The halo
            // round it is screened, which is what a light does to the black beside it.
            overlay.Picture(
                _name.Picture,
                box.X,
                box.Y,
                box.Z,
                box.W,
                new Vector4(colour, on * (0.55f + (0.45f * pulse))),
                letter.Box);

            for (int ring = 1; ring <= 3; ring++)
            {
                float scale = 1f + (ring * 0.28f * pulse);
                float alpha = on * pulse * 0.5f / ring;

                overlay.Picture(
                    _name.Picture,
                    centre.X - (box.Z * scale / 2f),
                    centre.Y - (box.W * scale / 2f),
                    box.Z * scale,
                    box.W * scale,
                    new Vector4(colour, alpha),
                    letter.Box,
                    OverlayBlend.Screen);
            }
        }
    }

    /// <summary>The statue, taking the colour of the lights on the floor beside it.</summary>
    private void Strobe(Overlay overlay, Vector4 statue, DiscoParty party)
    {
        if (!party.Started)
        {
            return;
        }

        float beat = 0.18f + (0.16f * MathF.Max(0f, MathF.Cos(party.Beat * 2f * MathF.PI)));
        Vector3 colour = Rainbow(party.Elapsed * 0.08f);

        overlay.Picture(
            _statue.Picture,
            statue.X,
            statue.Y,
            statue.Z,
            statue.W,
            new Vector4(colour, beat),
            null,
            OverlayBlend.Screen);
    }

    /// <summary>What the bartender says, for a while, in the menu's own letters.</summary>
    private static void Say(Overlay overlay, float width, float height, DiscoParty party)
    {
        const float Stays = 7f;

        if (party.Caption is not { Length: > 0 } line || party.CaptionAge > Stays)
        {
            return;
        }

        float alpha = MathF.Min(
            Math.Clamp(party.CaptionAge / 0.5f, 0f, 1f),
            Math.Clamp((Stays - party.CaptionAge) / 1f, 0f, 1f));

        float wide = overlay.Measure(line);
        float x = (width - wide) / 2f;
        float y = height * 0.80f - overlay.LineHeight;

        overlay.Text(line, x + 2f, y + 2f, new Vector4(0f, 0f, 0f, alpha));
        overlay.Text(line, x, y, new Vector4(1f, 0.95f, 0.8f, alpha));
    }

    /// <summary>A saturated colour from round the wheel.</summary>
    private static Vector3 Rainbow(float turn)
    {
        float h = ((turn % 1f) + 1f) % 1f * 6f;
        float x = 1f - MathF.Abs((h % 2f) - 1f);

        return (int)h switch
        {
            0 => new Vector3(1f, x, 0f),
            1 => new Vector3(x, 1f, 0f),
            2 => new Vector3(0f, 1f, x),
            3 => new Vector3(0f, x, 1f),
            4 => new Vector3(x, 0f, 1f),
            _ => new Vector3(1f, 0f, x),
        };
    }

    /// <summary>Takes the party's music off while a film plays.</summary>
    public void Hush() => _party?.Hush();

    /// <summary>Puts it back.</summary>
    public void Resume() => _party?.Resume();

    /// <inheritdoc/>
    public void Dispose()
    {
        _party?.Dispose();
        _party = null;
    }

    /// <summary>Where the statue stands, in pixels.</summary>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>Left, top, width and height.</returns>
    private Vector4 Statue(float width, float height)
    {
        float tall = height * 0.98f;
        float wide = tall * _statue.SheetAspect;

        return new Vector4(
            MathF.Round(-width * 0.035f), MathF.Round(height - tall), wide, tall);
    }

    /// <summary>Where the game's name is written, in pixels.</summary>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>Left, top, width and height, of the painted part alone.</returns>
    private Vector4 Name(float width, float height)
    {
        // Sized against the window's width and capped by its height, so that a 21:9 monitor
        // gets lettering in proportion to the wall rather than lettering as wide as a third
        // of a very wide screen.
        float aspect = MathF.Max(_name.Aspect, 0.01f);
        float tall = MathF.Min(width * 0.34f / aspect, height * 0.16f);
        float wide = tall * aspect;

        return new Vector4(
            MathF.Round((width * 0.90f) - wide),
            MathF.Round((height * 0.55f) - (tall / 2f)),
            wide,
            tall);
    }

    /// <summary>Draws the wall, scrolling, screened onto the statue and the black.</summary>
    private void Wall(Overlay overlay, float width, float bandTop, float band)
    {
        float scale = MathF.Max(1f, band / MathF.Max(1, _wall.Height));
        float tile = MathF.Max(1f, _wall.Width * scale);

        // Drawn taller than the band and clipped back to it, because every slice hangs a
        // few pixels above or below where the band is and one that hung would otherwise
        // show the black behind it along an edge.
        float overscan = MathF.Max(2f, band * WaveDepth * 2.5f);

        // How much of the picture's height that comes to, and where it starts. The middle
        // of it, so what is cropped off is the same amount top and bottom. Taken as a
        // window into the picture rather than by stretching it: the whole point of not
        // fitting the wall into the band was to keep one texel on one pixel.
        float shown = MathF.Min(1f, (band + (2f * overscan)) / MathF.Max(1f, _wall.Height * scale));
        float top = (1f - shown) / 2f;

        // What that window is on the screen, which is the band plus as much of the
        // overscan as the picture actually had to give.
        float tall = shown * _wall.Height * scale;
        float above = bandTop - ((tall - band) / 2f);

        // One width more than the window needs, so the seam is always off the screen. The
        // picture is painted to meet itself, which is what lets this be a scroll rather
        // than a fade from one copy to another.
        int tiles = (int)MathF.Ceiling(width / tile) + 1;

        // Where the wall has got to, as a part of one of its own widths. It travels left,
        // so the edge coming into the window is the right-hand one. Not rounded to whole
        // pixels: at eight pixels a second, snapping would make it step eight times a
        // second instead of sliding.
        float start = -((_elapsed / WallSeconds) % 1f) * tile;

        overlay.PushClip(new Vector4(0f, bandTop, width, band));

        for (int t = 0; t < tiles; t++)
        {
            float tileLeft = start + (t * tile);

            for (int i = 0; i < WallSlices; i++)
            {
                // Both edges from the tile rather than by adding a width to a left edge,
                // so that one slice's right edge is bit for bit its neighbour's left one.
                // Anything else leaves a hairline of black or a doubly screened line.
                float left = tileLeft + (tile * i / WallSlices);
                float right = tileLeft + (tile * (i + 1) / WallSlices);

                if (right <= 0f || left >= width)
                {
                    continue;
                }

                float near = Opacity(left / MathF.Max(1f, width));
                float far = Opacity(right / MathF.Max(1f, width));

                if (near <= 0.004f && far <= 0.004f)
                {
                    continue;
                }

                overlay.Picture(
                    _wall.Picture,
                    left,
                    above + (Wave((left + right) / (2f * MathF.Max(1f, width))) * band),
                    right - left,
                    tall,
                    new Vector4(1f, 1f, 1f, near),
                    new Vector4(
                        i / (float)WallSlices,
                        top,
                        ((i + 1) / (float)WallSlices) - (i / (float)WallSlices),
                        shown),
                    OverlayBlend.Screen,
                    new Vector4(1f, 1f, 1f, far));
            }
        }

        overlay.PopClip();
    }

    /// <summary>How much of the wall survives at a point across the window.</summary>
    /// <param name="part">Where across, from nought to one.</param>
    /// <returns>How opaque the wall is there: the edge fade and the drift together.</returns>
    private float Opacity(float part) => Edge(part) * Cloud(part);

    /// <summary>How much of the wall survives this far across the window.</summary>
    /// <param name="part">Where across, from nought to one.</param>
    /// <returns>A multiplier for the slice's opacity.</returns>
    private static float Edge(float part)
    {
        float side = MathF.Min(part, 1f - part) / EdgeWidth;

        return EdgeLeast + ((1f - EdgeLeast) * Math.Clamp(side, 0f, 1f));
    }

    /// <summary>
    /// The slow thinning and thickening that makes the wall read as smoke rather than as a
    /// photograph on a conveyor belt.
    /// </summary>
    /// <param name="part">Where across the window, from nought to one.</param>
    /// <returns>A multiplier for the slice's opacity.</returns>
    private float Cloud(float part)
    {
        // Three waves whose lengths do not divide into one another, so the pattern does not
        // come round again within any sitting, and each drifting at its own rate in its own
        // direction. Across the window rather than with the wall: this is the air in front
        // of it, not a mark on it, and it has to be seen to be moving over a wall that is
        // itself moving.
        float a = MathF.Sin((part * 3.3f) + (_elapsed * 0.043f));
        float b = MathF.Sin((part * 7.9f) - (_elapsed * 0.027f));
        float c = MathF.Sin((part * 15.1f) + (_elapsed * 0.017f));

        float thickness = 0.5f + (0.5f * ((a * 0.5f) + (b * 0.32f) + (c * 0.18f)));

        return 1f - (CloudDepth * thickness);
    }

    /// <summary>How far this part of the wall hangs below where the band is.</summary>
    /// <param name="part">Where across the window, from nought to one.</param>
    /// <returns>A part of the band's height, up or down.</returns>
    private float Wave(float part) =>
        ((MathF.Sin((part * 4.7f) + (_elapsed * 0.090f)) * 0.6f) +
         (MathF.Sin((part * 9.1f) - (_elapsed * 0.055f)) * 0.4f)) * WaveDepth;

    /// <summary>Where the light out of frame is, from nought at the left to one.</summary>
    private float Lamp =>
        0.5f - (0.62f * MathF.Cos(_elapsed * 2f * MathF.PI / LightSeconds));

    /// <summary>Draws the statue's shadow on the wall behind it.</summary>
    private void Shadow(Overlay overlay, Vector4 statue, float bandTop, float band)
    {
        // Away from the light, and never further than a good part of the statue's own
        // width: a shadow that reached the far side of the window reads as a second statue.
        float lean = (0.5f - Lamp) * statue.Z * 0.42f;

        overlay.PushClip(new Vector4(0f, bandTop, overlay.Width, band));

        for (int i = 0; i < ShadowSlices; i++)
        {
            float from = i / (float)ShadowSlices;
            float to = (i + 1) / (float)ShadowSlices;

            // One at the top of the statue and nought at its feet: a cast shadow is pinned
            // where the thing casting it meets the ground.
            float away = 1f - from;

            // Both edges off the statue's own height for the same reason the wall's are
            // off its width: a strip whose bottom is not exactly the next strip's top
            // draws a line across the shadow.
            float top = statue.Y + (statue.W * from);
            float bottom = statue.Y + (statue.W * to);

            overlay.Picture(
                _statue.Picture,
                statue.X + (lean * away) + (statue.Z * 0.06f),
                top,
                statue.Z,
                bottom - top,
                new Vector4(0f, 0f, 0f, 0.34f * away),
                new Vector4(0f, from, 1f, to - from),
                OverlayBlend.Alpha,
                new Vector4(0f, 0f, 0f, 0.34f * (1f - to)),
                down: true);
        }

        overlay.PopClip();
    }

    /// <summary>Draws the light the statue is standing in.</summary>
    private void Light(Overlay overlay, Vector4 statue)
    {
        // Where the lamp is, in the statue's own width. It runs off both ends, which is
        // what "out of view" means here: for most of the cycle the bright part of the beam
        // is off the picture and what crosses the stone is its edge.
        float lamp = ((Lamp * 1.7f) - 0.35f) * statue.Z;
        float reach = MathF.Max(1f, statue.Z * 0.42f);

        // A cosine hill about the lamp, a little under half the statue wide.
        float Beam(float across)
        {
            float distance = MathF.Abs(across - lamp) / reach;

            return distance >= 1f
                ? 0f
                : 0.30f * (0.5f + (0.5f * MathF.Cos(distance * MathF.PI)));
        }

        for (int i = 0; i < LightSlices; i++)
        {
            float near = statue.Z * i / LightSlices;
            float far = statue.Z * (i + 1) / LightSlices;

            float from = Beam(near);
            float to = Beam(far);

            if (from <= 0.002f && to <= 0.002f)
            {
                continue;
            }

            overlay.Picture(
                _statue.Picture,
                statue.X + near,
                statue.Y,
                far - near,
                statue.W,

                // Warm, because what the light is coming past is a wall on fire. Not white:
                // a white light on grey stone in front of a red wall reads as a bloom
                // rather than as somewhere the statue is standing.
                new Vector4(1f, 0.82f, 0.62f, from),
                new Vector4(
                    i / (float)LightSlices,
                    0f,
                    ((i + 1) / (float)LightSlices) - (i / (float)LightSlices),
                    1f),
                OverlayBlend.Screen,
                new Vector4(1f, 0.82f, 0.62f, to));
        }
    }

    /// <summary>Draws whichever sigil is surfacing in the wall, if any.</summary>
    private void Sigil(Overlay overlay, float width, float height, float bandTop, float band)
    {
        if (_sigil < 0 || _sigil >= _sigils.Length)
        {
            return;
        }

        TitleLayer sigil = _sigils[_sigil];

        // In over four seconds and out over the last four, with nothing but the rise in
        // between — so it is still moving when it goes, which is what stops the fade
        // reading as a light being switched off.
        float part = Math.Clamp(_sigilAge / SigilSeconds, 0f, 1f);

        float alpha = SigilOpacity * MathF.Min(
            Math.Clamp(_sigilAge / SigilFade, 0f, 1f),
            Math.Clamp((SigilSeconds - _sigilAge) / SigilFade, 0f, 1f));

        if (alpha <= 0.004f)
        {
            return;
        }

        float tall = band * 0.42f;
        float wide = tall * sigil.SheetAspect;

        // Behind the lettering and inside the wall: it starts across the top of the words
        // and rises out of them, and its whole travel is well within the band, which is
        // what lets it be drawn turned — a turned rectangle is kept whole or dropped whole
        // by a clip, so one that wandered out of the band would vanish rather than be cut.
        var centre = new Vector2(
            width * 0.755f,
            bandTop + (band * 0.50f) - (height * SigilRise * part));

        overlay.Sprite(
            sigil.Picture,
            centre,
            new Vector2(wide, tall),
            new Vector4(SigilInk.X, SigilInk.Y, SigilInk.Z, alpha),

            // A tenth of a radian over the whole appearance, one way for one sigil and the
            // other way for the next, so that three of them in a row are not three of the
            // same gesture.
            (_sigil % 2 == 0 ? SigilTurn : -SigilTurn) * (part - 0.5f),
            OverlayBlend.Multiply);
    }

    /// <summary>Sinks the statue's feet into the black the rows are drawn on.</summary>
    private static void Feet(Overlay overlay, float width, float height, float bandBottom)
    {
        float depth = height - bandBottom;

        if (depth <= 1f)
        {
            return;
        }

        overlay.Fade(
            0f,
            bandBottom,
            width,
            depth,
            new Vector4(0f, 0f, 0f, 0f),
            new Vector4(0f, 0f, 0f, 1f));
    }

    /// <summary>How long to wait before the next sigil.</summary>
    private float Wait() =>
        SigilWaitShortest +
        ((float)_random.NextDouble() * (SigilWaitLongest - SigilWaitShortest));

    /// <summary>The part of a picture that is not transparent, in texture coordinates.</summary>
    /// <param name="image">The picture.</param>
    /// <returns>
    /// Left, top, width and height. The whole of it when nothing in it is transparent.
    /// </returns>
    private static Vector4 Painted(DecodedImage image)
    {
        if (!image.HasAlpha || image.Width <= 0 || image.Height <= 0)
        {
            return new Vector4(0f, 0f, 1f, 1f);
        }

        int left = image.Width;
        int right = -1;
        int top = image.Height;
        int bottom = -1;

        for (int y = 0; y < image.Height; y++)
        {
            int row = y * image.Width * 4;

            for (int x = 0; x < image.Width; x++)
            {
                // Eight rather than nought. The edge of a painted drop shadow is a couple
                // of steps of alpha spread over a hundred pixels of nothing, and measuring
                // to that gives back the whole sheet — which is the thing this exists to
                // avoid.
                if (image.Pixels[row + (x * 4) + 3] <= 8)
                {
                    continue;
                }

                if (x < left)
                {
                    left = x;
                }

                if (x > right)
                {
                    right = x;
                }

                if (y < top)
                {
                    top = y;
                }

                bottom = y;
            }
        }

        if (right < left || bottom < top)
        {
            return new Vector4(0f, 0f, 1f, 1f);
        }

        return new Vector4(
            left / (float)image.Width,
            top / (float)image.Height,
            (right - left + 1) / (float)image.Width,
            (bottom - top + 1) / (float)image.Height);
    }
}
