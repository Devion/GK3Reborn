// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game;

/// <summary>
/// What sort of fire a flame is.
/// </summary>
/// <remarks>
/// The three bitmap sets the artists painted fires with, and they are three different
/// fires: a candle is a still teardrop, a hearth is a wood fire with tongues that come
/// away from it, and the temple's bowl is a body of burning fuel. Nothing else separates
/// them — the models are all the same flat card — so the bitmap is the evidence.
/// </remarks>
public enum FlameKind
{
    /// <summary>
    /// <c>CS5FLAME</c>: the generic flame — candles, lanterns, chafing dishes, braziers.
    /// </summary>
    Candle,

    /// <summary><c>TE2FIRE</c>: a wood fire, in the bar, the chapel and TE1's brazier.</summary>
    Hearth,

    /// <summary><c>TE4FIRETRANSP</c>: the temple's bowl of fire.</summary>
    Cauldron,
}

/// <summary>
/// An open flame standing in a room: a candle, a lantern, a brazier, a fire.
/// </summary>
/// <param name="Model">The model it belongs to, for reporting.</param>
/// <param name="Position">The middle of the flame card, in world space.</param>
/// <param name="Height">How tall the card is, in world units.</param>
/// <param name="Width">How wide it is across its longest horizontal axis.</param>
/// <param name="Visible">Whether the scene draws it as the room opens.</param>
public readonly record struct Flame(
    string Model, Vector3 Position, float Height, float Width, bool Visible)
{
    /// <summary>
    /// How large the fire is, from nought for the smallest flame in the game to one for the
    /// largest.
    /// </summary>
    /// <remarks>
    /// The corpus's flames run from a chafing dish's sterno at 1.4 units tall to the
    /// temple's bowl of fire at 12.6, with the candles, lanterns and braziers between. It
    /// is the one measurement that separates them, and everything about how a flame behaves
    /// is scaled off it: how far its light swings, how quickly, and how much smoke it makes.
    /// </remarks>
    public float Size => Math.Clamp((Height - 1.5f) / 10f, 0f, 1f);

    /// <summary>How far its light swings either side of the light the artists set.</summary>
    /// <remarks>
    /// A candle wavers by about a tenth and a bonfire surges by a quarter, which is what
    /// these two numbers say. Larger than either and a room reads as a strobe rather than
    /// as a room with a fire in it.
    /// </remarks>
    public float Swing => 0.10f + (0.15f * Size);

    /// <summary>
    /// How fast it swings, as the base frequency of the flicker in hertz.
    /// </summary>
    /// <remarks>
    /// <b>Larger fires flicker more slowly.</b> A candle is nervous — a small flame is
    /// pushed about by every draught in the room — and a bonfire surges, because the mass
    /// of burning gas above it takes time to move. Reading it the other way round is the
    /// single thing that makes an artificial fire look artificial.
    /// </remarks>
    public float Rate => 2.2f - (0.9f * Size);

    /// <summary>What sort of fire it is; see <see cref="FlameKind"/>.</summary>
    public FlameKind Kind { get; init; }

    /// <summary>
    /// Which of the model's groups draw the flame card, so that they can be taken out of
    /// the picture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The card is the 1999 fire: a flat quad with a bitmap cycled over it. The volume
    /// drawn in its place is the whole point of drawing a fire as a shader, and the two
    /// cannot both be there — the card is opaque where it is lit, so it would stand as a
    /// grey-brown rectangle in the middle of the flame. See <see cref="Flames.Hide"/>.
    /// </para>
    /// <para>
    /// A list rather than one pair, because a flame card is usually modelled twice, back
    /// to back, and both halves are one fire.
    /// </para>
    /// </remarks>
    public IReadOnlyList<(int Mesh, int Submesh)> Cards
    {
        get => _cards ?? [];
        init => _cards = value;
    }

    private readonly IReadOnlyList<(int Mesh, int Submesh)>? _cards;

    /// <summary>
    /// Which part of the card the artists actually painted a flame on: how far up the foot
    /// of it is, how far up the tip, and how much of the width it takes, all as fractions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A flame card is nearly always bigger than the flame on it.</b> The bar's fire is
    /// a quad twenty-four units tall with a low fire painted across the bottom third of it;
    /// TE4's bowl fills nearly all of its own. Reading the card as the fire makes the bar's
    /// hearth two and a half times the size it has ever been — a bonfire in a fireplace.
    /// </para>
    /// <para>
    /// Nought to one over the whole card when nothing has measured it, which is what
    /// <see cref="Flames.In"/> gives when it is not handed anything to read bitmaps with.
    /// </para>
    /// </remarks>
    public Vector3 Paint
    {
        get => _paint == default ? new Vector3(0f, 1f, 1f) : _paint;
        init => _paint = value;
    }

    private readonly Vector3 _paint;

    /// <summary>How wide the plume is at its widest, in world units.</summary>
    /// <remarks>
    /// Half of as much of the card's width as carries any flame. The volume is allowed to
    /// lean and lick outside that — see the shader — but this is the body of it.
    /// </remarks>
    public float Radius => MathF.Max(Width * Paint.Z / 2f, 0.05f);

    /// <summary>Where the flame stands, at the bottom of the card.</summary>
    public Vector3 Base => Position - new Vector3(0f, Height / 2f, 0f);

    /// <summary>Where the burning gas starts, which is not where the card does.</summary>
    public Vector3 Foot => Base + new Vector3(0f, Paint.X * Height, 0f);

    /// <summary>How tall the burning gas is, in world units.</summary>
    public float Plume => MathF.Max((Paint.Y - Paint.X) * Height, 0.05f);

    /// <summary>
    /// How fast the shape of the fire itself moves, as a multiplier on the clock.
    /// </summary>
    /// <remarks>
    /// The same reading as <see cref="Rate"/> and for the same reason: a small flame is
    /// pushed about by every draught and a large one takes time to move. This is the shape
    /// rather than the light, so it is slower than the flicker — a fire that changes
    /// outline twice a second is a fire in a film played at the wrong speed.
    /// </remarks>
    /// <remarks>
    /// Steeper than it was, reported as a hanging lantern being "way too static": a candle
    /// at 1.4 was a shape that moved once every second and a half, which for something the
    /// size of a thumb reads as a painting of a flame rather than as one.
    /// </remarks>
    public float Churn => 2.6f - (1.7f * Size);

    /// <summary>
    /// Where in its own cycle this fire is, so that no two in a room burn in step.
    /// </summary>
    /// <remarks>
    /// From where it stands rather than from a stream, because it has to be the same on
    /// every run and in both backends, and because CS6's twelve lanterns burning as one is
    /// the thing that gives a room away.
    /// </remarks>
    public float Phase =>
        MathF.Abs(((Position.X * 0.317f) + (Position.Y * 0.113f) + (Position.Z * 0.531f))
            % 97f);
}

/// <summary>
/// Finds the open flames in a room.
/// </summary>
/// <remarks>
/// <para>
/// GK3 draws every fire in the game the same way: a flat quad, always facing the camera,
/// painted with a flame bitmap that a behaviour script cycles through two to eight frames
/// of for as long as the room is loaded. <c>model=te4firetransp, type=gasprop,
/// gas=te4Fire.gas</c> is the temple's bowl of fire, and <c>ANIM Te4FireTransp / LOOP</c>
/// is the whole of the script.
/// </para>
/// <para>
/// So a flame is found by what it is painted with, and there are three bitmaps: the
/// generic <c>CS5FLAME</c> that does for candles, lanterns and chafing dishes across seven
/// rooms, the temple's own <c>TE4FIRETRANSP</c>, and the <c>TE2FIRE</c> set that the
/// hotel bar, the chapel and the temple's brazier share. Nothing else in the corpus is an
/// open flame, and no room's own geometry carries one — every fire in the game is a model
/// the scene places.
/// </para>
/// <para>
/// <b>The authored texture is not enough.</b> Three of them — the bar's fire, the chapel's
/// and the brazier — ship painted with something else entirely (<c>RL2FLOOR</c>,
/// <c>TE1CLMS</c>) and become fire only when their script's first <c>[MTEXTURES]</c> line
/// lands. A model is a flame if <em>any</em> texture it ever draws is one, which is what
/// reading its behaviour script is for.
/// </para>
/// </remarks>
public static class Flames
{
    /// <summary>The bitmaps that are an open flame, by the prefix their names share.</summary>
    /// <remarks>
    /// Prefixes because every one of them is a numbered set: <c>CS5FLAME</c>,
    /// <c>CS5FLAME01</c>, <c>CS5FLAME02</c>; <c>TE4FIRETRANSP1</c> through
    /// <c>TE4FIRETRANSP8</c>; and <c>TE2FIRESM1</c> through <c>TE2FIREHI7T</c>, which is a
    /// fire in three sizes with a blend between each pair.
    /// </remarks>
    private static readonly (string Bitmap, FlameKind Kind)[] Bitmaps =
    [
        ("CS5FLAME", FlameKind.Candle),
        ("TE4FIRETRANSP", FlameKind.Cauldron),
        ("TE2FIRE", FlameKind.Hearth),
    ];

    /// <summary>
    /// How far apart two flame cards of one model have to be to be two flames.
    /// </summary>
    /// <remarks>
    /// A flame card is usually modelled twice, back to back, so that it draws from either
    /// side; both copies occupy the same place and are one fire. <c>TE6_CANDLES</c> is the
    /// case that says the merge cannot simply be "one model, one flame": it is five candles
    /// around a tomb in a single file, a hundred units apart.
    /// </remarks>
    private const float SameFlame = 2f;

    /// <summary>Whether a bitmap is an open flame.</summary>
    /// <param name="texture">The texture's name, with or without an extension.</param>
    /// <returns>True when it is one of the flame sets.</returns>
    public static bool IsFlame(string? texture) => KindOf(texture) is not null;

    /// <summary>What sort of fire a bitmap is.</summary>
    /// <param name="texture">The texture's name, with or without an extension.</param>
    /// <returns>The kind, or null when it is not a flame at all.</returns>
    /// <remarks>
    /// The bitmap is the only evidence there is. Every fire in the game is the same flat
    /// card with the same script over it, so what tells the temple's bowl of fire from a
    /// candle in a lantern is which of the three sets was painted onto it.
    /// </remarks>
    public static FlameKind? KindOf(string? texture)
    {
        if (texture is not { Length: > 0 })
        {
            return null;
        }

        foreach ((string bitmap, FlameKind kind) in Bitmaps)
        {
            if (texture.StartsWith(bitmap, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>Finds every open flame a room places.</summary>
    /// <param name="models">The models the scene loaded, props and actors alike.</param>
    /// <param name="animations">
    /// The animation library, for the textures a flame's script paints onto it. Null finds
    /// only the flames that ship painted as one, which is most but not all of them.
    /// </param>
    /// <param name="bitmaps">
    /// Where to read a flame's own bitmap from, for measuring how much of its card it is
    /// actually painted on; null leaves every fire filling the whole of its card. See
    /// <see cref="Flame.Paint"/>, which is what this is for.
    /// </param>
    /// <returns>One entry per fire, in the order the scene placed them.</returns>
    public static IReadOnlyList<Flame> In(
        IReadOnlyList<PlacedModel> models,
        AnimationLibrary? animations,
        Func<string, DecodedImage?>? bitmaps = null)
    {
        ArgumentNullException.ThrowIfNull(models);

        Dictionary<string, Vector3>? measured = bitmaps is null ? null : [];

        List<Flame> found = [];

        foreach (PlacedModel placed in models)
        {
            // Characters are never fires, and asking would read the whole cast's geometry
            // in every room.
            if (placed.Kind != PlacedModelKind.Prop)
            {
                continue;
            }

            Dictionary<(int Mesh, int Submesh), (FlameKind Kind, string Texture)>? painted = null;

            foreach ((int mesh, int submesh, FlameKind kind, string texture)
                in Painted(placed, animations))
            {
                painted ??= [];
                painted[(mesh, submesh)] = (kind, texture);
            }

            List<Flame> mine = [];

            for (int mesh = 0; mesh < placed.Model.Meshes.Count; mesh++)
            {
                ModMesh group = placed.Model.Meshes[mesh];

                for (int submesh = 0; submesh < group.Submeshes.Count; submesh++)
                {
                    string? bitmap = group.Submeshes[submesh].TextureName;
                    FlameKind? kind = KindOf(bitmap);

                    if (kind is null &&
                        painted is not null &&
                        painted.TryGetValue((mesh, submesh), out (FlameKind Kind, string Texture) swapped))
                    {
                        kind = swapped.Kind;
                        bitmap = swapped.Texture;
                    }

                    if (kind is not { } burning)
                    {
                        continue;
                    }

                    // Mesh space, then the model's own placement, which is the order the
                    // sink puts them in — see ISceneSink.Add.
                    Matrix4x4 toWorld = group.MeshToLocal * placed.Transform;

                    if (Card(placed,
                            group.Submeshes[submesh],
                            toWorld,
                            burning,
                            (mesh, submesh),
                            Painting(bitmap, group.Submeshes[submesh], bitmaps, measured))
                        is { } card)
                    {
                        Merge(mine, card);
                    }
                }
            }

            found.AddRange(mine);
        }

        return found;
    }

    /// <summary>Which of a model's groups its own behaviour script paints with fire.</summary>
    private static IEnumerable<(int Mesh, int Submesh, FlameKind Kind, string Texture)> Painted(
        PlacedModel placed, AnimationLibrary? animations)
    {
        if (animations is null || placed.Idle is not { } script)
        {
            yield break;
        }

        foreach (GasStep step in script.Steps)
        {
            // Both spellings. A fire that never varies is written `ANIM Te4FireTransp,
            // LOOP`; one that does is written as a run of `ONEOF`, which is how the dining
            // room's three chafing dishes avoid burning in step.
            if (step.Action is not (GasAction.Animate or GasAction.OneOf) ||
                step.Name is not { Length: > 0 } named)
            {
                continue;
            }

            if (animations.Read(named) is not { } animation)
            {
                continue;
            }

            foreach (AnimationTexture swap in animation.Textures)
            {
                // The line names the model it was authored against. A script belongs to one
                // model, but the animations it plays are shared — TE2FIREHI is played by
                // the bar's fire, the chapel's and the temple's brazier alike — so the name
                // has to be matched or one room's fire marks another room's floor.
                if (KindOf(swap.Texture) is { } kind &&
                    string.Equals(swap.Model, placed.Name, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (swap.Mesh, swap.Submesh, kind, swap.Texture);
                }
            }
        }
    }

    /// <summary>Measures one flame card in world space.</summary>
    private static Flame? Card(
        PlacedModel placed,
        ModSubmesh group,
        Matrix4x4 toWorld,
        FlameKind kind,
        (int Mesh, int Submesh) card,
        Vector3 paint)
    {
        Vector3[] positions = group.Positions;

        if (positions.Length == 0)
        {
            return null;
        }

        Vector3 low = new(float.MaxValue);
        Vector3 high = new(float.MinValue);
        Vector3 sum = Vector3.Zero;

        foreach (Vector3 local in positions)
        {
            Vector3 world = Vector3.Transform(local, toWorld);

            low = Vector3.Min(low, world);
            high = Vector3.Max(high, world);
            sum += world;
        }

        Vector3 span = high - low;

        return new Flame(
            placed.Name,
            sum / positions.Length,
            span.Y,
            MathF.Max(span.X, span.Z),
            placed.Visible)
        {
            Kind = kind,
            Cards = [card],
            Paint = Along(group, positions, toWorld, paint),
        };
    }

    /// <summary>
    /// Which band of a flame bitmap has any flame on it, and how much of its width.
    /// </summary>
    /// <param name="texture">The bitmap the card draws, or null.</param>
    /// <param name="group">The card, for nothing but a guard on it having any vertices.</param>
    /// <param name="bitmaps">Where to read it from, or null to measure nothing.</param>
    /// <param name="measured">What has already been read, since a room's flames share bitmaps.</param>
    /// <returns>
    /// The lowest and highest painted row as fractions of the image from the top, and the
    /// painted fraction of its width; the whole image when there is nothing to read.
    /// </returns>
    /// <remarks>
    /// <b>Alpha, because these bitmaps are colour-keyed.</b> GK3 marks a texture alpha-tested
    /// by its top-left pixel being magenta, and the decoder turns that magenta into
    /// transparency — so a flame bitmap arrives with nothing anywhere the flame is not. A
    /// threshold rather than any alpha at all: the edge of a keyed shape is a fringe of
    /// nearly-transparent texels once it has been filtered, and counting those puts the tip
    /// of the flame a few rows higher than it is.
    /// </remarks>
    private static Vector3 Painting(
        string? texture,
        ModSubmesh group,
        Func<string, DecodedImage?>? bitmaps,
        Dictionary<string, Vector3>? measured)
    {
        Vector3 whole = new(0f, 1f, 1f);

        if (bitmaps is null || measured is null ||
            texture is not { Length: > 0 } || group.TexCoords.Length == 0)
        {
            return whole;
        }

        if (measured.TryGetValue(texture, out Vector3 known))
        {
            return known;
        }

        measured[texture] = whole;

        if (bitmaps(texture) is not { Width: > 0, Height: > 0 } image)
        {
            return whole;
        }

        int lowRow = int.MaxValue;
        int highRow = -1;
        int lowColumn = int.MaxValue;
        int highColumn = -1;

        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int at = ((y * image.Width) + x) * 4;

                if (at + 3 >= image.Pixels.Length || image.Pixels[at + 3] < 96)
                {
                    continue;
                }

                lowRow = Math.Min(lowRow, y);
                highRow = Math.Max(highRow, y);
                lowColumn = Math.Min(lowColumn, x);
                highColumn = Math.Max(highColumn, x);
            }
        }

        if (highRow < 0)
        {
            // Nothing transparent anywhere, which is what an unkeyed bitmap looks like: the
            // flame fills its card and there is nothing to measure.
            return whole;
        }

        Vector3 band = new(
            lowRow / (float)image.Height,
            (highRow + 1) / (float)image.Height,
            (highColumn - lowColumn + 1) / (float)image.Width);

        measured[texture] = band;
        return band;
    }

    /// <summary>
    /// Turns a band of a bitmap into a band of the card, the way the card is textured.
    /// </summary>
    /// <remarks>
    /// The rows are measured from the top of the image and the card is measured from its
    /// foot, and which way round the two are is the card's own business: a quad may be
    /// textured either way up. So the mapping is taken from the card itself — the texture
    /// coordinate at its lowest corner against the one at its highest — rather than
    /// assumed, and a card textured upside down comes out the same way as one that is not.
    /// </remarks>
    private static Vector3 Along(
        ModSubmesh group, Vector3[] positions, Matrix4x4 toWorld, Vector3 band)
    {
        // Which row of the bitmap a texture coordinate lands on.
        //
        // Wrapped, because GK3's are negative. A flame card's corners carry -0.09
        // and -0.91 rather than 0.91 and 0.09; the sampler repeats and draws the same
        // texels either way, and taking them at face value put the whole painted band
        // outside the card and every fire in the game an inch tall.
        //
        // Left alone within the range, because a card whose top edge is a round 1.0 is a
        // card whose top edge is the bottom row of the bitmap, not the top row of it.
        static float Row(float v) => v is >= 0f and <= 1f ? v : v - MathF.Floor(v);

        if (band == new Vector3(0f, 1f, 1f) || group.TexCoords.Length < positions.Length)
        {
            return band;
        }

        float lowY = float.MaxValue;
        float highY = float.MinValue;
        float atLow = 0f;
        float atHigh = 1f;

        for (int i = 0; i < positions.Length; i++)
        {
            float y = Vector3.Transform(positions[i], toWorld).Y;

            if (y < lowY)
            {
                lowY = y;
                atLow = Row(group.TexCoords[i].Y);
            }

            if (y > highY)
            {
                highY = y;
                atHigh = Row(group.TexCoords[i].Y);
            }
        }

        if (MathF.Abs(atHigh - atLow) < 1e-4f)
        {
            return new Vector3(0f, 1f, band.Z);
        }

        float first = Math.Clamp((band.X - atLow) / (atHigh - atLow), 0f, 1f);
        float last = Math.Clamp((band.Y - atLow) / (atHigh - atLow), 0f, 1f);

        return new Vector3(MathF.Min(first, last), MathF.Max(first, last), band.Z);
    }

    /// <summary>
    /// Finds what a room's fires are burning over.
    /// </summary>
    /// <param name="flames">The fires; see <see cref="In"/>.</param>
    /// <param name="objects">
    /// The room's own named objects and the boxes they fill; see
    /// <see cref="Rendering.ISceneSink.SceneObjectBoxes"/>.
    /// </param>
    /// <returns>One entry per object lying in a fire, with the fire it is lying in.</returns>
    /// <remarks>
    /// <para>
    /// <b>It finds one thing in the whole game, and that is the point.</b> TE4's bowl of
    /// fire has a stone at the bottom of it — <c>te4stonefire_scene</c>, a pebble 1.8 units
    /// across in a bowl ten deep — and taking it out with the right glove is the room's
    /// puzzle. The flame card is opaque where it is lit, so from anywhere but straight
    /// above there is nothing in the bowl but fire, and the player is told about the stone
    /// only by a line of Gabriel's and the scene's own close-up camera.
    /// </para>
    /// <para>
    /// So a thing lying in a fire is given a glint: one still, warm spark held over it,
    /// drawn in front of the flame rather than inside it. It is not the original's
    /// behaviour and it is not meant to be — reported as "the fire stone is very hard to
    /// see unless the camera is pointed straight down into the fire", and the answer is to
    /// make the fire say there is something in it.
    /// </para>
    /// <para>
    /// The test is geometric rather than a name: an object whose middle is inside the
    /// flame's own footprint and below its top. Nothing else in the corpus's 49 fires is
    /// standing in one — the flames sit in lanterns and chafing dishes the room draws as
    /// part of the wall, which carry no object name of their own.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(Flame Fire, string Object, Vector3 Centre)> Holding(
        IReadOnlyList<Flame> flames,
        IReadOnlyList<(string Name, Vector3 Minimum, Vector3 Maximum)> objects)
    {
        ArgumentNullException.ThrowIfNull(flames);
        ArgumentNullException.ThrowIfNull(objects);

        List<(Flame, string, Vector3)> held = [];

        foreach (Flame flame in flames)
        {
            float reach = flame.Width / 2f;

            foreach ((string name, Vector3 minimum, Vector3 maximum) in objects)
            {
                Vector3 centre = (minimum + maximum) / 2f;
                Vector3 span = maximum - minimum;

                // Smaller than the fire it is in, or the bowl the fire stands in would
                // qualify: its own middle is under the flame too.
                if (MathF.Max(span.X, span.Z) >= reach)
                {
                    continue;
                }

                if (MathF.Abs(centre.X - flame.Position.X) > reach ||
                    MathF.Abs(centre.Z - flame.Position.Z) > reach ||
                    centre.Y > flame.Position.Y + (flame.Height / 2f) ||
                    centre.Y < flame.Position.Y - flame.Height)
                {
                    continue;
                }

                held.Add((flame, name, centre));
            }
        }

        return held;
    }

    /// <summary>
    /// Stops the room drawing its flame cards.
    /// </summary>
    /// <param name="flames">The fires; see <see cref="In"/>.</param>
    /// <param name="models">The models the scene loaded, so the cards can be found again.</param>
    /// <returns>How many cards were taken out of the picture.</returns>
    /// <remarks>
    /// <para>
    /// The 1999 fire is a flat quad with a bitmap cycled over it, and it is what the volume
    /// drawn by <see cref="Rendering.Shaders.ParticleShaders"/> replaces. The two cannot
    /// both be drawn: the card is opaque where it is lit and it writes depth, so a fire
    /// with its card still standing is a flame with a brown rectangle through the middle
    /// of it.
    /// </para>
    /// <para>
    /// <b>By part rather than by model.</b> A flame is often one group of something larger
    /// — a lantern, a chafing dish, a candlestick — and hiding the model would take the
    /// lantern with it. It also has to survive a script: TE6 keeps its candles hidden until
    /// somebody lights them, and <c>ShowModel</c> puts back the model without putting back
    /// a part that was switched off separately, which is exactly the behaviour wanted here.
    /// </para>
    /// <para>
    /// What it cannot do is take the card out of the traced world — one instance stands for
    /// a whole model, so a hidden card still occludes a shadow ray, which is what it did
    /// while it was being drawn.
    /// </para>
    /// </remarks>
    public static int Hide(IReadOnlyList<Flame> flames, IReadOnlyList<PlacedModel> models)
    {
        ArgumentNullException.ThrowIfNull(flames);
        ArgumentNullException.ThrowIfNull(models);

        int hidden = 0;

        foreach (Flame flame in flames)
        {
            foreach (PlacedModel placed in models)
            {
                if (!string.Equals(placed.Name, flame.Model, StringComparison.OrdinalIgnoreCase) ||
                    placed.Stage is not { } stage ||
                    !placed.Placement.Exists)
                {
                    continue;
                }

                foreach ((int mesh, int submesh) in flame.Cards)
                {
                    stage.SetPartVisible(placed.Placement, mesh, submesh, visible: false);
                    hidden++;
                }
            }
        }

        return hidden;
    }

    /// <summary>Adds a card to a model's flames, or folds it into the one it doubles.</summary>
    private static void Merge(List<Flame> flames, Flame card)
    {
        for (int i = 0; i < flames.Count; i++)
        {
            if (Vector3.Distance(flames[i].Position, card.Position) <= SameFlame)
            {
                // The taller of the two, so a card modelled slightly short does not shrink
                // the fire it is the back half of.
                flames[i] = flames[i] with
                {
                    Height = MathF.Max(flames[i].Height, card.Height),
                    Width = MathF.Max(flames[i].Width, card.Width),

                    // Both halves, so that hiding the fire hides all of it. The back of a
                    // card is a separate group with its own texture and a fire drawn as a
                    // volume with one of the two still standing is a bright rectangle
                    // through the middle of it.
                    Cards = [.. flames[i].Cards, .. card.Cards],
                };

                return;
            }
        }

        flames.Add(card);
    }
}
