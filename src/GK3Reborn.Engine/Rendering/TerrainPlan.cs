// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using System.Runtime.InteropServices;
using GK3Reborn.Rendering.Shaders;

namespace GK3Reborn.Rendering;

/// <summary>One corner of the backdrop: where it is and which way its ground faces.</summary>
/// <param name="Position">Where it is, in backdrop metres.</param>
/// <param name="Normal">Which way the surface faces.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct TerrainVertex(Vector3 Position, Vector3 Normal);

/// <summary>Where one modelled tree's geometry sits, and what it is painted with.</summary>
/// <param name="Kind">Which of the impostor species it stands in for.</param>
/// <param name="Detail">Nought for the full tree, one for the cheap one.</param>
/// <param name="Triangles">What it costs to draw one.</param>
/// <param name="FirstIndex">Where it starts in the shared index buffer.</param>
/// <param name="VertexOffset">What is added to each of its indices.</param>
/// <param name="Parts">Its parts, each a texture and a slice of the indices.</param>
public readonly record struct TerrainModelDraw(
    int Kind,
    int Detail,
    int Triangles,
    uint FirstIndex,
    int VertexOffset,
    (int Sheet, uint FirstIndex, uint IndexCount)[] Parts);

/// <summary>What one frame of the backdrop is drawn with.</summary>
/// <param name="Ground">The block the ground, the impostors and the models all read.</param>
/// <param name="Sky">The block the generated sky reads.</param>
/// <param name="Eye">Where the camera stands in backdrop metres, for anything else that asks.</param>
/// <param name="Reselected">
/// Whether the near band was rebuilt this frame, and so whether the model instance data
/// needs writing to the device again. False on most frames: a room camera is a fixed
/// viewpoint and the trees do not move.
/// </param>
public readonly record struct TerrainFrame(
    TerrainConstants Ground, TerrainSkyConstants Sky, Vector3 Eye, bool Reselected);

/// <summary>
/// Everything about drawing a reconstructed horizon that is not a device: the meshes, the
/// forest, which trees are near enough to be models this frame, and the two constant blocks
/// a frame is drawn with.
/// </summary>
/// <remarks>
/// <para>
/// Both backends own buffers, textures and pipelines; neither owns the recipe. What is here
/// is arithmetic over arrays — a heightfield turned into triangles, a placement file gathered
/// by species, a camera turned into the backdrop's own metric space — and none of it has any
/// reason to exist twice. The one thing that would go wrong if it did is the thing hardest
/// to see: two horizons that are each individually plausible and disagree about where a
/// ridge is.
/// </para>
/// <para>
/// The backdrop lives in its own metric space — metres around the scene's centre — and is
/// drawn with its own projection, so no room unit ever meets a terrain metre except at one
/// constant: <see cref="MetersPerUnit"/> turns the camera's offset from the scene centre into
/// a movement through the backdrop, which is what gives the horizon parallax instead of the
/// swimming a camera-glued skybox shows on every cut and glide.
/// </para>
/// <para>
/// It cannot share the room's depth range — the room's projection has no idea what four
/// kilometres are — so the vertex stages squeeze the backdrop's whole depth into the far tail
/// of the buffer, above 0.999. The room always wins the depth test against it, the backdrop
/// still sorts against itself inside the tail, and the generated sky at exactly 1.0 loses to
/// both.
/// </para>
/// <para>
/// The full recipe and why each rule exists:
/// <c>ContentWorkspace/enhanced/skyboxes/terrain-plan.md</c>.
/// </para>
/// </remarks>
public sealed class TerrainPlan
{
    /// <summary>Floats per placed tree: where it is, how big, which way round, which shape.</summary>
    public const int Stride = 6;

    /// <summary>How many metres of backdrop one unit of room is worth.</summary>
    /// <remarks>
    /// GK3's people are about seventy units for a grown adult, so a unit is roughly an
    /// inch; 0.025 keeps a walk across a courtyard a walk, not a flight.
    /// </remarks>
    public const float MetersPerUnit = 0.025f;

    /// <summary>
    /// The shapes a distant wood is made of, in the order the offline placement numbers them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four silhouettes rather than one. A hillside of identical cones is the tell that
    /// gave the reconstructed horizon away from across a valley: a real wood is conifers
    /// and broadleaves mixed, with scrub where it thins out, and at a kilometre the only
    /// thing that survives of a tree <em>is</em> its silhouette — so the silhouette is the
    /// one thing worth spending geometry on.
    /// </para>
    /// <para>
    /// Sixteen to twenty-four triangles apiece, which is what an impostor can afford when
    /// there are twenty thousand of them. The measurements are metres at a scale of one,
    /// and the offline placement varies that per tree.
    /// </para>
    /// </remarks>
    private static readonly (int Sides, float Height, (float At, float Radius)[] Rings)[]
        Impostors =
    [
        // A spruce: widest at the foot, drawn straight to a point.
        (8, 14f, [(0.00f, 3.6f), (0.45f, 2.2f)]),

        // A broadleaf: a round crown carried clear of the ground, widest a third of the
        // way up and closed underneath. Three rings, because two make a diamond and a
        // diamond at this range is a cone with a pointed bottom.
        (10, 11f, [(0.24f, 2.9f), (0.44f, 4.5f), (0.74f, 3.5f)]),

        // A cypress: kept narrow and run tall.
        (6, 17f, [(0.00f, 1.7f), (0.55f, 1.4f)]),

        // Scrub, for the fringe of a wood and the open ground beyond it.
        (6, 3.6f, [(0.10f, 2.6f), (0.45f, 3.0f)]),
    ];

    /// <summary>How many impostor species there are.</summary>
    public static int ImpostorCount => Impostors.Length;

    private float[] _heights = [];
    private int _grid;
    private float _extent;
    private Vector3? _sunDirection;
    private float _azimuth;
    private Vector3 _anchorUnits;

    private float[] _placements = [];
    private (float Away, int At)[] _candidates = [];
    private Vector3 _selectedAt = new(float.MaxValue);
    private float _modelReach;
    private float _modelKinds;

    private TerrainPlan()
    {
    }

    /// <summary>How many metres of ground one tile of texture covers.</summary>
    public float TileMeters { get; set; } = 60f;

    /// <summary>How far the camera is kept above the backdrop's own ground, in metres.</summary>
    /// <remarks>
    /// About a person's eye height. What it guards is not the view from a hill — where the
    /// camera stands tens of metres over the terrain and should — but the case where
    /// <see cref="LiftMeters"/> is larger than the ground under the viewpoint, which buries
    /// the camera and turns the whole horizon into a rising wall.
    /// </remarks>
    public float ClearanceMeters { get; set; } = 2f;

    /// <summary>How far the whole backdrop is raised against the camera, in metres.</summary>
    /// <remarks>
    /// The offline heights put the panorama's own camera at zero, but the room's cameras
    /// stand wherever the scenes put them — often high enough that whole hillsides sink
    /// below the visible horizon. Raising the backdrop is done by standing the camera lower
    /// in it, which carries the fog along for free.
    /// </remarks>
    public float LiftMeters { get; set; } = 12f;

    /// <summary>How strongly the vista's colour is laid over the tiles, zero to one.</summary>
    public float TintAmount { get; set; } = 0.6f;

    /// <summary>
    /// How much of the light a metre of air at the valley floor takes out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set so that a hillside half a kilometre off has lost about a third of its contrast
    /// and one two kilometres off is very nearly the colour of the sky. That is a clear day
    /// in hill country rather than a foggy one.
    /// </para>
    /// <para>
    /// Per metre, and deliberately not scaled to the size of the set. What decides how hazy
    /// a mountain looks is how far away it is, and a reconstruction that reaches six
    /// kilometres should have a hazier rim than one that reaches one.
    /// </para>
    /// </remarks>
    public float HazeDensity { get; set; } = 6.5e-4f;

    /// <summary>How many metres the haze thins over, above the camera.</summary>
    /// <remarks>
    /// The scale height of the air, and what makes this aerial perspective rather than
    /// distance fog. At a hundred and thirty metres a ridge rising two hundred above the
    /// camera sits in a fifth of the density its own foot does, so it stands clear of the
    /// murk in the valley below it — which is the shape the eye reads as depth in real
    /// country, and the reason a flat fog makes hills look like a painted flat.
    /// </remarks>
    public float HazeHeight { get; set; } = 130f;

    /// <summary>Fraction of the procedural sky occupied by cloud, zero to one.</summary>
    public float CloudCoverage { get; set; } = 0.78f;

    /// <summary>Frequency of the cloud forms; smaller values make broader masses.</summary>
    public float CloudScale { get; set; } = 1f;

    /// <summary>How far the modelled trees may reach, in metres from the camera.</summary>
    /// <remarks>
    /// Past this the impostors have it, whatever the budget would allow. Three hundred
    /// metres is where a fourteen-metre tree is about forty pixels tall on a 720-line
    /// screen — small enough that a cone with the right silhouette is honestly as good, and
    /// small enough that the alpha-tested cards start to shimmer rather than resolve.
    /// </remarks>
    public float ModelReachMeters { get; set; } = 460f;

    /// <summary>
    /// How many triangles a frame may spend on the near forest.
    /// </summary>
    /// <remarks>
    /// The budget rather than a count of trees, because the two levels of detail differ by
    /// five times: a full tree is twenty thousand triangles and the cheap one four, so "the
    /// nearest two hundred" means something very different depending on which is drawn.
    /// Spending it nearest-first means the trees the player is looking at get the full model
    /// and the rest get whatever is left.
    /// </remarks>
    public int ModelTriangleBudget { get; set; } = 3_000_000;

    /// <summary>How many of the nearest may be the full model rather than the cheap one.</summary>
    /// <remarks>
    /// Both a count and a distance, and the distance is what stops the count being silly. A
    /// full broadleaf is twenty-two thousand triangles against the cheap one's four, and
    /// spending the first forty of those on trees a quarter of a kilometre out — where the
    /// two are indistinguishable — is most of the budget gone before the band that can
    /// actually use it. Seventy metres is about where the difference stops showing.
    /// </remarks>
    public int FullDetailTrees { get; set; } = 48;

    /// <summary>How near a tree must be to be worth the full model.</summary>
    public float FullDetailMeters { get; set; } = 70f;

    /// <summary>The ground's corners.</summary>
    public TerrainVertex[] Vertices { get; private set; } = [];

    /// <summary>Its triangles.</summary>
    public uint[] Indices { get; private set; } = [];

    /// <summary>Every impostor shape's corners, in one buffer.</summary>
    public TerrainVertex[] TreeVertices { get; private set; } = [];

    /// <summary>Their triangles, in one buffer, sliced by <see cref="ImpostorRanges"/>.</summary>
    public ushort[] TreeIndices { get; private set; } = [];

    /// <summary>The whole forest, gathered by species: six floats a tree.</summary>
    public float[] TreeInstances { get; private set; } = [];

    /// <summary>Where each impostor shape's geometry sits in the shared buffers.</summary>
    public (uint FirstIndex, int VertexOffset, uint IndexCount)[] ImpostorRanges
    {
        get;
        private set;
    } = [];

    /// <summary>How many instances of each shape there are, and where they start.</summary>
    public (uint First, uint Count)[] Stands { get; private set; } = [];

    /// <summary>How many trees the impostor buffer holds in all.</summary>
    public uint TreeCount { get; private set; }

    /// <summary>The modelled trees' corners, in one buffer.</summary>
    public TerrainTreeVertex[] ModelVertices { get; private set; } = [];

    /// <summary>Their triangles, in one buffer.</summary>
    public uint[] ModelIndices { get; private set; } = [];

    /// <summary>What can be drawn as a model, and where its geometry is.</summary>
    public TerrainModelDraw[] Models { get; private set; } = [];

    /// <summary>How many instances of each model there are this frame, and where.</summary>
    public (uint First, uint Count)[] ModelStands { get; private set; } = [];

    /// <summary>
    /// The near band's placements, six floats a tree, filled by <see cref="Frame"/>.
    /// </summary>
    /// <remarks>
    /// Sized once for every tree the budget could reach at the cheapest model, so the
    /// selection never grows it and a frame never allocates. Only the first
    /// <see cref="ModelCount"/> times <see cref="Stride"/> floats are live.
    /// </remarks>
    public float[] ModelInstanceData { get; private set; } = [];

    /// <summary>How many trees are drawn as models this frame.</summary>
    public uint ModelCount { get; private set; }

    /// <summary>Whether there is any ground to draw at all.</summary>
    public bool HasGround => Indices.Length > 0;

    /// <summary>Works the whole backdrop out, on the host.</summary>
    /// <param name="backdrop">The terrain, forest and layers.</param>
    /// <param name="sheets">
    /// How many tree textures the device will have. A part painted with one that is not
    /// there is dropped, which is what keeps a half-published set drawing.
    /// </param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentException">Its heights do not match its grid.</exception>
    public static TerrainPlan Create(TerrainBackdrop backdrop, int sheets)
    {
        ArgumentNullException.ThrowIfNull(backdrop);

        var plan = new TerrainPlan
        {
            _extent = backdrop.ExtentMeters,
            _sunDirection = backdrop.SunDirection,
            _azimuth = backdrop.Azimuth,
            _anchorUnits = backdrop.AnchorUnits,
        };

        plan.BuildMesh(backdrop);
        plan.BuildTrees(backdrop);
        plan.BuildTreeModels(backdrop, sheets);

        return plan;
    }

    /// <summary>
    /// Works out what this frame draws: the near band, and the two constant blocks.
    /// </summary>
    /// <param name="camera">Where the player is looking from, in room units.</param>
    /// <param name="width">Viewport width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The frame.</returns>
    public TerrainFrame Frame(Camera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);

        // The camera's offset from the scene centre, turned into backdrop metres and into
        // the backdrop's own frame — the sky's azimuth separates the two. Clamped so no
        // camera the scripts place can leave the grid or dive through a ridge.
        Matrix4x4 intoTerrain = Matrix4x4.CreateRotationY(-_azimuth);
        Vector3 offset = Vector3.TransformNormal(
            (camera.Position - _anchorUnits) * MetersPerUnit, intoTerrain);

        float reach = _extent * 0.25f;
        if (offset.Length() > reach)
        {
            offset = Vector3.Normalize(offset) * reach;
        }

        offset.Y -= LiftMeters;

        // And never below the ground it is standing on. The lift is a constant and the
        // reconstruction is not: a set whose panorama saw almost nothing is nearly all
        // fill, and its fill sits close to zero — so twelve metres of lift put the camera a
        // few metres *under* the surface, and every direction became a wall of hillside
        // rising out of the bottom of the frame. CSD is the set that did it. Raising rather
        // than clamping, because a lookout genuinely stands sixty metres over its own
        // valley and that has to survive.
        offset.Y = MathF.Max(offset.Y, Ground(offset.X, offset.Z) + ClearanceMeters);

        Vector3 forward = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 forwardT = Vector3.TransformNormal(forward, intoTerrain);
        Vector3 upT = Vector3.TransformNormal(camera.Up, intoTerrain);

        Matrix4x4 view = Matrix4x4.CreateLookAtLeftHanded(offset, offset + forwardT, upT);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
            camera.FieldOfView, (float)width / Math.Max(1, height), 2f, _extent * 3f);
        projection.M22 *= -1;

        // The sun the scene is lit by, brought into the backdrop's frame so a slope faces it
        // the same way the room's shadows say it should.
        Vector4 sun = _sunDirection is { } travelling
            ? new Vector4(
                Vector3.TransformNormal(Vector3.Normalize(-travelling), intoTerrain), 1f)
            : new Vector4(0f, 1f, 0f, 0f);

        // Before the block is built, because how far the modelled trees reached is one of
        // the numbers in it: the impostors are told to start where the models ran out.
        bool reselected = SelectTreeModels(offset);

        var ground = new TerrainConstants
        {
            ViewProjection = view * projection,
            Sun = sun,
            Params = new Vector4(TileMeters, TintAmount, HazeDensity, _extent),
            Eye = new Vector4(offset, Math.Clamp(CloudCoverage, 0f, 1f)),
            Haze = new Vector4(_modelReach, _modelKinds, 0f, MathF.Max(1f, HazeHeight)),
        };

        // The sky's basis must be the same orthonormal basis CreateLookAt uses. Passing
        // camera.Up directly works only at zero pitch; as the view tilts it shears every ray
        // away from the centre, which made a fixed cloud field appear to zoom and squint.
        Vector3 skyRight = Vector3.Normalize(Vector3.Cross(camera.Up, forward));
        Vector3 skyUp = Vector3.Cross(forward, skyRight);
        float tanHalfFov = MathF.Tan(camera.FieldOfView / 2f);

        var sky = new TerrainSkyConstants
        {
            Forward = new Vector4(forward, 0f),
            Right = new Vector4(skyRight, tanHalfFov * width / Math.Max(1, height)),
            Up = new Vector4(skyUp, tanHalfFov),
            Viewport = new Vector4(width, height, 0f, 0f),
            Sun = _sunDirection is { } sunWorld
                ? new Vector4(Vector3.Normalize(-sunWorld), 1f)
                : new Vector4(0f, 1f, 0f, 0f),
            Clouds = new Vector4(
                Math.Clamp(CloudCoverage, 0f, 1f),
                Math.Clamp(CloudScale, 0.25f, 4f),
                19.7f + (MathF.Sin(_azimuth) * 13.1f),
                -7.3f + (MathF.Cos(_azimuth) * 17.9f)),
        };

        return new TerrainFrame(ground, sky, offset, reselected);
    }

    private void BuildMesh(TerrainBackdrop backdrop)
    {
        // Every other grid cell: 512 by 512 corners over the 1024 grid is a quarter of
        // the vertices for a silhouette the eye cannot tell apart at these distances.
        const int Step = 2;

        int grid = backdrop.Grid;
        float extent = backdrop.ExtentMeters;
        float[] heights = backdrop.Heights;

        if (heights.Length != grid * grid)
        {
            throw new ArgumentException(
                $"A terrain backdrop's heights are {heights.Length} values for a " +
                $"{grid} by {grid} grid.",
                nameof(backdrop));
        }

        int side = ((grid - 1) / Step) + 1;
        float step = (2f * extent) / (grid - 1);

        // Kept, so the camera can be told what the ground under it is doing. See Ground.
        _heights = heights;
        _grid = grid;

        var vertices = new TerrainVertex[side * side];

        for (int row = 0; row < side; row++)
        {
            int gz = Math.Min(row * Step, grid - 1);

            for (int column = 0; column < side; column++)
            {
                int gx = Math.Min(column * Step, grid - 1);

                // Central differences over the vertices that are actually drawn.
                //
                // They used to be taken over single cells of the full-resolution grid, on
                // the reasoning that a vertex the stride skipped should still bend its
                // neighbours' normals. It does — and the detail it bends them by is finer
                // than the surface carrying it, so neighbouring vertices a stride apart get
                // normals from unrelated cells. On a ridge whose faces are a pixel or two
                // wide, that lights adjacent triangles differently for no reason the shape
                // shows, and the ridge crawls. The normal has to describe the surface that
                // is there.
                float left = heights[(gz * grid) + Math.Max(gx - Step, 0)];
                float right = heights[(gz * grid) + Math.Min(gx + Step, grid - 1)];
                float near = heights[(Math.Max(gz - Step, 0) * grid) + gx];
                float far = heights[(Math.Min(gz + Step, grid - 1) * grid) + gx];

                var normal = Vector3.Normalize(
                    new Vector3(left - right, 2f * Step * step, near - far));

                vertices[(row * side) + column] = new TerrainVertex(
                    new Vector3(
                        (gx * step) - extent,
                        heights[(gz * grid) + gx],
                        (gz * step) - extent),
                    normal);
            }
        }

        uint[] indices = new uint[(side - 1) * (side - 1) * 6];
        int write = 0;

        for (int row = 0; row < side - 1; row++)
        {
            for (int column = 0; column < side - 1; column++)
            {
                uint a = (uint)((row * side) + column);
                uint b = a + 1;
                uint c = a + (uint)side;
                uint d = c + 1;

                indices[write++] = a;
                indices[write++] = c;
                indices[write++] = b;
                indices[write++] = b;
                indices[write++] = c;
                indices[write++] = d;
            }
        }

        Vertices = vertices;
        Indices = indices;
    }

    private void BuildTrees(TerrainBackdrop backdrop)
    {
        float[] trees = backdrop.Trees;

        if (trees.Length < Stride)
        {
            return;
        }

        List<TerrainVertex> mesh = [];
        List<ushort> shapes = [];
        var ranges = new (uint, int, uint)[Impostors.Length];

        for (int kind = 0; kind < Impostors.Length; kind++)
        {
            (int sides, float height, (float At, float Radius)[] rings) = Impostors[kind];
            int vertexOffset = mesh.Count;
            uint firstIndex = (uint)shapes.Count;

            foreach ((float at, float radius) in rings)
            {
                for (int i = 0; i < sides; i++)
                {
                    float angle = i * (2f * MathF.PI / sides);
                    var outward = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));

                    mesh.Add(new TerrainVertex(
                        (outward * radius) + new Vector3(0f, at * height, 0f),
                        Vector3.Normalize(outward + new Vector3(0f, radius / height, 0f))));
                }
            }

            // Relative to this shape's first vertex, because the draw adds the shape's
            // vertex offset for us. Absolute here and it would be added twice, and every
            // shape but the first would be built from another shape's corners.
            int tip = mesh.Count - vertexOffset;
            mesh.Add(new TerrainVertex(new Vector3(0f, height, 0f), Vector3.UnitY));

            // A crown lifted off the ground is closed underneath; one standing on it is
            // not, because nothing ever sees the bottom of a fir.
            bool skirted = rings[0].At > 0.001f;
            int foot = mesh.Count - vertexOffset;

            if (skirted)
            {
                mesh.Add(new TerrainVertex(
                    new Vector3(0f, rings[0].At * height * 0.35f, 0f), -Vector3.UnitY));
            }

            void Band(int lower, int upper)
            {
                for (int i = 0; i < sides; i++)
                {
                    int next = (i + 1) % sides;

                    shapes.Add((ushort)(lower + i));
                    shapes.Add((ushort)(lower + next));
                    shapes.Add((ushort)(upper + next));

                    shapes.Add((ushort)(lower + i));
                    shapes.Add((ushort)(upper + next));
                    shapes.Add((ushort)(upper + i));
                }
            }

            for (int ring = 0; ring + 1 < rings.Length; ring++)
            {
                Band(ring * sides, (ring + 1) * sides);
            }

            int last = (rings.Length - 1) * sides;

            for (int i = 0; i < sides; i++)
            {
                int next = (i + 1) % sides;

                shapes.Add((ushort)(last + i));
                shapes.Add((ushort)(last + next));
                shapes.Add((ushort)tip);

                if (skirted)
                {
                    shapes.Add((ushort)next);
                    shapes.Add((ushort)i);
                    shapes.Add((ushort)foot);
                }
            }

            ranges[kind] = (firstIndex, vertexOffset, (uint)shapes.Count - firstIndex);
        }

        // The instances, straight from the offline placement but gathered by shape, so that
        // one wood is four draws over four slices of one buffer rather than four buffers or
        // a branch in the vertex shader. A cap far above any real set, purely so a malformed
        // file cannot ask for the moon.
        uint count = Math.Min((uint)(trees.Length / Stride), 800_000u);
        var placed = new float[count * Stride];
        var stands = new (uint First, uint Count)[Impostors.Length];
        uint written = 0;

        for (int kind = 0; kind < Impostors.Length; kind++)
        {
            uint first = written;

            for (uint at = 0; at < count; at++)
            {
                // Anything the file numbers past the shapes that exist falls to the first,
                // which is a conifer: an unknown species is still a tree.
                int wanted = (int)trees[(at * Stride) + 5];

                if (wanted != kind && !(kind == 0 && (wanted < 0 || wanted >= Impostors.Length)))
                {
                    continue;
                }

                trees.AsSpan((int)(at * Stride), Stride)
                    .CopyTo(placed.AsSpan((int)(written * Stride), Stride));

                written++;
            }

            stands[kind] = (first, written - first);
        }

        TreeVertices = [.. mesh];
        TreeIndices = [.. shapes];
        TreeInstances = placed;
        ImpostorRanges = ranges;
        Stands = stands;
        TreeCount = written;
    }

    private void BuildTreeModels(TerrainBackdrop backdrop, int sheets)
    {
        if (backdrop.TreeModels.Count == 0 || backdrop.Trees.Length < Stride || sheets <= 0)
        {
            return;
        }

        var corners = new List<TerrainTreeVertex>();
        var indices = new List<uint>();
        var draws = new List<TerrainModelDraw>();

        foreach (TerrainTreeModel model in backdrop.TreeModels)
        {
            if (model.Kind is < 0 or >= 4 || model.Vertices.Length == 0)
            {
                continue;
            }

            int vertexOffset = corners.Count;
            uint firstIndex = (uint)indices.Count;

            corners.AddRange(model.Vertices);
            indices.AddRange(model.Indices);

            var parts = new List<(int, uint, uint)>();

            foreach (TerrainTreePart part in model.Parts)
            {
                if (part.Texture >= 0 && part.Texture < sheets && part.IndexCount > 0)
                {
                    parts.Add((part.Texture, firstIndex + part.FirstIndex, part.IndexCount));
                }
            }

            if (parts.Count == 0)
            {
                continue;
            }

            draws.Add(new TerrainModelDraw(
                model.Kind, model.Detail, model.Triangles, firstIndex, vertexOffset, [.. parts]));

            _modelKinds = (int)_modelKinds | (1 << model.Kind);
        }

        if (draws.Count == 0)
        {
            return;
        }

        Models = [.. draws];
        ModelStands = new (uint, uint)[Models.Length];
        ModelVertices = [.. corners];
        ModelIndices = [.. indices];
        _placements = backdrop.Trees;

        // Room for every tree the budget could ever reach at the cheapest model, so the
        // selection never has to grow it and a frame never allocates.
        int cheapest = Models.Min(m => Math.Max(1, m.Triangles));
        int capacity = Math.Clamp(ModelTriangleBudget / cheapest, 64, 20_000);

        ModelInstanceData = new float[capacity * Stride];
    }

    /// <summary>
    /// Picks which trees are near enough to be drawn as models, and where.
    /// </summary>
    /// <param name="eye">The camera, in backdrop metres.</param>
    /// <returns>Whether the selection was rebuilt.</returns>
    /// <remarks>
    /// <para>
    /// Nearest first, spending a triangle budget: the closest handful get the full model,
    /// the next few hundred get the cheap one, and the budget stops wherever it stops. What
    /// that leaves is how far the models actually got, and the impostors are told to start
    /// there rather than at a constant, so a dense wood and a thin one both hand over
    /// exactly where the models ran out.
    /// </para>
    /// <para>
    /// Only when the camera has moved. A room camera is a fixed viewpoint and the trees do
    /// not move, so the answer is the same frame after frame; recomputing it would sort
    /// twenty thousand distances sixty times a second to arrive back where it was.
    /// </para>
    /// </remarks>
    private bool SelectTreeModels(Vector3 eye)
    {
        if (Models.Length == 0)
        {
            return false;
        }

        // Eight metres. Small enough that the band never visibly lags the camera, large
        // enough that a glide is a handful of rebuilds rather than one a frame.
        if ((eye - _selectedAt).LengthSquared() < 64f)
        {
            return false;
        }

        _selectedAt = eye;

        int trees = _placements.Length / Stride;
        float reach = ModelReachMeters;
        float reachSquared = reach * reach;

        if (_candidates.Length < trees)
        {
            _candidates = new (float, int)[trees];
        }

        int found = 0;

        for (int i = 0; i < trees; i++)
        {
            int at = i * Stride;
            int kind = (int)_placements[at + 5];

            if (kind is < 0 or >= 4 || ((int)_modelKinds & (1 << kind)) == 0)
            {
                continue;
            }

            float dx = _placements[at] - eye.X;
            float dy = _placements[at + 1] - eye.Y;
            float dz = _placements[at + 2] - eye.Z;
            float away = (dx * dx) + (dy * dy) + (dz * dz);

            if (away < reachSquared)
            {
                _candidates[found++] = (away, at);
            }
        }

        Array.Sort(_candidates, 0, found, CandidateOrder.Instance);

        // Grouped by model, because a draw is one model and one slice of the buffer. The
        // pass below decides each tree's detail from its rank, and the pass after gathers
        // them: two cheap walks rather than a sort inside every group.
        int capacity = ModelInstanceData.Length / Stride;
        float full = FullDetailMeters * FullDetailMeters;
        var wanted = new int[Models.Length];
        int spent = 0;
        int taken = 0;
        float last = 0;

        var detail = new byte[found];

        for (int rank = 0; rank < found && taken < capacity; rank++)
        {
            int at = _candidates[rank].At;
            int kind = (int)_placements[at + 5];
            int want = rank < FullDetailTrees && _candidates[rank].Away < full ? 0 : 1;
            int model = Model(kind, want);

            // A species with only one of the two levels grown uses it for both bands.
            if (model < 0)
            {
                model = Model(kind, 1 - want);
            }

            if (model < 0 || spent + Models[model].Triangles > ModelTriangleBudget)
            {
                break;
            }

            detail[rank] = (byte)model;
            wanted[model]++;
            spent += Models[model].Triangles;
            last = _candidates[rank].Away;
            taken++;
        }

        uint first = 0;

        for (int model = 0; model < Models.Length; model++)
        {
            ModelStands[model] = (first, (uint)wanted[model]);
            first += (uint)wanted[model];
            wanted[model] = 0;
        }

        for (int rank = 0; rank < taken; rank++)
        {
            int model = detail[rank];
            uint slot = ModelStands[model].First + (uint)wanted[model]++;

            _placements.AsSpan(_candidates[rank].At, Stride)
                .CopyTo(ModelInstanceData.AsSpan((int)slot * Stride, Stride));
        }

        ModelCount = (uint)taken;

        // Where the impostors are told to take over. The farthest tree the budget reached
        // when it ran out, and the full reach when it did not: a wood that fits entirely
        // inside the budget should hand over at the distance, not at its own last tree.
        _modelReach = taken == 0
            ? 0f
            : (taken < found ? MathF.Sqrt(last) : reach);

        return true;
    }

    /// <summary>
    /// The height of the backdrop's ground at a point, in its own metres.
    /// </summary>
    /// <param name="x">Where, east.</param>
    /// <param name="z">Where, north.</param>
    /// <returns>The height, or nought when there is no grid to ask.</returns>
    /// <remarks>
    /// Bilinear, and off the full-resolution grid rather than the drawn mesh: this is asked
    /// once a frame and what it is for is keeping the camera out of the hill, so it should
    /// agree with the ground rather than with the stride the ground is drawn at.
    /// </remarks>
    private float Ground(float x, float z)
    {
        if (_grid < 2 || _heights.Length != _grid * _grid || _extent <= 0f)
        {
            return 0f;
        }

        float at = ((x / _extent) + 1f) * 0.5f * (_grid - 1);
        float down = ((z / _extent) + 1f) * 0.5f * (_grid - 1);

        int left = Math.Clamp((int)MathF.Floor(at), 0, _grid - 2);
        int top = Math.Clamp((int)MathF.Floor(down), 0, _grid - 2);
        float acrossFraction = Math.Clamp(at - left, 0f, 1f);
        float downFraction = Math.Clamp(down - top, 0f, 1f);

        float upper = float.Lerp(
            _heights[(top * _grid) + left], _heights[(top * _grid) + left + 1], acrossFraction);
        float lower = float.Lerp(
            _heights[((top + 1) * _grid) + left],
            _heights[((top + 1) * _grid) + left + 1],
            acrossFraction);

        return float.Lerp(upper, lower, downFraction);
    }

    /// <summary>Which model draws a given species at a given level of detail.</summary>
    private int Model(int kind, int detail)
    {
        for (int i = 0; i < Models.Length; i++)
        {
            if (Models[i].Kind == kind && Models[i].Detail == detail)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Nearest first.</summary>
    private sealed class CandidateOrder : IComparer<(float Away, int At)>
    {
        public static readonly CandidateOrder Instance = new();

        public int Compare((float Away, int At) a, (float Away, int At) b) =>
            a.Away.CompareTo(b.Away);
    }
}
