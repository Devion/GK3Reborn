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
    }

    /// <summary>Drawn wherever a texture is asked for and missing.</summary>
    public VulkanTexture Fallback { get; }

    /// <summary>A normal pointing straight out, bound wherever a surface has no map.</summary>
    /// <remarks>
    /// Which is how a partial set stays a perfectly good set: 250 of the game's 6,657
    /// textures have a normal map so far, and the other 6,407 look exactly as they did.
    /// </remarks>
    public VulkanTexture Flat { get; }

    /// <summary>How many normal maps the device is holding.</summary>
    public int NormalCount => _normals.Count;

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
    }

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
    }

    /// <summary>Finds a surface's normal map, or a flat one.</summary>
    /// <param name="name">The colour texture's name.</param>
    /// <returns>The map, or <see cref="Flat"/>.</returns>
    public VulkanTexture GetNormal(string name) =>
        name.Length > 0 && _normals.TryGetValue(name, out VulkanTexture? normal)
            ? normal
            : Flat;

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

        _textures.Clear();
        _normals.Clear();
        _keyed.Clear();

        Fallback.Dispose();
        Flat.Dispose();
    }
}
