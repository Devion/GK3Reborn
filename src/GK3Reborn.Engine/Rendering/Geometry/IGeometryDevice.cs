using System.Numerics;
using GK3Reborn.Formats.Bitmaps;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>What a buffer of geometry is for.</summary>
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
    Data,

    /// <summary>
    /// A packed sheet, read as colour and never given a mip chain.
    /// </summary>
    Atlas,
}

/// <summary>A buffer of geometry on a device.</summary>
public interface IGeometryBuffer : IDisposable
{
    /// <summary>How large it is.</summary>
    ulong Bytes { get; }

    /// <summary>Rewrites a buffer that was made to be rewritten.</summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="data">What to write.</param>
    /// <exception cref="InvalidOperationException">The buffer is not one of those.</exception>
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
    void Refresh(ReadOnlySpan<byte> pixels, int width, int height);
}

/// <summary>The textures one batch is drawn with, bound together.</summary>
public interface IGeometryMaterial
{
}

/// <summary>Many staging copies recorded once and submitted once.</summary>
public interface IGeometryUploads : IDisposable
{
    /// <summary>Submits every copy in the batch and waits for them.</summary>
    void Submit();
}

/// <summary>
/// Somewhere a scene's geometry and textures can be put, whichever API is underneath.
/// </summary>
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
    IGeometryBuffer CreateDynamicVertices(ulong bytes);

    /// <summary>Puts a picture on the device.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="kind">What it holds, which decides how it is read.</param>
    /// <param name="mipmaps">Whether to build a mip chain for it.</param>
    /// <param name="into">An open batch to record the copy into, or null to submit alone.</param>
    /// <returns>The texture, whose contents are there once the batch has been submitted.</returns>
    IGeometryTexture CreateTexture(
        DecodedImage image,
        GeometryTextureKind kind = GeometryTextureKind.Colour,
        bool mipmaps = true,
        IGeometryUploads? into = null);

    /// <summary>Puts an already-compressed picture on the device.</summary>
    /// <param name="image">The blocks, as the file holds them.</param>
    /// <param name="into">An open batch to record the copy into, or null to submit alone.</param>
    /// <returns>The texture, whose contents are there once the batch has been submitted.</returns>
    IGeometryTexture CreateTexture(CompressedImage image, IGeometryUploads? into = null);

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
    void Reserve(int materials);

    /// <summary>Frees every material the device has handed out.</summary>
    void ReleaseMaterials();

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
    void SetTraced(int part, bool traced);

    /// <summary>Says that a deforming piece has a new shape.</summary>
    /// <param name="key">Which animated batch.</param>
    /// <param name="positions">Its vertices now.</param>
    void Reshape(int key, ReadOnlySpan<Vector3> positions);

    /// <summary>Makes everything recorded since the last one true.</summary>
    void Settle();
}
