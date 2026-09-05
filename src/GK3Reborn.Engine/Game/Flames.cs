// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Formats.Models;

namespace GK3Reborn.Game;

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
    private static readonly string[] Bitmaps = ["CS5FLAME", "TE4FIRETRANSP", "TE2FIRE"];

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
    public static bool IsFlame(string? texture)
    {
        if (texture is not { Length: > 0 })
        {
            return false;
        }

        foreach (string bitmap in Bitmaps)
        {
            if (texture.StartsWith(bitmap, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds every open flame a room places.</summary>
    /// <param name="models">The models the scene loaded, props and actors alike.</param>
    /// <param name="animations">
    /// The animation library, for the textures a flame's script paints onto it. Null finds
    /// only the flames that ship painted as one, which is most but not all of them.
    /// </param>
    /// <returns>One entry per fire, in the order the scene placed them.</returns>
    public static IReadOnlyList<Flame> In(
        IReadOnlyList<PlacedModel> models, AnimationLibrary? animations)
    {
        ArgumentNullException.ThrowIfNull(models);

        List<Flame> found = [];

        foreach (PlacedModel placed in models)
        {
            // Characters are never fires, and asking would read the whole cast's geometry
            // in every room.
            if (placed.Kind != PlacedModelKind.Prop)
            {
                continue;
            }

            HashSet<(int Mesh, int Submesh)>? painted = null;

            foreach ((int mesh, int submesh) in Painted(placed, animations))
            {
                painted ??= [];
                painted.Add((mesh, submesh));
            }

            List<Flame> mine = [];

            for (int mesh = 0; mesh < placed.Model.Meshes.Count; mesh++)
            {
                ModMesh group = placed.Model.Meshes[mesh];

                for (int submesh = 0; submesh < group.Submeshes.Count; submesh++)
                {
                    if (!IsFlame(group.Submeshes[submesh].TextureName) &&
                        !(painted?.Contains((mesh, submesh)) ?? false))
                    {
                        continue;
                    }

                    // Mesh space, then the model's own placement, which is the order the
                    // sink puts them in — see ISceneSink.Add.
                    Matrix4x4 toWorld = group.MeshToLocal * placed.Transform;

                    if (Card(placed, group.Submeshes[submesh].Positions, toWorld) is { } card)
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
    private static IEnumerable<(int Mesh, int Submesh)> Painted(
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
                if (IsFlame(swap.Texture) &&
                    string.Equals(swap.Model, placed.Name, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (swap.Mesh, swap.Submesh);
                }
            }
        }
    }

    /// <summary>Measures one flame card in world space.</summary>
    private static Flame? Card(
        PlacedModel placed, Vector3[] positions, Matrix4x4 toWorld)
    {
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
            placed.Visible);
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
                };

                return;
            }
        }

        flames.Add(card);
    }
}
