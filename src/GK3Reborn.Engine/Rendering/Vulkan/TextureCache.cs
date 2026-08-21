using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;
using Silk.NET.Vulkan;

namespace GK3Reborn.Rendering.Vulkan;

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
/// </remarks>
public sealed class TextureCache : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Dictionary<string, VulkanTexture> _textures =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _keyed = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, VulkanTexture> _normals =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, VulkanTexture> _orms =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, VulkanTexture> _heights =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a cache over a device.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="fallback">Drawn wherever a texture is asked for and missing.</param>
    public TextureCache(VulkanContext context, DecodedImage fallback)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        Fallback = VulkanTexture.Create(context, fallback);

        // A normal pointing straight out of the surface, for everything with no map. It is
        // (0.5, 0.5, 1) rather than white, because the shader decodes a normal from the
        // range 0..1 back to -1..1 and white would tilt every surface into a corner.
        Flat = VulkanTexture.Create(
            context,
            new DecodedImage(1, 1, [128, 128, 255, 255], HasAlpha: false, "flat-normal"),
            mipmaps: false,
            SamplerAddressMode.Repeat,
            linear: true);

        // Occlusion, roughness and metalness, in that order, for everything with no map:
        // unoccluded, fully rough, not a metal. Which is exactly the surface the renderer
        // drew before any of this existed, so a batch that binds this is unchanged by it.
        //
        // Linear, like the normal map and for the same reason. These three channels are
        // numbers rather than a colour, and an sRGB upload would bend every one of them.
        Neutral = VulkanTexture.Create(
            context,
            new DecodedImage(1, 1, [255, 255, 0, 255], HasAlpha: false, "neutral-orm"),
            mipmaps: false,
            SamplerAddressMode.Repeat,
            linear: true);

        // A height map at the middle of its range, which is the surface as modelled: half
        // is the plane the geometry is actually on, and displacement is measured either
        // side of it. It costs nothing on its own, because the shader's height scale is
        // zero for any surface with no map, so this is never sampled into an offset.
        Level = VulkanTexture.Create(
            context,
            new DecodedImage(1, 1, [128, 128, 128, 255], HasAlpha: false, "level-height"),
            mipmaps: false,
            SamplerAddressMode.Repeat,
            linear: true);
    }

    /// <summary>Drawn wherever a texture is asked for and missing.</summary>
    public VulkanTexture Fallback { get; }

    /// <summary>A normal pointing straight out, bound wherever a surface has no map.</summary>
    /// <remarks>
    /// Which is how a partial set stays a perfectly good set: 250 of the game's 6,657
    /// textures have a normal map so far, and the other 6,407 look exactly as they did.
    /// </remarks>
    public VulkanTexture Flat { get; }

    /// <summary>Neutral occlusion, roughness and metalness, bound where a surface has none.</summary>
    /// <remarks>
    /// Unoccluded, fully rough, not a metal — the material the renderer assumed everywhere
    /// before there were maps to say otherwise. A batch that binds this looks exactly as it
    /// did, which is what lets the specular lobe be switched on before the maps exist.
    /// </remarks>
    public VulkanTexture Neutral { get; }

    /// <summary>A height map at mid grey, bound where a surface has none.</summary>
    public VulkanTexture Level { get; }

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
        }

        _textures[name] = VulkanTexture.Create(_context, keyed);
        DeviceBytes += WithMips(keyed.Width, keyed.Height);
    }

    /// <summary>Uploads a block-compressed texture, or keeps the one already here.</summary>
    /// <param name="name">Its name, matched without regard to case.</param>
    /// <param name="image">The compressed levels.</param>
    /// <remarks>
    /// No keying. <see cref="TextureKeying"/> works on texels, and these are blocks; the
    /// loader is what decides that a texture needing a colour key takes the decoded path
    /// instead. Only three of the 324 textures in the pilot set do.
    /// </remarks>
    public void Add(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _textures[name] = VulkanTexture.Create(_context, image);
        DeviceBytes += image.Blocks.Length;
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

        _normals[name] = VulkanTexture.Create(_context, image);
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

        _normals[name] = VulkanTexture.Create(
            _context, image, mipmaps: true, SamplerAddressMode.Repeat, linear: true);

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
    public VulkanTexture GetNormal(string name) =>
        name.Length > 0 && _normals.TryGetValue(name, out VulkanTexture? normal)
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

        _orms[name] = VulkanTexture.Create(_context, image);
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

        _orms[name] = VulkanTexture.Create(
            _context, image, mipmaps: true, SamplerAddressMode.Repeat, linear: true);

        DeviceBytes += WithMips(image.Width, image.Height);
    }

    /// <summary>Finds a surface's ORM map, or a neutral one.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>The map, or <see cref="Neutral"/>.</returns>
    public VulkanTexture GetOrm(string name) =>
        name.Length > 0 && _orms.TryGetValue(name, out VulkanTexture? orm)
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

    /// <summary>Uploads a block-compressed height map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The compressed levels.</param>
    public void AddHeight(string name, CompressedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_heights.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _heights[name] = VulkanTexture.Create(_context, image);
        DeviceBytes += image.Blocks.Length;
    }

    /// <summary>Uploads a height map, or keeps the one already here.</summary>
    /// <param name="name">The colour texture it belongs to.</param>
    /// <param name="image">The decoded map.</param>
    /// <remarks>
    /// Linear, like the other two. A height field is a distance.
    /// </remarks>
    public void AddHeight(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_heights.ContainsKey(name))
        {
            Reused++;
            return;
        }

        _heights[name] = VulkanTexture.Create(
            _context, image, mipmaps: true, SamplerAddressMode.Repeat, linear: true);

        DeviceBytes += WithMips(image.Width, image.Height);
    }

    /// <summary>Finds a surface's height map, or a level one.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>The map, or <see cref="Level"/>.</returns>
    public VulkanTexture GetHeight(string name) =>
        name.Length > 0 && _heights.TryGetValue(name, out VulkanTexture? height)
            ? height
            : Level;

    /// <summary>Finds a texture, or the fallback.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The texture.</returns>
    public VulkanTexture Get(string name) =>
        name.Length > 0 && _textures.TryGetValue(name, out VulkanTexture? texture)
            ? texture
            : Fallback;

    /// <inheritdoc/>
    public void Dispose()
    {
        _context.Api.DeviceWaitIdle(_context.Device);

        foreach (VulkanTexture texture in _textures.Values)
        {
            texture.Dispose();
        }

        foreach (VulkanTexture normal in _normals.Values)
        {
            normal.Dispose();
        }

        foreach (VulkanTexture orm in _orms.Values)
        {
            orm.Dispose();
        }

        foreach (VulkanTexture height in _heights.Values)
        {
            height.Dispose();
        }

        _textures.Clear();
        _normals.Clear();
        _orms.Clear();
        _heights.Clear();
        _keyed.Clear();

        Fallback.Dispose();
        Flat.Dispose();
        Neutral.Dispose();
        Level.Dispose();
    }
}
