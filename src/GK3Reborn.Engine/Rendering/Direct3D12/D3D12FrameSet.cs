using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Rendering.Geometry;
using Silk.NET.Direct3D12;

namespace GK3Reborn.Rendering.Direct3D12;

/// <summary>
/// What a frame is, bound once for all of it.
/// </summary>
/// <remarks>
/// <para>
/// The Direct3D counterpart of <c>FrameUniformSet</c>, and it holds the same four things
/// because the shader reads the same four: the camera and the frame's own numbers in a
/// constant buffer, the light rig, the grid that says which lights reach which cell, and the
/// list of which lights those are. With ray tracing compiled in there is a fifth, the
/// acceleration structure.
/// </para>
/// <para>
/// The rig and the grid are storage buffers rather than constant ones for a reason worth
/// keeping: a constant buffer has to be sized when the shader is compiled and only sixteen
/// kilobytes of it are guaranteed, which is what once put a limit of sixty-four lights on a
/// scene. A raw buffer is unsized on both sides and the loop is bounded by the cell rather
/// than by the array.
/// </para>
/// <para>
/// One set per frame in flight. Everything here is written by the host each frame, so a
/// single set would be rewritten while the device was still reading it for the frame before
/// — a room lit by two frames at once, which reads as a flicker rather than as a bug.
/// </para>
/// <para>
/// <b>The descriptors live in the geometry device's heap rather than one of their own.</b> A
/// command list may bind one shader-visible heap of each kind at a time, so a frame table and
/// a material table that came from different heaps could not both be bound — the second is
/// refused. This was written with its own heap first; the picture came out black and the
/// debug layer said exactly why in one line.
/// </para>
/// </remarks>
public sealed unsafe class D3D12FrameSet : IDisposable
{
    /// <summary>How many descriptors one frame's set takes.</summary>
    /// <remarks>
    /// The constant buffer, the rig, the cells, the lights that reach them, and the
    /// acceleration structure. Five whether or not the last is filled: a table is a run of
    /// slots, and leaving a hole in it would mean two table shapes to bind.
    /// </remarks>
    private const uint DescriptorsPerFrame = 5;

    private readonly D3D12Context _context;
    private readonly D3D12GeometryDevice _geometry;
    private readonly uint _first;
    private readonly D3D12Buffer[] _uniforms;
    private readonly D3D12Buffer[] _rig;
    private readonly D3D12Buffer[] _cells;
    private readonly D3D12Buffer[] _reaching;
    private readonly bool _rayTracing;
    private IGeometryAccelerationStructure? _scene;
    private bool _disposed;

    private D3D12FrameSet(
        D3D12Context context,
        D3D12GeometryDevice geometry,
        uint first,
        D3D12Buffer[] uniforms,
        D3D12Buffer[] rig,
        D3D12Buffer[] cells,
        D3D12Buffer[] reaching,
        bool rayTracing)
    {
        _context = context;
        _geometry = geometry;
        _first = first;
        _uniforms = uniforms;
        _rig = rig;
        _cells = cells;
        _reaching = reaching;
        _rayTracing = rayTracing;
    }

    /// <summary>How the room's lights are divided up, once it has been given some.</summary>
    public SceneLightGrid? Grid { get; private set; }

    /// <summary>How much tracing to do, and how.</summary>
    public RayTracingSettings Settings { get; set; } = RayTracingSettings.For(RayTracingQuality.None);

    /// <summary>This frame's jitter, in pixels.</summary>
    public Vector2 JitterPixels { get; set; }

    /// <summary>How much brighter a surface that carries its own light is drawn.</summary>
    public float EmissiveGain { get; set; } = 1f;

    /// <summary>The wind's clock.</summary>
    public float Seconds { get; set; }

    /// <summary>The wind's clock as it stood a frame ago.</summary>
    public float PreviousSeconds { get; private set; }

    /// <summary>How many frames of sets there are.</summary>
    public int Count => _uniforms.Length;

    /// <summary>Creates the sets.</summary>
    /// <param name="context">The device.</param>
    /// <param name="geometry">Whose heap the descriptors are taken from.</param>
    /// <param name="frames">How many frames are kept in flight.</param>
    /// <param name="rayTracing">Whether the acceleration structure slot is filled.</param>
    /// <returns>The sets.</returns>
    /// <exception cref="D3D12Exception">Something on the device refused.</exception>
    public static D3D12FrameSet Create(
        D3D12Context context, D3D12GeometryDevice geometry, int frames, bool rayTracing)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);

        {
            uint first = geometry.AllocateViews((uint)frames * DescriptorsPerFrame);

            var uniforms = new D3D12Buffer[frames];
            var rig = new D3D12Buffer[frames];
            var cells = new D3D12Buffer[frames];
            var reaching = new D3D12Buffer[frames];

            for (int i = 0; i < frames; i++)
            {
                uniforms[i] = D3D12Buffer.CreateHostVisible(
                    context, (ulong)Marshal.SizeOf<FrameUniforms>(), forConstants: true);

                // Room for the whole rig whether or not a room uses it. Sixteen bytes for
                // the count, padded so the array behind it starts on the boundary the
                // standard layout wants.
                rig[i] = D3D12Buffer.CreateHostVisible(
                    context, (ulong)(16 + (GpuLight.Capacity * Marshal.SizeOf<GpuLight>())));

                cells[i] = D3D12Buffer.CreateHostVisible(
                    context, (ulong)((SceneLightGrid.MostCells + 1) * sizeof(int)));
                reaching[i] = D3D12Buffer.CreateHostVisible(
                    context, (ulong)(SceneLightGrid.MostIndices * sizeof(int)));

                uint at = first + ((uint)i * DescriptorsPerFrame);
                uniforms[i].DescribeConstants(context, geometry.ViewCpu(at));
                rig[i].DescribeRead(context, geometry.ViewCpu(at + 1));
                cells[i].DescribeRead(context, geometry.ViewCpu(at + 2));
                reaching[i].DescribeRead(context, geometry.ViewCpu(at + 3));
            }

            return new D3D12FrameSet(
                context, geometry, first, uniforms, rig, cells, reaching, rayTracing);
        }
    }

    /// <summary>Where one frame's descriptors start, for a draw to bind.</summary>
    /// <param name="frame">Which frame in flight.</param>
    /// <returns>The handle.</returns>
    public GpuDescriptorHandle Table(int frame) =>
        _geometry.ViewGpu(_first + ((uint)(frame % Count) * DescriptorsPerFrame));

    /// <summary>Points the ray-tracing paths at the scene they trace against.</summary>
    /// <param name="scene">The acceleration structure.</param>
    /// <remarks>
    /// Written into every frame's set rather than one, because a set is bound by whichever
    /// frame is being recorded and the scene does not change between them.
    /// </remarks>
    public void SetScene(IGeometryAccelerationStructure scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_rayTracing || scene is not D3D12GeometryStructure direct)
        {
            return;
        }

        _scene = scene;

        for (int i = 0; i < Count; i++)
        {
            direct.Structure.Describe(
                _context, _geometry.ViewCpu(_first + ((uint)i * DescriptorsPerFrame) + 4));
        }
    }

    /// <summary>Sets the lights anything without baked lighting is lit by.</summary>
    /// <param name="lights">The rig the scene was authored with.</param>
    /// <param name="scene">What the geometry occupies.</param>
    /// <param name="sunGain">How much brighter a distant key burns than it was authored.</param>
    public void SetLights(
        IReadOnlyList<AuthoredLight> lights, SceneExtent scene = default, float sunGain = 1f)
    {
        ArgumentNullException.ThrowIfNull(lights);
        ObjectDisposedException.ThrowIf(_disposed, this);

        IReadOnlyList<AuthoredLight> chosen = GpuLight.Choose(lights, scene);

        int stride = Marshal.SizeOf<GpuLight>();
        byte[] bytes = new byte[16 + (GpuLight.Capacity * stride)];

        // The count leads, padded to a float4 so the array that follows starts on the
        // sixteen-byte boundary the standard layout requires.
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (float)chosen.Count);

        var packed = new GpuLight[chosen.Count];

        for (int i = 0; i < chosen.Count; i++)
        {
            packed[i] = GpuLight.From(chosen[i], scene, sunGain);
            MemoryMarshal.Write(bytes.AsSpan(16 + (i * stride), stride), in packed[i]);
        }

        Vector3 minimum = scene.Minimum;
        Vector3 maximum = scene.Maximum;

        if (!(maximum.X > minimum.X) || !(maximum.Y > minimum.Y) || !(maximum.Z > minimum.Z))
        {
            minimum = new Vector3(-1e5f);
            maximum = new Vector3(1e5f);
        }

        SceneLightGrid grid = SceneLightGrid.Build(GpuLight.Describe(packed), minimum, maximum);
        Grid = grid;

        GridOrigin = new Vector4(grid.Origin, grid.Cell);
        GridCounts = new Vector4(grid.Counts.X, grid.Counts.Y, grid.Counts.Z, packed.Length);

        for (int i = 0; i < Count; i++)
        {
            _rig[i].Write<byte>(bytes);
            _cells[i].Write<int>(grid.Offsets);

            // An empty rig gives an empty index list, and writing a zero-length span through
            // a mapped pointer is not the same as writing nothing.
            if (grid.Indices.Length > 0)
            {
                _reaching[i].Write<int>(grid.Indices);
            }
        }
    }

    /// <summary>The corner the light grid starts at, and how wide one of its cells is.</summary>
    public Vector4 GridOrigin { get; private set; }

    /// <summary>How many cells the grid has along each axis, and how many lights in all.</summary>
    public Vector4 GridCounts { get; private set; }

    /// <summary>Writes this frame's numbers, ready to be bound.</summary>
    /// <param name="frame">Which frame in flight.</param>
    /// <param name="camera">Where the room is seen from.</param>
    /// <param name="aspect">Width divided by height.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Viewport height in pixels.</param>
    /// <remarks>
    /// The same numbers the Vulkan path writes, in the same order, because it is the same
    /// shader reading them. The projection carries a Y flip for Vulkan's clip space and the
    /// translation to HLSL takes it back out, so what goes in here is the matrix the Vulkan
    /// path would use rather than one built for Direct3D. See <c>HlslTranspiler</c>.
    /// </remarks>
    public void Write(int frame, Camera camera, float aspect, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ObjectDisposedException.ThrowIf(_disposed, this);

        RayTracingSettings settings = _rayTracing
            ? Settings
            : RayTracingSettings.For(RayTracingQuality.None);

        Matrix4x4 viewProjection = camera.View * camera.Projection(aspect);

        // Kept without the jitter, because this is what the motion vectors are measured
        // against and a jitter is not movement. See Camera.ProjectionWithoutJitter.
        Matrix4x4 steady = camera.View * camera.ProjectionWithoutJitter(aspect);

        // The first frame has no previous one, and a motion vector against an identity
        // matrix is the whole screen moving at once. Its own is the honest answer: nothing
        // moved, because there was nothing to move from.
        Matrix4x4 previous = _previousViewProjection ?? steady;
        _previousViewProjection = steady;

        PreviousSeconds = _wasAt ?? Seconds;
        _wasAt = Seconds;

        var uniforms = new FrameUniforms(
            viewProjection,
            previous,
            new Vector4(Vector3.Normalize(camera.LightDirection), 0),
            new Vector4(camera.Position, 1),
            new Vector4(
                settings.ShadowLights,
                settings.AmbientOcclusionRays,
                settings.ShadowSamples,
                settings.LightmapIndirect),

            // The viewport in pixels, so the motion vectors come out in pixels rather than
            // in a normalised space nobody can read — and the clock, which only the foliage
            // reads.
            new Vector4(settings.AmbientOcclusionRadius, Seconds, width, height),

            // Where the light grid starts and how it is divided, so a fragment can work out
            // which cell it stands in. Constant for as long as a room is loaded.
            GridOrigin,
            GridCounts,
            new Vector4(settings.Ambient, settings.LightmapHint),

            // The jitter the projection above was built with, so the fragment stage can take
            // it back out of the motion vectors, and how far above white a surface that
            // carries its own light may go.
            new Vector4(JitterPixels.X, JitterPixels.Y, EmissiveGain, 0f));

        _uniforms[frame % Count].Write<FrameUniforms>([uniforms]);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scene = null;

        foreach (D3D12Buffer[] buffers in (D3D12Buffer[][])[_uniforms, _rig, _cells, _reaching])
        {
            foreach (D3D12Buffer buffer in buffers)
            {
                buffer.Dispose();
            }
        }

    }

    private Matrix4x4? _previousViewProjection;
    private float? _wasAt;
}
