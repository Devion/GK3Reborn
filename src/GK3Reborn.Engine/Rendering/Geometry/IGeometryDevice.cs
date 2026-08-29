using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>What a buffer of geometry is for.</summary>
/// <remarks>
/// Vulkan needs this said when a buffer is made; Direct3D does not care until the buffer is
/// bound. It is stated either way, because the backend that needs it cannot infer it and
/// the one that does not can ignore it.
/// </remarks>
public enum GeometryBufferKind
{
    /// <summary>Vertices.</summary>
    Vertices,

    /// <summary>Indices, thirty-two bits each.</summary>
    Indices,

    /// <summary>Indices, sixteen bits each.</summary>
    ShortIndices,
}

/// <summary>What a texture holds, which decides how it is read.</summary>
public enum GeometryTextureKind
{
    /// <summary>Colour, which the hardware converts from sRGB on read.</summary>
    Colour,

    /// <summary>
    /// A direction or a measurement, read exactly as it was written.
    /// </summary>
    /// <remarks>
    /// A normal map is not a colour. Its channels are a direction, and putting one through
    /// the sRGB path bends every normal towards flat — which reads as a weak, waxy surface
    /// rather than as the colour-space mistake it is.
    /// </remarks>
    Data,

    /// <summary>
    /// A packed sheet, read as colour and never given a mip chain.
    /// </summary>
    /// <remarks>
    /// Each coarser level would average texels across tile boundaries, so by the third
    /// level a tile is visibly contaminated by its neighbours.
    /// </remarks>
    Atlas,
}

/// <summary>A buffer of geometry on a device.</summary>
/// <remarks>
/// Deliberately almost empty. What a scene does with a buffer is make it, sometimes rewrite
/// it, and hand it back to be drawn; everything about how it is bound belongs to whatever
/// is doing the binding.
/// </remarks>
public interface IGeometryBuffer : IDisposable
{
    /// <summary>How large it is.</summary>
    ulong Bytes { get; }

    /// <summary>Rewrites a buffer that was made to be rewritten.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="data">What to write.</param>
    /// <exception cref="InvalidOperationException">The buffer is not one of those.</exception>
    /// <remarks>
    /// Only the per-frame buffers an animated batch owns. Everything else is written once
    /// when it is made and is in memory the host cannot reach.
    /// </remarks>
    void Write<T>(ReadOnlySpan<T> data)
        where T : unmanaged;
}

/// <summary>A texture on a device, as a scene refers to one.</summary>
public interface IGeometryTexture : IDisposable
{
    /// <summary>How much device memory it takes.</summary>
    long Bytes { get; }
}

/// <summary>The textures one batch is drawn with, bound together.</summary>
/// <remarks>
/// A Vulkan descriptor set or a Direct3D descriptor table. Opaque on purpose: what a
/// material <em>is</em> depends entirely on the backend, and the scene only ever needs to
/// say "draw this batch with that one".
/// </remarks>
public interface IGeometryMaterial
{
}

/// <summary>Many staging copies recorded once and submitted once.</summary>
/// <remarks>
/// <b>Submitting each copy on its own waits for the whole queue, and a room is hundreds of
/// buffers.</b> RC4 is 358 batches with a vertex buffer and an index buffer apiece, so
/// unbatched that is seven hundred stalls and about 300 ms of a door — measured on the
/// Vulkan backend, and true of both.
/// </remarks>
public interface IGeometryUploads : IDisposable
{
    /// <summary>Submits every copy in the batch and waits for them.</summary>
    void Submit();
}

/// <summary>
/// Somewhere a scene's geometry and textures can be put, whichever API is underneath.
/// </summary>
/// <remarks>
/// <para>
/// The seam that lets one <c>SceneGeometry</c> serve both backends. Assembling a scene is
/// two and a half thousand lines of reading models, cutting relief into floors, rounding
/// objects, packing lightmaps, thinning foliage and folding transforms — none of which is
/// about a graphics API — and about sixty lines that make buffers, bind textures and record
/// draws. This is those sixty lines.
/// </para>
/// <para>
/// It is narrow because it was drawn around what the scene actually asks for rather than
/// around what a graphics API offers. There is no pipeline here, no command buffer and no
/// descriptor: a scene makes buffers, names textures, asks for a material, and hands the
/// result back. Anything wider would be a second graphics API to keep in step with two
/// real ones.
/// </para>
/// </remarks>
public interface IGeometryDevice : IDisposable
{
    /// <summary>Whether acceleration structures and inline ray queries are available.</summary>
    bool SupportsRayTracing { get; }

    /// <summary>Whether block-compressed textures can be uploaded as they are.</summary>
    bool BlockCompression { get; }

    /// <summary>How many distinct textures are resident.</summary>
    int TextureCount { get; }

    /// <summary>How many times a request found a texture already resident.</summary>
    int TexturesReused { get; }

    /// <summary>How much device memory those textures occupy.</summary>
    long TextureBytes { get; }

    /// <summary>Opens a batch of uploads.</summary>
    /// <returns>The batch.</returns>
    IGeometryUploads BeginUploads();

    /// <summary>Makes a buffer holding a copy of some data.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="data">What to put in it.</param>
    /// <param name="kind">What it is for.</param>
    /// <param name="into">An open batch to record the copy into, or null to submit alone.</param>
    /// <returns>The buffer, whose contents are there once the batch has been submitted.</returns>
    IGeometryBuffer CreateBuffer<T>(
        ReadOnlySpan<T> data, GeometryBufferKind kind, IGeometryUploads? into = null)
        where T : unmanaged;

    /// <summary>Makes a buffer of vertices the host can rewrite every frame.</summary>
    /// <param name="bytes">How large.</param>
    /// <returns>The buffer.</returns>
    /// <remarks>
    /// What an animated batch owns one of per frame in flight. Writing one buffer while the
    /// device reads it for an earlier frame gives a character built from two poses at once.
    /// </remarks>
    IGeometryBuffer CreateDynamicVertices(ulong bytes);

    /// <summary>Whether a texture is already resident under that name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True if it is.</returns>
    bool HasTexture(string name);

    /// <summary>Puts a texture on the device under a name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="image">The picture.</param>
    /// <param name="kind">What it holds.</param>
    void AddTexture(string name, DecodedImage image, GeometryTextureKind kind = GeometryTextureKind.Colour);

    /// <summary>Puts an already-compressed texture on the device under a name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="image">The blocks.</param>
    /// <param name="kind">What it holds.</param>
    void AddTexture(string name, CompressedImage image, GeometryTextureKind kind = GeometryTextureKind.Colour);

    /// <summary>Finds a texture by the name it was given.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The texture, or the fallback where there is no such thing.</returns>
    IGeometryTexture Texture(string name);

    /// <summary>The white texture that stands in for a map a surface does not have.</summary>
    IGeometryTexture White { get; }

    /// <summary>The flat normal map, which says every surface faces the way it already does.</summary>
    IGeometryTexture Flat { get; }

    /// <summary>The neutral occlusion, roughness and metalness map.</summary>
    IGeometryTexture Neutral { get; }

    /// <summary>The level height map, which displaces nothing.</summary>
    IGeometryTexture Level { get; }

    /// <summary>Binds five textures together as one material.</summary>
    /// <param name="diffuse">The base colour.</param>
    /// <param name="lightmap">The baked light, or white where there is none.</param>
    /// <param name="normal">The normal map, or flat.</param>
    /// <param name="orm">Occlusion, roughness and metalness, or neutral.</param>
    /// <param name="height">The height map, or level.</param>
    /// <returns>The material.</returns>
    IGeometryMaterial CreateMaterial(
        IGeometryTexture diffuse,
        IGeometryTexture lightmap,
        IGeometryTexture normal,
        IGeometryTexture orm,
        IGeometryTexture height);

    /// <summary>Waits until the device has finished everything it was given.</summary>
    void Wait();
}
