using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// The textures the device holds, kept across rooms.
/// </summary>
public sealed class TextureCache : IDisposable
{
    private readonly IGeometryDevice _device;
    private readonly Dictionary<string, IGeometryTexture> _textures =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _keyed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the holes in a keyed texture say about the shape drawn on it.</summary>
    private readonly Dictionary<string, CutoutMask> _cutouts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IGeometryTexture> _normals =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IGeometryTexture> _orms =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IGeometryTexture> _heights =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Height maps kept as numbers as well as as pictures.</summary>
    private readonly Dictionary<string, HeightField> _fields =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a cache over a device.</summary>
    /// <param name="device">Where the textures go.</param>
    /// <param name="fallback">Drawn wherever a texture is asked for and missing.</param>
    public TextureCache(IGeometryDevice device, DecodedImage fallback)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        Fallback = device.CreateTexture(fallback);

        // A normal pointing straight out of the surface, for everything with no map. It is
        // (0.5, 0.5, 1) rather than white, because the shader decodes a normal from the
        // range 0..1 back to -1..1 and white would tilt every surface into a corner.
        Flat = device.CreateTexture(
            new DecodedImage(1, 1, [128, 128, 255, 255], HasAlpha: false, "flat-normal"),
            GeometryTextureKind.Data,
            mipmaps: false);

        // Occlusion, roughness and metalness, in that order, for everything with no map:
        // unoccluded, fully rough, not a metal. Which is exactly the surface the renderer
        // drew before any of this existed, so a batch that binds this is unchanged by it.
        //
        // Linear, like the normal map and for the same reason. These three channels are
        // numbers rather than a colour, and an sRGB upload would bend every one of them.
        Neutral = device.CreateTexture(
            new DecodedImage(1, 1, [255, 255, 0, 255], HasAlpha: false, "neutral-orm"),
            GeometryTextureKind.Data,
            mipmaps: false);

        // A height map at the middle of its range, which is the surface as modelled: half
        // is the plane the geometry is actually on, and displacement is measured either
        // side of it. It costs nothing on its own, because the shader's height scale is
        // zero for any surface with no map, so this is never sampled into an offset.
        Level = device.CreateTexture(
            new DecodedImage(1, 1, [128, 128, 128, 255], HasAlpha: false, "level-height"),
            GeometryTextureKind.Data,
            mipmaps: false);

        // Bound in the lightmap slot wherever a batch has none. Both APIs require every
        // declared binding to point at something valid even when the shader ignores what it
        // reads, and white is the value that makes ignoring it harmless: the shader
        // multiplies by it.
        White = device.CreateTexture(
            new DecodedImage(1, 1, [255, 255, 255, 255], HasAlpha: false, "white"),
            GeometryTextureKind.Colour,
            mipmaps: false);
    }

    /// <summary>Drawn wherever a texture is asked for and missing.</summary>
    public IGeometryTexture Fallback { get; }

    /// <summary>Solid white, bound in the lightmap slot wherever a batch has no lightmap.</summary>
    public IGeometryTexture White { get; }

    /// <summary>A normal pointing straight out, bound wherever a surface has no map.</summary>
    public IGeometryTexture Flat { get; }

    /// <summary>Neutral occlusion, roughness and metalness, bound where a surface has none.</summary>
    public IGeometryTexture Neutral { get; }

    /// <summary>A height map at mid grey, bound where a surface has none.</summary>
    public IGeometryTexture Level { get; }

    /// <summary>How large a height field is kept for the CPU, in texels.</summary>
    private const int FieldExtent = 256;

    /// <summary>How many normal maps the device is holding.</summary>
    public int NormalCount => _normals.Count;

    /// <summary>How many ORM maps the device is holding.</summary>
    public int OrmCount => _orms.Count;

    /// <summary>How many height maps the device is holding.</summary>
    public int HeightCount => _heights.Count;

    /// <summary>How many textures the device is holding.</summary>
    public int Count => _textures.Count;

    /// <summary>How many were asked for and already here.</summary>
    public int Reused { get; private set; }

    /// <summary>
    /// The textures whose transparency is keyed rather than authored.
    /// </summary>
    public IReadOnlySet<string> Keyed => _keyed;

    /// <summary>
    /// Whether keyed textures are measured for the lattice of bars that may be drawn on
    /// them.
    /// </summary>
    public bool MeasureCutouts { get; set; }

    /// <summary>What the holes in a texture measured as, if it is a lattice of bars.</summary>
    /// <param name="name">The texture's name.</param>
    /// <returns>The mask, or null for every texture that is nobody's railing.</returns>
    public CutoutMask? Cutout(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _cutouts.GetValueOrDefault(name);
    }

    /// <summary>Whether a texture is already here.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>True when nothing needs reading, decoding or uploading.</returns>
    public bool Has(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _textures.ContainsKey(name);
    }

    /// <summary>Uploads a texture, or keeps the one already here.</summary>
    /// <param name="name">Its name, matched without regard to case.</param>
    /// <param name="image">The decoded image.</param>
    public void Add(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.ContainsKey(name))
        {
            Reused++;
            return;
        }

        // Keying happens before upload so that mip generation never sees the key colour;
        // see TextureKeying.
        DecodedImage keyed = TextureKeying.Apply(image);

        if (keyed.HasAlpha)
        {
            _keyed.Add(name);

            // Measured here because this is the last place the texels exist as numbers: the
            // next line hands them to the device and they are a picture from then on.
            if (MeasureCutouts &&
                !CutoutCards.Leaves.Contains(name) &&
                CutoutMask.Measure(keyed) is { } cutout)
            {
                _cutouts[name] = cutout;
            }
        }

        _textures[name] = _device.CreateTexture(keyed);
        DeviceBytes += WithMips(keyed.Width, keyed.Height);
    }

    /// <summary>Uploads a block-compressed texture, or keeps the one already here.</summary>
    /// <param name="name">Its name, matched without regard to case.</param>
    /// <param name="image">The compressed levels.</param>
    public void Add(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.ContainsKey(name))
        {
            Reused++;
            return;
        }

        // Whether this picture has holes, and if so what shape the bars around them are.
        // Both questions are answered from one expanded level, because expanding is the
        // expensive half and asking twice would double a room's load for nothing.
        //
        // <b>The first question has to be asked here and not only on the decoded path.</b>
        // A keyed texture reaches the device as blocks whenever the pack holds it, which is
        // every shipped build; asking only where texels arrive as texels left `Keyed` empty
        // in exactly the configuration players run, and every caller of it — the culling,
        // and what goes into the acceleration structure — silently got the answer for a
        // solid sheet.
        if (MayCutOut(image.Format) && Expand(image) is { } expanded)
        {
            if (TextureKeying.HasHoles(expanded))
            {
                _keyed.Add(name);
            }

            if (MeasureCutouts &&
                !CutoutCards.Leaves.Contains(name) &&
                CutoutMask.Measure(expanded) is { } cutout)
            {
                _cutouts[name] = cutout;
            }
        }

        _textures[name] = _device.CreateTexture(image);
        DeviceBytes += image.Blocks.Length;
    }

    /// <summary>Whether a block format has an alpha channel a cutout could live in.</summary>
    private static bool MayCutOut(BlockFormat format) =>
        format is BlockFormat.Bc7Srgb or BlockFormat.Bc7Unorm;

    /// <summary>
    /// Expands the level of a packed texture that the silhouette was authored at.
    /// </summary>
    private static DecodedImage? Expand(CompressedImage image)
    {
        if (!BlockDecoder.CanDecode(image.Format) || image.Mips < 1)
        {
            return null;
        }

        int level = 0;

        while (level + 1 < image.Mips)
        {
            (_, _, int wide, int tall) = image.Level(level);

            if (Math.Max(wide, tall) <= CutoutMask.ReferenceTexels)
            {
                break;
            }

            level++;
        }

        (_, _, int width, int height) = image.Level(level);

        if (width < 4 || height < 4)
        {
            return null;
        }

        byte[] pixels = new byte[BlockDecoder.DecodedLength(width, height)];

        try
        {
            BlockDecoder.DecodeLevel(image, level, pixels);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        return new DecodedImage(width, height, pixels, HasAlpha: true, image.Name);
    }

    /// <summary>Uploads a block-compressed normal map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The compressed levels.</param>
    public void AddNormal(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_normals.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _normals[name] = _device.CreateTexture(image);
        DeviceBytes += image.Blocks.Length;
    }

    /// <summary>Roughly how many bytes of video memory the textures here occupy.</summary>
    public long DeviceBytes { get; private set; }

    /// <summary>Whether a surface's normal map is already here.</summary>
    /// <param name="name">The <em>colour</em> texture's name; a normal map is named for it.</param>
    /// <returns>True when there is nothing to read, decode or upload.</returns>
    public bool HasNormal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _normals.ContainsKey(name);
    }

    /// <summary>Uploads a normal map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The decoded map.</param>
    public void AddNormal(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_normals.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _normals[name] = _device.CreateTexture(image, GeometryTextureKind.Data);

        DeviceBytes += WithMips(image.Width, image.Height);
    }

    /// <summary>How much video memory an uncompressed texture and its chain take.</summary>
    private static long WithMips(int width, int height) =>
        (long)width * height * 4 * 4 / 3;

    /// <summary>Finds a surface's normal map, or a flat one.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>The map, or <see cref="Flat"/>.</returns>
    public IGeometryTexture GetNormal(string name) =>
        name.Length > 0 && _normals.TryGetValue(name, out IGeometryTexture? normal)
            ? normal
            : Flat;

    /// <summary>Whether a surface's ORM map is already here.</summary>
    /// <param name="name">The <em>colour</em> texture's name; an ORM map is named for it.</param>
    /// <returns>True when there is nothing to read, decode or upload.</returns>
    public bool HasOrm(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _orms.ContainsKey(name);
    }

    /// <summary>Uploads a block-compressed ORM map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The compressed levels.</param>
    public void AddOrm(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_orms.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _orms[name] = _device.CreateTexture(image);
        DeviceBytes += image.Blocks.Length;
    }

    /// <summary>Uploads an ORM map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The decoded map.</param>
    public void AddOrm(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_orms.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _orms[name] = _device.CreateTexture(image, GeometryTextureKind.Data);

        DeviceBytes += WithMips(image.Width, image.Height);
    }

    /// <summary>Finds a surface's ORM map, or a neutral one.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>The map, or <see cref="Neutral"/>.</returns>
    public IGeometryTexture GetOrm(string name) =>
        name.Length > 0 && _orms.TryGetValue(name, out IGeometryTexture? orm)
            ? orm
            : Neutral;

    /// <summary>Whether a surface's height map is already here.</summary>
    /// <param name="name">The <em>colour</em> texture's name.</param>
    /// <returns>True when there is nothing to read, decode or upload.</returns>
    public bool HasHeight(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _heights.ContainsKey(name);
    }

    /// <summary>Whether a surface's height map is here as numbers the CPU can read.</summary>
    /// <param name="name">The <em>colour</em> texture's name.</param>
    /// <returns>True when <see cref="FieldFor"/> will answer.</returns>
    public bool HasField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _fields.ContainsKey(name);
    }

    /// <summary>A surface's height map as numbers, if one was kept.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>The field, or null.</returns>
    public HeightField? FieldFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _fields.TryGetValue(name, out HeightField? field) ? field : null;
    }

    /// <summary>Uploads a block-compressed height map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The compressed levels.</param>
    /// <param name="keepField">Whether to keep a decoded copy for the CPU to read.</param>
    public void AddHeight(string name, CompressedImage image, bool keepField = false)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (keepField && !_fields.ContainsKey(name) && HeightField.From(image, FieldExtent) is { } field)
        {
            _fields[name] = field;
        }

        if (_heights.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _heights[name] = _device.CreateTexture(image);
        DeviceBytes += image.Blocks.Length;
    }

    /// <summary>Uploads a height map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The decoded map.</param>
    /// <param name="keepField">Whether to keep a copy for the CPU to read.</param>
    public void AddHeight(string name, DecodedImage image, bool keepField = false)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (keepField && !_fields.ContainsKey(name))
        {
            _fields[name] = HeightField.From(image, FieldExtent);
        }

        if (_heights.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _heights[name] = _device.CreateTexture(image, GeometryTextureKind.Data);

        DeviceBytes += WithMips(image.Width, image.Height);
    }

    /// <summary>Finds a surface's height map, or a level one.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>The map, or <see cref="Level"/>.</returns>
    public IGeometryTexture GetHeight(string name) =>
        name.Length > 0 && _heights.TryGetValue(name, out IGeometryTexture? height)
            ? height
            : Level;

    /// <summary>Finds a texture, or the fallback.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The texture.</returns>
    public IGeometryTexture Get(string name) =>
        name.Length > 0 && _textures.TryGetValue(name, out IGeometryTexture? texture)
            ? texture
            : Fallback;

    /// <inheritdoc/>
    public void Dispose()
    {
        _device.Wait();

        foreach (IGeometryTexture texture in _textures.Values)
        {
            texture.Dispose();
        }

        foreach (IGeometryTexture normal in _normals.Values)
        {
            normal.Dispose();
        }

        foreach (IGeometryTexture orm in _orms.Values)
        {
            orm.Dispose();
        }

        foreach (IGeometryTexture height in _heights.Values)
        {
            height.Dispose();
        }

        _textures.Clear();
        _normals.Clear();
        _orms.Clear();
        _heights.Clear();
        _keyed.Clear();

        Fallback.Dispose();
        White.Dispose();
        Flat.Dispose();
        Neutral.Dispose();
        Level.Dispose();
    }
}
