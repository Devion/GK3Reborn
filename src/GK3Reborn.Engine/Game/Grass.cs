// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Foundation;
using GK3Reborn.Rendering;

namespace GK3Reborn.Game;

/// <summary>One kind of ground and the grass grown over it.</summary>
/// <param name="Ground">The ground texture's name, as the room paints it.</param>
/// <param name="Texture">The generated card's name, which the renderer recognises as grass.</param>
/// <param name="Card">The card itself: four clumps of blades side by side, drawn in the ground's own colour.</param>
/// <param name="Clumps">Every clump over that ground, baked into one model in world space.</param>
/// <param name="Count">How many clumps there are.</param>
public readonly record struct GrassBed(
    string Ground, string Texture, DecodedImage Card, ModFile Clumps, int Count);

/// <summary>
/// Grows ankle-high grass over the ground the room paints as grass.
/// </summary>
/// <remarks>
/// <para>
/// A GK3 lawn is a flat texture. This stands clumps of blades on it: three crossed cards a
/// hand high, spaced a stride apart with a little jitter, over every triangle whose texture
/// <c>FLOORMAP.TXT</c> calls grass. The blades are drawn, not shipped, and drawn in the
/// colour of the ground they stand on — the mean of the very texture underneath — so grass
/// over Rennes-le-Château's yellow-green lawn is yellow-green and grass over the wood's is
/// darker, and neither reads as grass from another game.
/// </para>
/// <para>
/// What makes it read as grass rather than as cards: each blade is dark at its root and
/// light at its tip, which is the shading a tuft has in life and the one thing a flat card
/// cannot get from the lights; the card comes in a bright pair and a shaded pair, and a
/// clump under a tree's crown takes the shaded one; and the card's normal is the ground's,
/// so the lights and the traced shadows fall on a blade exactly as they fall on the lawn
/// beside it. It moves in the wind by its own rule: see <see cref="GrassCards"/>.
/// </para>
/// </remarks>
public static class Grass
{
    /// <summary>The shortest and tallest clump, in world units. A character is seventy tall.</summary>
    public const float Shortest = 4.2f;

    /// <inheritdoc cref="Shortest"/>
    public const float Tallest = 7.4f;

    /// <summary>How far apart clumps stand, in world units, before the budget spreads them.</summary>
    public const float Stride = 7f;

    /// <summary>How many clumps a room may have. Twelve vertices each.</summary>
    public const int Budget = 110_000;

    /// <summary>How far a clump's root is sunk into the ground, so no blade floats.</summary>
    private const float Sunk = 0.4f;

    /// <summary>How wide a card is for its height.</summary>
    private const float Spread = 1.9f;

    /// <summary>A card is this many texels square per clump, four clumps side by side.</summary>
    public const int Tile = 128;

    /// <summary>How many clumps one submesh holds, under the sixteen-bit index.</summary>
    private const int ClumpsPerPiece = 5_400;

    /// <summary>How steep the ground may be and still carry grass: the normal's rise.</summary>
    private const float Steepest = 0.5f;

    /// <summary>What <c>FLOORMAP.TXT</c> calls the ground grass grows on.</summary>
    private const string GrassGround = "Grass";

    /// <summary>Whether a ground texture takes grass, and how much.</summary>
    /// <param name="texture">The texture's name.</param>
    /// <param name="groundOf">What <c>FLOORMAP.TXT</c> says the texture is underfoot, or null.</param>
    /// <returns>Nought for no grass; less than one for a blend of grass and something else.</returns>
    public static float Cover(string texture, Func<string?, string?> groundOf)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(groundOf);

        string plain = Path.GetFileNameWithoutExtension(texture);

        // A data quirk: the map calls a transparent checker grass, and nothing grows on it.
        if (plain.Contains("trans", StringComparison.OrdinalIgnoreCase))
        {
            return 0f;
        }

        if (!string.Equals(groundOf(plain), GrassGround, StringComparison.OrdinalIgnoreCase))
        {
            return 0f;
        }

        // Half grass, grass and dirt, grass and rock: the picture is half bare, and the
        // grass over it is half as thick.
        return plain.Contains("HALF", StringComparison.OrdinalIgnoreCase) ||
               plain.Contains("DIRT", StringComparison.OrdinalIgnoreCase) ||
               plain.Contains("ROCK", StringComparison.OrdinalIgnoreCase) ||
               plain.Contains("QTR", StringComparison.OrdinalIgnoreCase)
            ? 0.45f
            : 1f;
    }

    /// <summary>Grows a room's grass.</summary>
    /// <param name="room">The room's geometry.</param>
    /// <param name="groundOf">What <c>FLOORMAP.TXT</c> says a texture is underfoot.</param>
    /// <param name="hidden">The room's objects that are not drawn, by name.</param>
    /// <param name="read">Reads a ground texture by name, for its colour; null where it cannot.</param>
    /// <param name="shade">The tree crowns, under which the grass takes the shaded card.</param>
    /// <param name="seed">Something stable about the room.</param>
    /// <returns>One bed per kind of ground; empty for a room with no grass.</returns>
    public static IReadOnlyList<GrassBed> Grow(
        BspFile room,
        Func<string?, string?> groundOf,
        IReadOnlySet<string> hidden,
        Func<string, DecodedImage?> read,
        IReadOnlyList<Crown> shade,
        ulong seed)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(groundOf);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(shade);

        // Every grassy triangle, by the ground it is painted with, and how much ground
        // there is in all.
        var lawns = new Dictionary<string, List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, float Cover)>>(
            StringComparer.OrdinalIgnoreCase);

        double weighted = 0;

        foreach (BspPolygon polygon in room.Polygons)
        {
            if (polygon.SurfaceIndex < 0 || polygon.SurfaceIndex >= room.Surfaces.Count)
            {
                continue;
            }

            BspSurface surface = room.Surfaces[polygon.SurfaceIndex];

            if (surface.ObjectIndex >= 0 && surface.ObjectIndex < room.ObjectNames.Count &&
                hidden.Contains(room.ObjectNames[surface.ObjectIndex]))
            {
                continue;
            }

            float cover = Cover(surface.TextureName, groundOf);

            if (cover <= 0f)
            {
                continue;
            }

            foreach ((ushort ia, ushort ib, ushort ic) in room.Triangulate(polygon))
            {
                if (ia >= room.Vertices.Length || ib >= room.Vertices.Length || ic >= room.Vertices.Length)
                {
                    continue;
                }

                Vector3 a = room.Vertices[ia];
                Vector3 b = room.Vertices[ib];
                Vector3 c = room.Vertices[ic];
                Vector3 cross = Vector3.Cross(b - a, c - a);
                float twice = cross.Length();

                if (twice < 1e-3f)
                {
                    continue;
                }

                Vector3 normal = cross / twice;

                // Grass grows up. A wall painted with lawn is a wall, and a slope steeper
                // than about sixty degrees is a bank nothing stands on.
                if (normal.Y < 0f)
                {
                    normal = -normal;
                }

                if (normal.Y < Steepest)
                {
                    continue;
                }

                string ground = Path.GetFileNameWithoutExtension(surface.TextureName);

                if (!lawns.TryGetValue(ground, out var triangles))
                {
                    triangles = [];
                    lawns[ground] = triangles;
                }

                triangles.Add((a, b, c, normal, cover));
                weighted += twice * 0.5 * cover;
            }
        }

        if (lawns.Count == 0)
        {
            return [];
        }

        // A stride apart, or further where the room has more lawn than the budget: the
        // whole room thins evenly rather than the last bed going without.
        float stride = Stride;
        double wanted = weighted / (stride * stride);

        if (wanted > Budget)
        {
            stride *= (float)Math.Sqrt(wanted / Budget);
        }

        var random = new DeterministicRandom(seed);
        var beds = new List<GrassBed>();

        foreach ((string ground, var triangles) in lawns.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            Vector3 colour = ColourOf(read(ground));
            var card = new DecodedImage(
                Tile * 4, Tile, Draw(colour, unchecked(seed + (ulong)ground.Length)), HasAlpha: true, "grass");

            List<ModMesh> pieces = [];
            var positions = new List<Vector3>(ClumpsPerPiece * 12);
            var normals = new List<Vector3>(ClumpsPerPiece * 12);
            var uvs = new List<Vector2>(ClumpsPerPiece * 12);
            var indices = new List<ushort>(ClumpsPerPiece * 18);
            int count = 0;
            string texture = GrassCards.TexturePrefix + ground.ToUpperInvariant();

            void Flush()
            {
                if (positions.Count == 0)
                {
                    return;
                }

                var least = new Vector3(float.MaxValue);
                var most = new Vector3(float.MinValue);

                foreach (Vector3 at in positions)
                {
                    least = Vector3.Min(least, at);
                    most = Vector3.Max(most, at);
                }

                pieces.Add(new ModMesh
                {
                    Name = $"grass {pieces.Count}",
                    MeshToLocal = Matrix4x4.Identity,
                    BoundsMin = least,
                    BoundsMax = most,
                    Submeshes =
                    [
                        new ModSubmesh
                        {
                            TextureName = texture,
                            Color = (255, 255, 255),
                            Positions = [.. positions],
                            Normals = [.. normals],
                            TexCoords = [.. uvs],
                            Indices = [.. indices],
                        },
                    ],
                });

                positions.Clear();
                normals.Clear();
                uvs.Clear();
                indices.Clear();
            }

            foreach ((Vector3 a, Vector3 b, Vector3 c, Vector3 normal, float cover) in triangles)
            {
                float area = Vector3.Cross(b - a, c - a).Length() * 0.5f;
                double expected = area * cover / (stride * stride);
                int clumps = (int)expected;

                // The fraction left over is a clump some of the time, so a lawn of small
                // triangles is not bare because each is under one clump's worth.
                if (random.NextDouble() < expected - clumps)
                {
                    clumps++;
                }

                for (int i = 0; i < clumps; i++)
                {
                    // Uniform over the triangle.
                    float r1 = (float)random.NextDouble();
                    float r2 = (float)random.NextDouble();

                    if (r1 + r2 > 1f)
                    {
                        r1 = 1f - r1;
                        r2 = 1f - r2;
                    }

                    Vector3 foot = a + ((b - a) * r1) + ((c - a) * r2);

                    float height = Shortest + ((Tallest - Shortest) * (float)random.NextDouble());
                    float yaw = (float)random.NextDouble() * MathF.PI;

                    // The shaded card under a crown, and now and then elsewhere.
                    bool shaded = random.NextDouble() < 0.08 || Under(shade, foot);
                    int tile = (random.NextDouble() < 0.5 ? 0 : 1) + (shaded ? 2 : 0);

                    Clump(positions, normals, uvs, indices, foot, normal, height, yaw, tile);
                    count++;

                    if (positions.Count >= ClumpsPerPiece * 12)
                    {
                        Flush();
                    }
                }
            }

            Flush();

            if (pieces.Count > 0)
            {
                beds.Add(new GrassBed(
                    ground, texture, card, ModFile.FromMeshes(texture, pieces), count));
            }
        }

        return beds;
    }

    /// <summary>Whether a point on the ground is under a tree.</summary>
    private static bool Under(IReadOnlyList<Crown> shade, Vector3 foot)
    {
        foreach (Crown crown in shade)
        {
            if (foot.X >= crown.Least.X && foot.X <= crown.Most.X &&
                foot.Z >= crown.Least.Z && foot.Z <= crown.Most.Z &&
                foot.Y < crown.Most.Y)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Three cards crossed at sixty degrees, standing on a point.</summary>
    private static void Clump(
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector2> uvs,
        List<ushort> indices,
        Vector3 foot,
        Vector3 normal,
        float height,
        float yaw,
        int tile)
    {
        float half = height * Spread * 0.5f;
        float u0 = tile / 4f;
        float u1 = (tile + 1) / 4f;

        Vector3 root = foot - (normal * Sunk);
        Vector3 top = normal * (height + Sunk);

        for (int k = 0; k < 3; k++)
        {
            float turn = yaw + (k * MathF.PI / 3f);
            var across = new Vector3(MathF.Cos(turn), 0f, MathF.Sin(turn)) * half;

            int first = positions.Count;

            positions.Add(root - across);
            positions.Add(root + across);
            positions.Add(root + across + top);
            positions.Add(root - across + top);

            for (int i = 0; i < 4; i++)
            {
                normals.Add(normal);
            }

            uvs.Add(new Vector2(u0, 1f));
            uvs.Add(new Vector2(u1, 1f));
            uvs.Add(new Vector2(u1, 0f));
            uvs.Add(new Vector2(u0, 0f));

            indices.Add((ushort)first);
            indices.Add((ushort)(first + 1));
            indices.Add((ushort)(first + 2));
            indices.Add((ushort)first);
            indices.Add((ushort)(first + 2));
            indices.Add((ushort)(first + 3));
        }
    }

    /// <summary>The colour of a ground texture: its mean, less the transparent texels.</summary>
    /// <param name="image">The texture, or null for a ground that could not be read.</param>
    /// <returns>Red, green and blue from nought to one, as the picture encodes them.</returns>
    public static Vector3 ColourOf(DecodedImage? image)
    {
        if (image is not { Width: > 0, Height: > 0 } picture || picture.Pixels.Length < 4)
        {
            // A lawn nobody could read: a plausible green.
            return new Vector3(0.36f, 0.42f, 0.20f);
        }

        // Every eighth texel each way is plenty for a mean.
        int step = Math.Max(1, Math.Min(picture.Width, picture.Height) / 64);
        double r = 0, g = 0, b = 0;
        int counted = 0;

        for (int y = 0; y < picture.Height; y += step)
        {
            for (int x = 0; x < picture.Width; x += step)
            {
                int at = ((y * picture.Width) + x) * 4;

                if (at + 3 >= picture.Pixels.Length || picture.Pixels[at + 3] < 128)
                {
                    continue;
                }

                r += picture.Pixels[at];
                g += picture.Pixels[at + 1];
                b += picture.Pixels[at + 2];
                counted++;
            }
        }

        return counted == 0
            ? new Vector3(0.36f, 0.42f, 0.20f)
            : new Vector3((float)(r / counted), (float)(g / counted), (float)(b / counted)) / 255f;
    }

    /// <summary>Draws the card: four clumps of blades side by side, two bright and two shaded.</summary>
    /// <param name="ground">The ground's colour, from <see cref="ColourOf"/>.</param>
    /// <param name="seed">Something stable, so the same lawn draws the same card.</param>
    /// <returns>The card's texels, red, green, blue and alpha, top row first.</returns>
    public static byte[] Draw(Vector3 ground, ulong seed)
    {
        byte[] pixels = new byte[Tile * 4 * Tile * 4];

        // A blade is the ground's colour leant a little toward green and lifted a little:
        // it is the same plant the lawn texture is a picture of, seen standing up and
        // catching the light along its length. Leant and lifted only a little, so the
        // colour match survives; a fresh green mixed here reads as grass from another game.
        Vector3 blade = Vector3.Clamp(
            (ground * new Vector3(1.02f, 1.10f, 0.88f)) + new Vector3(0.04f, 0.06f, 0.01f),
            Vector3.Zero,
            Vector3.One);

        for (int tile = 0; tile < 4; tile++)
        {
            // The shaded pair is the bright pair again, drawn darker: the same clumps, so a
            // lawn is two shapes in two lights rather than four shapes.
            var random = new DeterministicRandom(unchecked(seed * 31 + (ulong)(tile % 2)));
            float shade = tile < 2 ? 1f : 0.62f;

            DrawClump(pixels, tile * Tile, blade * shade, random);
        }

        return pixels;
    }

    private static void DrawClump(byte[] pixels, int left, Vector3 colour, DeterministicRandom random)
    {
        int stride = Tile * 4;

        // Blades from the back to the front, the back ones darker: what a tuft looks like
        // is mostly the ones in front hiding the ones behind.
        var blades = new List<(float X, float Lean, float Length, float Width, float Bright)>();

        for (int i = 0; i < 18; i++)
        {
            blades.Add((
                X: 12f + ((Tile - 24f) * (float)random.NextDouble()),
                Lean: ((float)random.NextDouble() - 0.5f) * 70f,
                Length: (Tile * 0.5f) + (Tile * 0.47f * (float)random.NextDouble()),
                Width: 4.5f + (2.5f * (float)random.NextDouble()),
                Bright: 0.82f + (0.42f * (float)random.NextDouble())));
        }

        // And short ones, so the base of the clump is full.
        for (int i = 0; i < 8; i++)
        {
            blades.Add((
                X: 8f + ((Tile - 16f) * (float)random.NextDouble()),
                Lean: ((float)random.NextDouble() - 0.5f) * 40f,
                Length: (Tile * 0.2f) + (Tile * 0.22f * (float)random.NextDouble()),
                Width: 4f + (2.5f * (float)random.NextDouble()),
                Bright: 0.7f + (0.3f * (float)random.NextDouble())));
        }

        blades.Sort((a, b) => a.Bright.CompareTo(b.Bright));

        foreach ((float x0, float lean, float length, float width, float bright) in blades)
        {
            float steps = length * 1.5f;

            for (int s = 0; s <= steps; s++)
            {
                float t = s / steps;

                // A quadratic bend: straight up from the root, leaning over toward the tip.
                float x = x0 + (lean * t * t);
                float y = (Tile - 1) - (length * t) + (MathF.Abs(lean) * 0.12f * t * t);
                float radius = MathF.Max(width * 0.5f * (1f - (t * 0.9f)), 0.55f);

                // Dark at the root, light at the tip: the shading a tuft has in life.
                float along = 0.55f + (0.75f * t);
                Vector3 c = Vector3.Clamp(colour * bright * along, Vector3.Zero, Vector3.One);

                Disc(pixels, stride, left, x, y, radius, c);
            }
        }
    }

    private static void Disc(byte[] pixels, int stride, int left, float cx, float cy, float radius, Vector3 colour)
    {
        int x0 = Math.Max((int)MathF.Floor(cx - radius - 1f), 0);
        int x1 = Math.Min((int)MathF.Ceiling(cx + radius + 1f), Tile - 1);
        int y0 = Math.Max((int)MathF.Floor(cy - radius - 1f), 0);
        int y1 = Math.Min((int)MathF.Ceiling(cy + radius + 1f), Tile - 1);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float d = MathF.Sqrt(((x + 0.5f - cx) * (x + 0.5f - cx)) + ((y + 0.5f - cy) * (y + 0.5f - cy)));
                float cover = Math.Clamp(radius + 0.5f - d, 0f, 1f);

                if (cover <= 0f)
                {
                    continue;
                }

                int at = ((y * stride) + left + x) * 4;
                float had = pixels[at + 3] / 255f;

                // Painted over what is there: a later blade in front of an earlier one.
                float alpha = MathF.Max(had, cover);
                float mix = had > 0f ? cover : 1f;

                pixels[at] = (byte)Math.Clamp((pixels[at] * (1f - mix)) + (colour.X * 255f * mix), 0f, 255f);
                pixels[at + 1] = (byte)Math.Clamp((pixels[at + 1] * (1f - mix)) + (colour.Y * 255f * mix), 0f, 255f);
                pixels[at + 2] = (byte)Math.Clamp((pixels[at + 2] * (1f - mix)) + (colour.Z * 255f * mix), 0f, 255f);
                pixels[at + 3] = (byte)(alpha * 255f);
            }
        }
    }
}
