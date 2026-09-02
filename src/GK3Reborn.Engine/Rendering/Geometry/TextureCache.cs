using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>
/// The textures the device holds, kept across rooms.
/// </summary>
/// <remarks>
/// <para>
/// A room's geometry used to own its textures, so walking through a door threw away 120 of
/// them and uploaded the next room's from scratch. Most of what it threw away it wanted
/// back: the characters are in every room they appear in, and props and fittings repeat all
/// over the hotel.
/// </para>
/// <para>
/// Measured on R25: of a ~350 ms room load, about 200 ms was uploading textures the device
/// had already been given at some point. Reading and decoding them again cost another 68 ms
/// on top.
/// </para>
/// <para>
/// So they live here, with the renderer, and outlast any one room. Nothing is evicted: the
/// game's textures are small — mostly 256 squared — and a session touches a few hundred of
/// the 6,657, which is tens of megabytes. If that ever stops being true this is where a
/// bound goes, and it will need to know which textures a frame in flight is still reading.
/// </para>
/// <para>
/// None of the above is about a graphics API, which is why this is here rather than in a
/// backend. What a texture <em>is</em> differs between the two; which textures a session has
/// already paid for, which ones carry a colour key, and which height maps are kept as
/// numbers as well as as pictures, do not. The only thing it asks a device for is to turn a
/// picture into a texture.
/// </para>
/// </remarks>
public sealed class TextureCache : IDisposable
{
    private readonly IGeometryDevice _device;
    private readonly Dictionary<string, IGeometryTexture> _textures =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _keyed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the holes in a keyed texture say about the shape drawn on it.</summary>
    /// <remarks>
    /// Kept only for the textures that measure as a lattice of bars — some sixty of the
    /// eight hundred keyed ones in the game, and a handful in any room — because the mask is
    /// the only thing that knows where a railing's silhouette is once the picture is on the
    /// device. A 128-square mask is sixteen kilobytes; the ones that are nobody's railing
    /// are never built. See <see cref="CutoutMask"/>.
    /// </remarks>
    private readonly Dictionary<string, CutoutMask> _cutouts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IGeometryTexture> _normals =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IGeometryTexture> _orms =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IGeometryTexture> _heights =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Height maps kept as numbers as well as as pictures.</summary>
    /// <remarks>
    /// Only for the surfaces something intends to displace, which is a room's floor and
    /// nothing else. A field is a quarter of a megabyte and the game has 2,905 height maps;
    /// keeping one for every map a session ever loads would cost most of a gigabyte to
    /// answer a question about a hundred and twenty-six of them. See
    /// <see cref="Rendering.ReliefPlan"/>.
    /// </remarks>
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
    /// <remarks>
    /// Which is how a partial set stays a perfectly good set: 250 of the game's 6,657
    /// textures have a normal map so far, and the other 6,407 look exactly as they did.
    /// </remarks>
    public IGeometryTexture Flat { get; }

    /// <summary>Neutral occlusion, roughness and metalness, bound where a surface has none.</summary>
    /// <remarks>
    /// Unoccluded, fully rough, not a metal — the material the renderer assumed everywhere
    /// before there were maps to say otherwise. A batch that binds this looks exactly as it
    /// did, which is what lets the specular lobe be switched on before the maps exist.
    /// </remarks>
    public IGeometryTexture Neutral { get; }

    /// <summary>A height map at mid grey, bound where a surface has none.</summary>
    public IGeometryTexture Level { get; }

    /// <summary>How large a height field is kept for the CPU, in texels.</summary>
    /// <remarks>
    /// Displacement samples at whatever spacing its triangle budget affords, which on a
    /// village street is about seven units — a thirtieth of the 232 units the road texture
    /// tiles over, so 256 texels leaves eight to a cell to average over. Finer would be
    /// carrying detail no vertex can express.
    /// </remarks>
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
    /// <remarks>
    /// Remembered so the geometry using one can be kept out of the acceleration structure:
    /// without an any-hit shader a keyed surface casts a solid shadow from the parts of it
    /// that are holes.
    /// </remarks>
    public IReadOnlySet<string> Keyed => _keyed;

    /// <summary>
    /// Whether keyed textures are measured for the lattice of bars that may be drawn on
    /// them.
    /// </summary>
    /// <remarks>
    /// Set before any texture is added, from the setting that gates the whole treatment.
    /// Off, nothing is measured and nothing is kept, and a room is built exactly as it was
    /// before any of this existed.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// No keying. <see cref="TextureKeying"/> works on texels, and these are blocks; the
    /// loader is what decides that a texture needing a colour key takes the decoded path
    /// instead. Only three of the 324 textures in the pilot set do.
    /// </para>
    /// <para>
    /// <b>A packed texture may still carry a cutout, and it arrives here rather than
    /// above.</b> The packer leaves out only the keyed textures whose enhanced replacement
    /// did <em>not</em> carry the key across as alpha; the ones that did are packed, as BC7
    /// with a real alpha channel, so a railing installed with the content packs comes down
    /// this path and not the decoded one. Measuring only there is why this pass did nothing
    /// at all in a shipped build while every render made without the packs showed it
    /// working — the exact shape of failure the rest of this codebase keeps a note about.
    /// So the largest level is expanded, once, to be measured. See <see cref="CutoutMask"/>.
    /// </para>
    /// </remarks>
    public void Add(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.ContainsKey(name))
        {
            Reused++;
            return;
        }

        if (MeasureCutouts && MayCutOut(image.Format) && !CutoutCards.Leaves.Contains(name))
        {
            Measure(name, image);
        }

        _textures[name] = _device.CreateTexture(image);
        DeviceBytes += image.Blocks.Length;
    }

    /// <summary>Whether a block format has an alpha channel a cutout could live in.</summary>
    /// <remarks>
    /// BC5 is two channels and BC4 one, and neither is ever a base colour: they are the
    /// normal and height maps, which never come here.
    /// </remarks>
    private static bool MayCutOut(BlockFormat format) =>
        format is BlockFormat.Bc7Srgb or BlockFormat.Bc7Unorm;

    /// <summary>
    /// Expands one level of a packed texture and measures the holes in it.
    /// </summary>
    /// <remarks>
    /// <b>Not the largest level.</b> A shipped base colour is up to 2,048 square, and
    /// expanding that costs some forty milliseconds and sixteen megabytes to answer a
    /// question about a silhouette that was drawn at 128 — every texel above
    /// <see cref="CutoutMask.ReferenceTexels"/> was invented by an upscaler. Measuring the
    /// smallest level that still carries the outline costs about a millisecond and gives
    /// the same answer; taking level zero instead put a second and a quarter on a room's
    /// load, which is what found this.
    /// </remarks>
    private void Measure(string name, CompressedImage image)
    {
        if (!BlockDecoder.CanDecode(image.Format) || image.Mips < 1)
        {
            return;
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
            return;
        }

        byte[] pixels = new byte[BlockDecoder.DecodedLength(width, height)];

        try
        {
            BlockDecoder.DecodeLevel(image, level, pixels);
        }
        catch (NotSupportedException)
        {
            return;
        }

        if (CutoutMask.Measure(
                new DecodedImage(width, height, pixels, HasAlpha: true, name)) is { } cutout)
        {
            _cutouts[name] = cutout;
        }
    }

    /// <summary>Uploads a block-compressed normal map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The compressed levels.</param>
    /// <remarks>
    /// BC5 is linear by construction — it has no sRGB spelling — so nothing has to be said
    /// here to keep a direction from being treated as a colour.
    /// </remarks>
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
    /// <remarks>
    /// Uploaded <b>linear</b>, because its channels are a direction rather than a colour.
    /// </remarks>
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
    /// <remarks>
    /// Four bytes a texel and a third again for the chain, which is what the sum of a
    /// halving series comes to. Close enough to compare a texture set against itself, which
    /// is the only thing anybody asks this.
    /// </remarks>
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
    /// <remarks>
    /// Three channels, so BC7 rather than the BC5 a normal map takes — and BC7 has an sRGB
    /// spelling, which this must not be given. The format travels with the file.
    /// </remarks>
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
    /// <remarks>
    /// Uploaded <b>linear</b>. Occlusion, roughness and metalness are measurements, and
    /// putting them through the sRGB path pulls every one of them towards one end of its
    /// range — which reads as a generator that produced bad numbers rather than as a
    /// renderer that misread good ones.
    /// </remarks>
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
    /// <remarks>
    /// Apart from <see cref="HasHeight"/> on purpose. A room that displaces its floor wants
    /// a map the room before it uploaded and did not keep, and the only way to get one is
    /// to read the file again — so the loader has to be able to tell the two states apart.
    /// </remarks>
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
    /// <remarks>
    /// Linear, like the other two. A height field is a distance.
    /// </remarks>
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
