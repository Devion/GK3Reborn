using System.Numerics;
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

    /// <summary>Replaces the pixels of a texture without replacing the texture.</summary>
    /// <param name="pixels">The new picture, four bytes a pixel.</param>
    /// <param name="width">Its width, which must be the one it was made at.</param>
    /// <param name="height">Its height, which must be the one it was made at.</param>
    /// <exception cref="InvalidOperationException">This texture cannot be refreshed.</exception>
    /// <remarks>
    /// One caller: the lightmap, when the time of day changes. It matters that the texture
    /// survives rather than being replaced, because every material in the room already
    /// points at it — a new texture would mean rebuilding several hundred materials to
    /// change the light on geometry that has not moved.
    /// </remarks>
    void Refresh(ReadOnlySpan<byte> pixels, int width, int height);
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

    /// <summary>Puts a picture on the device.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="kind">What it holds, which decides how it is read.</param>
    /// <param name="mipmaps">Whether to build a mip chain for it.</param>
    /// <returns>The texture.</returns>
    /// <remarks>
    /// The whole of what a device is asked to do about textures. Which ones a session has
    /// already paid for, which carry a colour key, and which height maps are kept as numbers
    /// as well as as pictures are all <see cref="TextureCache"/>'s business, and none of it
    /// is about a graphics API.
    /// </remarks>
    IGeometryTexture CreateTexture(
        DecodedImage image,
        GeometryTextureKind kind = GeometryTextureKind.Colour,
        bool mipmaps = true);

    /// <summary>Puts an already-compressed picture on the device.</summary>
    /// <param name="image">The blocks, as the file holds them.</param>
    /// <returns>The texture.</returns>
    /// <remarks>
    /// No kind and no mip choice: a block format says whether it carries an sRGB encode, and
    /// the chain is the one the compressor already built.
    /// </remarks>
    IGeometryTexture CreateTexture(CompressedImage image);

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

    /// <summary>Says how many materials a room is about to ask for.</summary>
    /// <param name="materials">How many.</param>
    /// <remarks>
    /// A hint with teeth on one backend and none on the other, which is why it is here
    /// rather than in either. Vulkan hands descriptor sets out of a pool that has to be
    /// sized in advance, and a pool that runs out mid-room spills every set after it into
    /// an overflow that should not have been needed. Direct3D has a heap already and does
    /// nothing with this.
    /// </remarks>
    void Reserve(int materials);

    /// <summary>Builds an acceleration structure over some geometry.</summary>
    /// <param name="meshes">What the rays can hit.</param>
    /// <returns>The structure, or null where the device cannot trace or there is nothing to.</returns>
    IGeometryAccelerationStructure? BuildAccelerationStructure(IReadOnlyList<TraceableMesh> meshes);

    /// <summary>Waits until the device has finished everything it was given.</summary>
    void Wait();
}

/// <summary>One piece of geometry the rays can hit.</summary>
/// <param name="Positions">Its vertices, in the model's own space.</param>
/// <param name="Indices">Its triangles.</param>
/// <param name="Part">Which placement it belongs to; zero is the room itself.</param>
/// <param name="Key">
/// Which animated batch reshapes it, or -1 for geometry that never deforms.
/// </param>
public readonly record struct TraceableMesh(
    Vector3[] Positions,
    uint[] Indices,
    int Part = 0,
    int Key = -1);

/// <summary>
/// The acceleration structure a scene is traced against.
/// </summary>
/// <remarks>
/// <para>
/// Two levels on both backends, and the division is the same on both: a bottom-level
/// structure per piece of geometry and one top level holding an instance of each with its
/// transform. The bottom level is the expensive part and does not change when something
/// moves, so a walking character is a rewritten transform rather than ten thousand rewritten
/// vertices.
/// </para>
/// <para>
/// Everything below is recorded rather than done. <see cref="Settle"/> is what makes it
/// true, and it has to be called after the fence and before the frame — rebuilding a
/// structure the device is still tracing against is the same hazard as rewriting a vertex
/// buffer it is still reading.
/// </para>
/// </remarks>
public interface IGeometryAccelerationStructure : IDisposable
{
    /// <summary>Triangles in the structure.</summary>
    int TriangleCount { get; }

    /// <summary>Pieces it was built from.</summary>
    int PartCount { get; }

    /// <summary>Says where a piece now stands.</summary>
    /// <param name="part">Which piece.</param>
    /// <param name="transform">Where it stands.</param>
    void Move(int part, Matrix4x4 transform);

    /// <summary>Says whether a piece is in the picture at all.</summary>
    /// <param name="part">Which piece.</param>
    /// <param name="traced">Whether rays should see it.</param>
    /// <remarks>
    /// A hidden model that still casts a shadow is worse than one that is simply drawn.
    /// </remarks>
    void SetTraced(int part, bool traced);

    /// <summary>Says that a deforming piece has a new shape.</summary>
    /// <param name="key">Which animated batch.</param>
    /// <param name="positions">Its vertices now.</param>
    void Reshape(int key, ReadOnlySpan<Vector3> positions);

    /// <summary>Makes everything recorded since the last one true.</summary>
    void Settle();
}
