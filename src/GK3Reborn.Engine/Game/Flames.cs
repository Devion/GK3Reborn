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
    public float Size => Math.Clamp((Height - 1.5f) / 10f, 0f, 1f);

    /// <summary>How far its light swings either side of the light the artists set.</summary>
    public float Swing => 0.10f + (0.15f * Size);

    /// <summary>
    /// How fast it swings, as the base frequency of the flicker in hertz.
    /// </summary>
    public float Rate => 2.2f - (0.9f * Size);

    /// <summary>What sort of fire it is; see <see cref="FlameKind"/>.</summary>
    public FlameKind Kind { get; init; }

    /// <summary>
    /// Which of the model's groups draw the flame card, so that they can be taken out of
    /// the picture.
    /// </summary>
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
    public Vector3 Paint
    {
        get => _paint == default ? new Vector3(0f, 1f, 1f) : _paint;
        init => _paint = value;
    }

    private readonly Vector3 _paint;

    /// <summary>How wide the plume is at its widest, in world units.</summary>
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
    public float Churn => 2.6f - (1.7f * Size);

    /// <summary>
    /// Where in its own cycle this fire is, so that no two in a room burn in step.
    /// </summary>
    public float Phase =>
        MathF.Abs(((Position.X * 0.317f) + (Position.Y * 0.113f) + (Position.Z * 0.531f))
            % 97f);
}

/// <summary>
/// Finds the open flames in a room.
/// </summary>
public static class Flames
{
    /// <summary>The bitmaps that are an open flame, by the prefix their names share.</summary>
    private static readonly (string Bitmap, FlameKind Kind)[] Bitmaps =
    [
        ("CS5FLAME", FlameKind.Candle),
        ("TE4FIRETRANSP", FlameKind.Cauldron),
        ("TE2FIRE", FlameKind.Hearth),
    ];

    /// <summary>
    /// How far apart two flame cards of one model have to be to be two flames.
    /// </summary>
    private const float SameFlame = 2f;

    /// <summary>Whether a bitmap is an open flame.</summary>
    /// <param name="texture">The texture's name, with or without an extension.</param>
    /// <returns>True when it is one of the flame sets.</returns>
    public static bool IsFlame(string? texture) => KindOf(texture) is not null;

    /// <summary>What sort of fire a bitmap is.</summary>
    /// <param name="texture">The texture's name, with or without an extension.</param>
    /// <returns>The kind, or null when it is not a flame at all.</returns>
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
