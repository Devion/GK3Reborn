using System.Numerics;
using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Game.Navigation;

/// <summary>
/// Where an actor is allowed to stand, as a bitmap laid over the floor.
/// </summary>
public sealed class WalkBoundary
{
    /// <summary>
    /// The regions that are closed unless a script opens them.
    /// </summary>
    private static readonly int[] ClosedByDefault = [8, 9, 255];

    private readonly IndexedImage _image;
    private readonly HashSet<int> _closed = [.. ClosedByDefault];
    private readonly Dictionary<string, (Vector2 Minimum, Vector2 Maximum)> _blocked =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a boundary.</summary>
    /// <param name="image">The boundary bitmap, as palette indices.</param>
    /// <param name="size">How much of the world the bitmap covers, in scene units.</param>
    /// <param name="offset">Where the world origin sits within it, in scene units.</param>
    public WalkBoundary(IndexedImage image, Vector2 size, Vector2 offset)
    {
        _image = image;
        Size = size;
        Offset = offset;
    }

    /// <summary>How much of the world the bitmap covers, on X and Z.</summary>
    public Vector2 Size { get; }

    /// <summary>Where the world origin sits within the covered area.</summary>
    public Vector2 Offset { get; }

    /// <summary>Width of the bitmap, in texels.</summary>
    public int Width => _image.Width;

    /// <summary>Height of the bitmap, in texels.</summary>
    public int Height => _image.Height;

    /// <summary>How many scene units a texel covers, on X and Z.</summary>
    public Vector2 TexelSize =>
        new(Size.X / MathF.Max(1, _image.Width), Size.Y / MathF.Max(1, _image.Height));

    /// <summary>Reads a scene's boundary, if it declares one and the bitmap is there.</summary>
    /// <param name="bitmap">The bitmap's bytes, or null if the archives do not have it.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="size">The declared size.</param>
    /// <param name="offset">The declared offset.</param>
    /// <returns>The boundary, or null.</returns>
    public static WalkBoundary? From(byte[]? bitmap, string name, Vector2 size, Vector2 offset)
    {
        if (bitmap is null || size.X <= 0 || size.Y <= 0)
        {
            return null;
        }

        return new WalkBoundary(BitmapDecoder.DecodeIndexed(bitmap, name), size, offset);
    }

    /// <summary>The region at a point in the world.</summary>
    /// <param name="world">The point. Only X and Z are read.</param>
    /// <returns>The palette index, or 255 for anywhere outside the bitmap.</returns>
    public int RegionAt(Vector3 world)
    {
        (int x, int y) = ToTexel(world);

        return x < 0 || y < 0 || x >= _image.Width || y >= _image.Height
            ? 255
            : _image.Indices[(y * _image.Width) + x];
    }

    /// <summary>Whether an actor may stand at a point.</summary>
    /// <param name="world">The point. Only X and Z are read.</param>
    /// <returns>True when the region there is open and nothing is standing on it.</returns>
    public bool IsWalkable(Vector3 world) =>
        !_closed.Contains(RegionAt(world)) && !IsBlocked(world.X, world.Z);

    /// <summary>Whether a region is open.</summary>
    /// <param name="region">The palette index.</param>
    /// <returns>True when an actor may stand in it.</returns>
    public bool IsRegionOpen(int region) => !_closed.Contains(region);

    /// <summary>Opens or closes a region.</summary>
    /// <param name="region">The palette index.</param>
    /// <param name="open">True to let actors through.</param>
    public void SetRegionOpen(int region, bool open)
    {
        if (!open)
        {
            _closed.Add(region);
            return;
        }

        if (!ClosedByDefault.Contains(region))
        {
            _closed.Remove(region);
        }
    }

    /// <summary>Shuts off a rectangle of the floor, under a name that can undo it.</summary>
    /// <param name="name">What is standing there; blocking the same name twice moves it.</param>
    /// <param name="minimum">Lower corner, on X and Z.</param>
    /// <param name="maximum">Upper corner, on X and Z.</param>
    public void Block(string name, Vector2 minimum, Vector2 maximum)
    {
        ArgumentNullException.ThrowIfNull(name);

        _blocked[name] = (Vector2.Min(minimum, maximum), Vector2.Max(minimum, maximum));
    }

    /// <summary>Takes a blocked rectangle away.</summary>
    /// <param name="name">The name it was blocked under.</param>
    /// <returns>True when something was there to remove.</returns>
    public bool Unblock(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _blocked.Remove(name);
    }

    /// <summary>What is standing in the way, and where, in a stable order.</summary>
    public IReadOnlyList<(string Name, Vector2 Minimum, Vector2 Maximum)> Blocked =>
        [.. _blocked.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase)
            .Select(b => (b.Key, b.Value.Minimum, b.Value.Maximum))];

    /// <summary>The texel a world position falls in.</summary>
    /// <param name="world">The point. Only X and Z are read.</param>
    /// <returns>Column and row, from the top-left, which may be outside the bitmap.</returns>
    public (int X, int Y) ToTexel(Vector3 world)
    {
        float u = (world.X + Offset.X) / Size.X;
        float v = (world.Z + Offset.Y) / Size.Y;

        return ((int)MathF.Floor(u * _image.Width), (int)MathF.Floor((1f - v) * _image.Height));
    }

    /// <summary>The middle of a texel, in the world.</summary>
    /// <param name="x">Column, from the left.</param>
    /// <param name="y">Row, from the top.</param>
    /// <returns>The point, with Y left at zero.</returns>
    public Vector3 ToWorld(int x, int y)
    {
        float u = (x + 0.5f) / _image.Width;
        float v = 1f - ((y + 0.5f) / _image.Height);

        return new Vector3((u * Size.X) - Offset.X, 0f, (v * Size.Y) - Offset.Y);
    }

    /// <summary>Whether an actor may stand on a texel.</summary>
    /// <param name="x">Column, from the left.</param>
    /// <param name="y">Row, from the top.</param>
    /// <returns>True when the region there is open.</returns>
    public bool IsTexelWalkable(int x, int y)
    {
        if (!IsRegionOpen(RegionOf(x, y)))
        {
            return false;
        }

        Vector3 centre = ToWorld(x, y);
        return !IsBlocked(centre.X, centre.Z);
    }

    /// <summary>Whether something is standing on a point.</summary>
    private bool IsBlocked(float x, float z)
    {
        foreach ((Vector2 minimum, Vector2 maximum) in _blocked.Values)
        {
            if (x >= minimum.X && x <= maximum.X && z >= minimum.Y && z <= maximum.Y)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The nearest texel an actor may stand on.</summary>
    /// <param name="world">The point. Only X and Z are read.</param>
    /// <returns>Column and row, or null when nothing in the bitmap is open.</returns>
    public (int X, int Y)? NearestWalkableTexel(Vector3 world)
    {
        (int x, int y) = ToTexel(world);

        if (IsTexelWalkable(x, y))
        {
            return (x, y);
        }

        (int X, int Y)? nearest = null;
        long nearestDistance = long.MaxValue;

        for (int row = 0; row < Height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                if (!IsTexelWalkable(column, row))
                {
                    continue;
                }

                long dx = column - x;
                long dy = row - y;
                long distance = (dx * dx) + (dy * dy);

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = (column, row);
                }
            }
        }

        return nearest;
    }

    /// <summary>The nearest point an actor may stand on.</summary>
    /// <param name="world">The point. Only X and Z are read.</param>
    /// <returns>
    /// The point itself when it is already open, the middle of the nearest open texel
    /// otherwise, or null when the boundary has no open texel at all.
    /// </returns>
    public Vector3? NearestWalkable(Vector3 world) =>
        IsWalkable(world)
            ? world
            : NearestWalkableTexel(world) is { } texel ? ToWorld(texel.X, texel.Y) : null;

    /// <summary>The region at a texel.</summary>
    /// <param name="x">Column, from the left.</param>
    /// <param name="y">Row, from the top.</param>
    /// <returns>The palette index, or 255 outside the bitmap.</returns>
    public int RegionOf(int x, int y) =>
        x < 0 || y < 0 || x >= _image.Width || y >= _image.Height
            ? 255
            : _image.Indices[(y * _image.Width) + x];

    /// <summary>How many texels an actor may stand on.</summary>
    /// <returns>The count, useful as a sanity check that a boundary loaded at all.</returns>
    public int WalkableTexels()
    {
        int count = 0;

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (IsTexelWalkable(x, y))
                {
                    count++;
                }
            }
        }

        return count;
    }
}
