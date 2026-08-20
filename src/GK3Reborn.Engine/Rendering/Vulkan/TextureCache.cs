using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Rendering;

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

    /// <summary>Creates a cache over a device.</summary>
    /// <param name="context">Device context.</param>
    /// <param name="fallback">Drawn wherever a texture is asked for and missing.</param>
    public TextureCache(VulkanContext context, DecodedImage fallback)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        Fallback = VulkanTexture.Create(context, fallback);
    }

    /// <summary>Drawn wherever a texture is asked for and missing.</summary>
    public VulkanTexture Fallback { get; }

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

        _textures.Clear();
        _keyed.Clear();
        Fallback.Dispose();
    }
}
