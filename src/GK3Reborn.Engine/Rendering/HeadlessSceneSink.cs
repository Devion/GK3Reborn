using System.Numerics;
using GK3Reborn.Formats.Bitmaps;
using GK3Reborn.Formats.Lightmaps;
using GK3Reborn.Formats.Models;
using GK3Reborn.Formats.Scenes;

namespace GK3Reborn.Rendering;

/// <summary>
/// Somewhere to put a scene when there is nothing to draw it with.
/// </summary>
/// <remarks>
/// Loading a scene and drawing it are separate jobs, and only the second needs a graphics
/// device. This takes everything the loader produces, measures it and throws it away, so
/// the whole of loading — the geometry, the bakes, every texture, the props and the people
/// — can be exercised on a build agent with no GPU. That is what makes "every scene loads
/// headlessly" a thing a command can answer rather than a thing somebody checks by
/// looking.
/// </remarks>
public sealed class HeadlessSceneSink : ISceneSink
{
    private readonly HashSet<string> _textures = new(StringComparer.OrdinalIgnoreCase);

    private Vector3 _minimum = new(float.MaxValue);
    private Vector3 _maximum = new(float.MinValue);

    /// <inheritdoc/>
    public Vector3 Minimum => _textures.Count == 0 && TriangleCount == 0 ? Vector3.Zero : _minimum;

    /// <inheritdoc/>
    public Vector3 Maximum => _textures.Count == 0 && TriangleCount == 0 ? Vector3.Zero : _maximum;

    /// <inheritdoc/>
    public int TextureCount => _textures.Count;

    /// <inheritdoc/>
    public int TriangleCount { get; private set; }

    /// <summary>How many models were placed in it.</summary>
    public int ModelCount { get; private set; }

    /// <summary>How many texels the textures cover, as a measure of what was decoded.</summary>
    public long TextureTexels { get; private set; }

    /// <inheritdoc/>
    public void AddTexture(string name, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_textures.Add(name))
        {
            TextureTexels += (long)image.Width * image.Height;
        }
    }

    /// <inheritdoc/>
    public void Add(ModFile model, Matrix4x4? transform = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        Matrix4x4 placement = transform ?? Matrix4x4.Identity;
        ModelCount++;

        foreach (ModMesh mesh in model.Meshes)
        {
            Matrix4x4 toWorld = mesh.MeshToLocal * placement;

            foreach (ModSubmesh submesh in mesh.Submeshes)
            {
                TriangleCount += submesh.Indices.Length / 3;

                foreach (Vector3 position in submesh.Positions)
                {
                    Grow(Vector3.Transform(position, toWorld));
                }
            }
        }
    }

    /// <inheritdoc/>
    public void AddScene(
        BspFile scene, MulFile? lightmaps = null, IReadOnlySet<string>? hiddenObjects = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        TriangleCount += scene.TriangleCount;

        foreach (Vector3 vertex in scene.Vertices)
        {
            Grow(vertex);
        }
    }

    private void Grow(Vector3 point)
    {
        _minimum = Vector3.Min(_minimum, point);
        _maximum = Vector3.Max(_maximum, point);
    }
}
