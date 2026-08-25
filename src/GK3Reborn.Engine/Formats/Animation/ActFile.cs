using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Formats.Animation;

/// <summary>Where one mesh group of a model sits on one frame.</summary>
/// <param name="Frame">Which frame.</param>
/// <param name="Mesh">Which mesh group of the model.</param>
/// <param name="MeshToLocal">Its basis and position, in the model's own space.</param>
public readonly record struct MeshPose(int Frame, int Mesh, Matrix4x4 MeshToLocal);

/// <summary>What one mesh group of a model occupies on one frame.</summary>
/// <param name="Frame">Which frame.</param>
/// <param name="Mesh">Which mesh group.</param>
/// <param name="Minimum">Lower corner, in mesh space.</param>
/// <param name="Maximum">Upper corner, in mesh space.</param>
public readonly record struct MeshBounds(int Frame, int Mesh, Vector3 Minimum, Vector3 Maximum);

/// <summary>The shape of one submesh on one frame.</summary>
/// <param name="Frame">Which frame.</param>
/// <param name="Mesh">Which mesh group.</param>
/// <param name="Submesh">Which submesh within it.</param>
/// <param name="Positions">Every vertex, in mesh space.</param>
public readonly record struct VertexPose(
    int Frame, int Mesh, int Submesh, IReadOnlyList<Vector3> Positions);

/// <summary>
/// A GK3 vertex animation.
/// </summary>
/// <remarks>
/// <para>
/// 5,796 clips and 399 MB — the whole of the game's movement. GK3's characters have no
/// skeleton: a clip stores, per frame, where each of a model's mesh groups sits and where
/// every one of its vertices is. A walk cycle is a list of poses, not a set of bone angles.
/// </para>
/// <para>
/// The format and every constant here are the spec in <c>Plan/06-c6-rig-solve.md</c> §3,
/// which was transcribed from G-Engine and validated by parsing the entire corpus. Its five
/// invariants are checked as the file is read rather than assumed, because each one failing
/// means the reader has lost its place and everything after is noise.
/// </para>
/// <para>
/// Vertex data is read only when asked for. <c>gab</c> alone is 50.2 million vertex samples
/// across its 943 clips, so anything that only wants to know how long a clip is, or where a
/// door swings to, should not pay for that.
/// </para>
/// </remarks>
public sealed class ActFile
{
    /// <summary>The only version the corpus contains.</summary>
    public const int Version = 258;

    /// <summary>The five bytes a fifth of the corpus ends with, which nothing reads.</summary>
    private static ReadOnlySpan<byte> Trailer => [0x01, 0x00, 0x00, 0x00, 0x00];

    private readonly Dictionary<int, List<MeshPose>> _transforms = [];
    private readonly Dictionary<(int Mesh, int Submesh), List<VertexPose>> _shapes = [];

    private ActFile(string name, string model, int frames, int meshes)
    {
        Name = name;
        ModelName = model;
        FrameCount = frames;
        MeshCount = meshes;
    }

    /// <summary>Name it was read under.</summary>
    public string Name { get; }

    /// <summary>
    /// The model it animates.
    /// </summary>
    /// <remarks>
    /// From the header, which is authoritative. In a 900-file sample 110 clips — 12% — sit
    /// in a directory named for a different model than the one they actually target, so
    /// pairing by filename mismatches one clip in eight.
    /// </remarks>
    public string ModelName { get; }

    /// <summary>How many frames long it is.</summary>
    public int FrameCount { get; }

    /// <summary>How many mesh groups the target model must have.</summary>
    public int MeshCount { get; }

    /// <summary>How long it lasts, in seconds.</summary>
    public double Duration => (double)FrameCount / AnimationFile.FramesPerSecond;

    /// <summary>Where each mesh group is, on the frames that record it.</summary>
    public List<MeshPose> Transforms { get; } = [];

    /// <summary>What each mesh group occupies, on the frames that record it.</summary>
    public List<MeshBounds> Bounds { get; } = [];

    /// <summary>The shapes, when the reader was asked for them.</summary>
    public List<VertexPose> Vertices { get; } = [];

    /// <summary>
    /// Whether the clip moves vertices at all, or only whole mesh groups.
    /// </summary>
    /// <remarks>
    /// 2,188 of the 5,796 clips — 37.8% — are rigid: a door swinging, a phone being lifted,
    /// a go-kart. Those need no skinning and no rig, only their mesh transforms, which is
    /// why they are the part of the corpus that can be played first.
    /// </remarks>
    public bool Deforms { get; private set; }

    /// <summary>Reads a clip.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="name">Name used in diagnostics.</param>
    /// <param name="diagnostics">Receives a reason when it cannot be read.</param>
    /// <param name="vertices">Whether to keep the vertex poses, which are most of the file.</param>
    /// <returns>The clip, or null when it is not one.</returns>
    public static ActFile? Read(
        ReadOnlySpan<byte> bytes, string name, DiagnosticBag diagnostics, bool vertices = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (bytes.Length < 52 || !bytes[..4].SequenceEqual("HTCA"u8))
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1150", DiagnosticSeverity.Warning,
                "A vertex animation does not start with the ACT marker.",
                name, null, "HTCA",
                bytes.Length >= 4 ? System.Text.Encoding.ASCII.GetString(bytes[..4]) : "an empty file",
                "The file may not be an animation at all."));

            return null;
        }

        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        int frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int meshes = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);

        if (version != Version)
        {
            diagnostics.Add(new Diagnostic(
                "GK3R1151", DiagnosticSeverity.Warning,
                "A vertex animation is a version nothing here has seen.",
                name, null, Version.ToString(CultureInfo.InvariantCulture),
                version.ToString(CultureInfo.InvariantCulture),
                "Every one of the corpus's 5,796 clips is version 258."));

            return null;
        }

        string model = Text(bytes.Slice(20, 32));
        var act = new ActFile(name, model, frames, meshes);

        try
        {
            act.Body(bytes, vertices);
        }
        catch (FormatParseException ex)
        {
            diagnostics.Add(ex.Diagnostic);
            return null;
        }

        return act;
    }

    /// <summary>
    /// Where a mesh group is on a frame.
    /// </summary>
    /// <param name="mesh">Which mesh group.</param>
    /// <param name="frame">Which frame.</param>
    /// <returns>Its transform, or null when the clip never places it.</returns>
    /// <remarks>
    /// The closest <em>previous</em> recorded pose, which is the rule from G-Engine's
    /// <c>VertexAnimationPose::GetForFrame</c>. A mesh that does not move is not recorded
    /// again, so a reader that expects a pose on every frame finds holes in every clip.
    /// </remarks>
    public Matrix4x4? PoseOf(int mesh, int frame)
    {
        if (!_transforms.TryGetValue(mesh, out List<MeshPose>? poses) || poses.Count == 0)
        {
            return null;
        }

        Matrix4x4? found = null;

        foreach (MeshPose pose in poses)
        {
            if (pose.Frame > frame)
            {
                break;
            }

            found = pose.MeshToLocal;
        }

        return found ?? poses[0].MeshToLocal;
    }

    /// <summary>
    /// The shape of a submesh on a frame.
    /// </summary>
    /// <param name="mesh">Which mesh group.</param>
    /// <param name="submesh">Which submesh within it.</param>
    /// <param name="frame">Which frame.</param>
    /// <returns>Its vertices, or null when the clip never shapes it.</returns>
    /// <remarks>
    /// The same closest-previous rule as <see cref="PoseOf"/>, and for the same reason: a
    /// submesh whose shape has not changed is not written again.
    /// </remarks>
    public IReadOnlyList<Vector3>? ShapeOf(int mesh, int submesh, int frame)
    {
        if (!_shapes.TryGetValue((mesh, submesh), out List<VertexPose>? poses) || poses.Count == 0)
        {
            return null;
        }

        IReadOnlyList<Vector3> found = poses[0].Positions;

        foreach (VertexPose pose in poses)
        {
            if (pose.Frame > frame)
            {
                break;
            }

            found = pose.Positions;
        }

        return found;
    }

    /// <summary>
    /// Where a mesh group is at a moment between two frames.
    /// </summary>
    /// <param name="mesh">Which mesh group.</param>
    /// <param name="frame">Which frame, with a fraction of the way to the next one.</param>
    /// <param name="cycles">
    /// Whether the clip runs straight back into itself, so that the last frame leads to the
    /// first rather than being held.
    /// </param>
    /// <returns>Its transform, or null when the clip never places it.</returns>
    /// <remarks>
    /// <para>
    /// GK3 records fifteen poses a second and a modern display shows sixty frames a second,
    /// so playing the recorded poses as they stand shows each of them four times over. On
    /// anything slow that reads as the original's stiffness; on anything fast it reads as
    /// strobing, and the lobby's ceiling fans — six degrees a recorded pose, ninety a
    /// second — are the clearest case in the game.
    /// </para>
    /// <para>
    /// So a moment between two recorded poses is the two of them mixed — but only where the
    /// two are recorded on consecutive frames. A mesh that does not move is not written
    /// again, so a gap in the recording is a pose held for the length of the gap, and mixing
    /// across one would make a mesh that moves once every ten frames drift the whole way
    /// instead of waiting and then moving. <see cref="Next"/> is where that is decided.
    /// Rotation is a proper spherical mix rather than a mix of the matrices, because mixing
    /// two rotation matrices shrinks whatever is between them.
    /// </para>
    /// </remarks>
    public Matrix4x4? PoseAt(int mesh, float frame, bool cycles = false)
    {
        if (!_transforms.TryGetValue(mesh, out List<MeshPose>? poses) || poses.Count == 0)
        {
            return null;
        }

        int previous = Before(poses.Count, i => poses[i].Frame, frame);

        if (previous < 0)
        {
            return poses[0].MeshToLocal;
        }

        (int to, float span) = Next(poses.Count, previous, poses[previous].Frame, i => poses[i].Frame, cycles);

        if (to < 0 || span <= 0)
        {
            return poses[previous].MeshToLocal;
        }

        return Mix(
            poses[previous].MeshToLocal,
            poses[to].MeshToLocal,
            Math.Clamp((frame - poses[previous].Frame) / span, 0f, 1f));
    }

    /// <summary>
    /// The shape of a submesh at a moment between two frames.
    /// </summary>
    /// <param name="mesh">Which mesh group.</param>
    /// <param name="submesh">Which submesh within it.</param>
    /// <param name="frame">Which frame, with a fraction of the way to the next one.</param>
    /// <param name="cycles">Whether the clip runs straight back into itself.</param>
    /// <returns>Its vertices, or null when the clip never shapes it.</returns>
    /// <remarks>
    /// The same rule as <see cref="PoseAt"/>, straight down the line between the two
    /// recorded shapes. A vertex travelling a curve over several recorded shapes is drawn as
    /// the chords of that curve, which at fifteen recorded shapes a second nobody can see;
    /// what they can see is a character's arm arriving in four equal jumps.
    /// </remarks>
    public IReadOnlyList<Vector3>? ShapeAt(int mesh, int submesh, float frame, bool cycles = false)
    {
        if (!_shapes.TryGetValue((mesh, submesh), out List<VertexPose>? poses) || poses.Count == 0)
        {
            return null;
        }

        int previous = Before(poses.Count, i => poses[i].Frame, frame);

        if (previous < 0)
        {
            return poses[0].Positions;
        }

        (int next, float span) = Next(poses.Count, previous, poses[previous].Frame, i => poses[i].Frame, cycles);

        IReadOnlyList<Vector3> from = poses[previous].Positions;

        if (next < 0 || span <= 0 || from.Count != poses[next].Positions.Count)
        {
            return from;
        }

        float part = Math.Clamp((frame - poses[previous].Frame) / span, 0f, 1f);

        if (part <= 0)
        {
            return from;
        }

        IReadOnlyList<Vector3> to = poses[next].Positions;
        var mixed = new Vector3[from.Count];

        for (int i = 0; i < mixed.Length; i++)
        {
            mixed[i] = Vector3.Lerp(from[i], to[i], part);
        }

        return mixed;
    }

    /// <summary>The last recorded entry at or before a frame, or -1 when there is none.</summary>
    private static int Before(int count, Func<int, int> frameOf, float frame)
    {
        int found = -1;

        for (int i = 0; i < count; i++)
        {
            if (frameOf(i) > frame)
            {
                break;
            }

            found = i;
        }

        return found;
    }

    /// <summary>
    /// Which recorded entry a moment is heading towards, and how many frames away it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only ever the very next frame.</b> A mix is between an entry and one recorded on
    /// the frame after it; a gap in the recording is a pose that is <em>held</em>, and there
    /// is nothing to head towards until the gap ends. That is the rule from G-Engine's
    /// <c>VertexAnimationPose::GetForTime</c>, whose own comment states it: if the next pose
    /// is not for the next frame, use the current pose with no interpolation.
    /// </para>
    /// <para>
    /// A gap says the mesh does not move, because a mesh that does not move is not recorded
    /// again. Sliding across one instead sets the mesh off the moment the hold begins and
    /// lands it as the hold ends, which is the whole gap spent somewhere it never was. It
    /// shows worst on the thing that holds most: <b>a planted foot</b>. Gabriel's walk
    /// records his right shoe on frames 0 and 4 to 15 and his left on 0 to 5 and 14 to 20 —
    /// the gaps are the half of each stride that foot spends on the ground. The right one's
    /// runs off the end of the clip, so what it mixed towards was the opening pose a whole
    /// stride's travel away, and the shoe left his ankle by fifty units and snapped back at
    /// the loop.
    /// </para>
    /// <para>
    /// Past the last recorded entry the pose is held — unless the clip cycles and that entry
    /// is on the clip's last frame, in which case the frame after it is the clip's opening
    /// one. That is what makes a fan come round rather than freezing for a fifteenth of a
    /// second at the top of every turn: its blades are recorded on every frame there is, so
    /// the wrap is a step of one frame like any other.
    /// </para>
    /// </remarks>
    private (int To, float Span) Next(
        int count, int previous, int at, Func<int, int> frameOf, bool cycles)
    {
        if (previous + 1 < count)
        {
            return frameOf(previous + 1) == at + 1 ? (previous + 1, 1f) : (-1, 0);
        }

        return cycles && count > 1 && at == FrameCount - 1 ? (0, 1f) : (-1, 0);
    }

    /// <summary>Mixes two mesh transforms, turning the shorter way round.</summary>
    /// <remarks>
    /// <para>
    /// A rotation is mixed as a rotation and not component by component, because a
    /// component-wise mix of two bases shortens them — the halfway point between two a
    /// quarter turn apart is 71% as long — which shrinks whatever it is applied to as it
    /// turns and springs it back again.
    /// </para>
    /// <para>
    /// <b>Every basis in the corpus is mirrored.</b> GK3's world is left-handed and its
    /// mesh transforms carry a determinant of −1, which <c>Matrix4x4.Decompose</c> deals
    /// with by picking an axis to call negative — and it need not pick the same one twice
    /// running. Decomposing these directly makes a fan blade turn inside out between one
    /// recorded pose and the next, which reads as the fan flickering in and out of
    /// existence rather than as a mistake about handedness. So the mirror is taken out
    /// first, the rotation mixed, and the mirror put back.
    /// </para>
    /// <para>
    /// Anything that is not a rotation with a mirror on it — a basis squashed flat, and
    /// some of the fan housings are — falls back to a straight component-wise mix. At the
    /// step sizes involved, a fifteenth of a second of the fastest thing in the game, that
    /// costs about a tenth of a percent of length and cannot go wrong.
    /// </para>
    /// </remarks>
    private static Matrix4x4 Mix(Matrix4x4 from, Matrix4x4 to, float part)
    {
        if (part <= 0)
        {
            return from;
        }

        if (part >= 1)
        {
            return to;
        }

        if (Turn(from, out Quaternion fromTurn, out bool fromMirrored) &&
            Turn(to, out Quaternion toTurn, out bool toMirrored) &&
            fromMirrored == toMirrored)
        {
            Matrix4x4 mixed = Matrix4x4.CreateFromQuaternion(
                Quaternion.Slerp(fromTurn, toTurn, part));

            if (fromMirrored)
            {
                mixed = Mirror(mixed);
            }

            mixed.Translation = Vector3.Lerp(from.Translation, to.Translation, part);
            return mixed;
        }

        return Matrix4x4.Lerp(from, to, part);
    }

    /// <summary>
    /// Reads a basis as a rotation, saying whether it was mirrored to get there.
    /// </summary>
    /// <param name="basis">The transform.</param>
    /// <param name="turn">The rotation it amounts to, once any mirror is taken out.</param>
    /// <param name="mirrored">Whether taking the mirror out was necessary.</param>
    /// <returns>False when it is not a rotation at all, mirrored or otherwise.</returns>
    /// <remarks>
    /// Orthonormality is checked rather than assumed: a basis whose axes are not unit
    /// length or not at right angles carries a scale or a shear, and turning it into a
    /// quaternion silently throws that away.
    /// </remarks>
    private static bool Turn(Matrix4x4 basis, out Quaternion turn, out bool mirrored)
    {
        turn = Quaternion.Identity;
        mirrored = false;

        var i = new Vector3(basis.M11, basis.M12, basis.M13);
        var j = new Vector3(basis.M21, basis.M22, basis.M23);
        var k = new Vector3(basis.M31, basis.M32, basis.M33);

        const float Slack = 0.01f;

        if (Math.Abs(i.LengthSquared() - 1) > Slack ||
            Math.Abs(j.LengthSquared() - 1) > Slack ||
            Math.Abs(k.LengthSquared() - 1) > Slack ||
            Math.Abs(Vector3.Dot(i, j)) > Slack ||
            Math.Abs(Vector3.Dot(j, k)) > Slack ||
            Math.Abs(Vector3.Dot(i, k)) > Slack)
        {
            return false;
        }

        mirrored = Vector3.Dot(Vector3.Cross(i, j), k) < 0;

        turn = Quaternion.CreateFromRotationMatrix(mirrored ? Mirror(basis) : basis);
        return true;
    }

    /// <summary>Turns a basis inside out, or back again.</summary>
    /// <remarks>
    /// The third axis reversed, which flips the handedness and is its own inverse. Which
    /// axis is arbitrary — any one of the three gives the same answer once it is put back —
    /// and the point is that the same one is chosen every time, which is exactly what
    /// <c>Matrix4x4.Decompose</c> does not promise.
    /// </remarks>
    private static Matrix4x4 Mirror(Matrix4x4 basis)
    {
        basis.M31 = -basis.M31;
        basis.M32 = -basis.M32;
        basis.M33 = -basis.M33;
        return basis;
    }

    /// <summary>Which submeshes of a mesh group the clip shapes.</summary>
    /// <param name="mesh">Which mesh group.</param>
    /// <returns>Their indices, in order.</returns>
    public IReadOnlyList<int> ShapedSubmeshes(int mesh) =>
        [.. _shapes.Keys.Where(k => k.Mesh == mesh).Select(k => k.Submesh).Order()];

    /// <summary>Reads every frame.</summary>
    private void Body(ReadOnlySpan<byte> bytes, bool wantVertices)
    {
        int at = 52;
        int[] offsets = new int[FrameCount];

        for (int i = 0; i < FrameCount; i++)
        {
            offsets[i] = (int)Read32(bytes, ref at);
        }

        // The previous recorded shape of each submesh. Compressed frames store deltas
        // against this rather than against the model's rest pose, so losing it loses
        // everything after it.
        Dictionary<(int Mesh, int Submesh), Vector3[]> last = [];

        for (int frame = 0; frame < FrameCount; frame++)
        {
            // Invariant 2. Each frame declares where it starts, and being anywhere else
            // means a block length was misread — after which everything is noise, so this
            // is the moment to stop rather than the moment to carry on hopefully.
            if (at != offsets[frame])
            {
                throw Malformed(
                    "GK3R1152",
                    "A vertex animation's frame does not start where the file says it does.",
                    offsets[frame].ToString(CultureInfo.InvariantCulture),
                    at.ToString(CultureInfo.InvariantCulture));
            }

            for (int mesh = 0; mesh < MeshCount; mesh++)
            {
                int index = Read16(bytes, ref at);

                // Invariant 3.
                if (index != mesh)
                {
                    throw Malformed(
                        "GK3R1153",
                        "A vertex animation's meshes are not in order.",
                        mesh.ToString(CultureInfo.InvariantCulture),
                        index.ToString(CultureInfo.InvariantCulture));
                }

                long remaining = Read32(bytes, ref at);

                while (remaining > 0)
                {
                    remaining -= Block(bytes, ref at, frame, mesh, last, wantVertices);
                }

                if (remaining != 0)
                {
                    throw Malformed(
                        "GK3R1154",
                        "A vertex animation's blocks do not add up to the length declared.",
                        "0",
                        remaining.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        // Invariant 5.
        ReadOnlySpan<byte> tail = bytes[at..];

        if (tail.Length != 0 && !tail.SequenceEqual(Trailer))
        {
            throw Malformed(
                "GK3R1155",
                "A vertex animation has bytes after its last frame that are not the known trailer.",
                "nothing, or 01 00 00 00 00",
                Convert.ToHexString(tail[..Math.Min(16, tail.Length)]));
        }
    }

    /// <summary>Reads one block, and says how many bytes of the mesh's budget it used.</summary>
    private long Block(
        ReadOnlySpan<byte> bytes,
        ref int at,
        int frame,
        int mesh,
        Dictionary<(int Mesh, int Submesh), Vector3[]> last,
        bool wantVertices)
    {
        int dataId = bytes[at++];
        int size = (int)Read32(bytes, ref at);
        int body = at;

        switch (dataId)
        {
            case 0:
                Shape(bytes, body, frame, mesh, last, wantVertices, compressed: false);
                break;

            case 1:
                Shape(bytes, body, frame, mesh, last, wantVertices, compressed: true);
                break;

            case 2:
            {
                // Invariant 4.
                if (size != 48)
                {
                    throw Malformed(
                        "GK3R1156",
                        "A vertex animation's transform block is the wrong size.",
                        "48",
                        size.ToString(CultureInfo.InvariantCulture));
                }

                int cursor = body;
                Vector3 i = ReadVector(bytes, ref cursor);
                Vector3 j = ReadVector(bytes, ref cursor);
                Vector3 k = ReadVector(bytes, ref cursor);
                Vector3 position = ReadVector(bytes, ref cursor);

                var pose = new MeshPose(frame, mesh, Basis(i, j, k, position));

                Transforms.Add(pose);

                if (!_transforms.TryGetValue(mesh, out List<MeshPose>? poses))
                {
                    poses = [];
                    _transforms[mesh] = poses;
                }

                poses.Add(pose);
                break;
            }

            case 3:
            {
                // Invariant 4.
                if (size != 24)
                {
                    throw Malformed(
                        "GK3R1157",
                        "A vertex animation's bounds block is the wrong size.",
                        "24",
                        size.ToString(CultureInfo.InvariantCulture));
                }

                int cursor = body;
                Vector3 minimum = ReadVector(bytes, ref cursor);
                Vector3 maximum = ReadVector(bytes, ref cursor);

                Bounds.Add(new MeshBounds(frame, mesh, minimum, maximum));
                break;
            }

            default:
                throw Malformed(
                    "GK3R1158",
                    "A vertex animation contains a block of a kind nothing here reads.",
                    "0, 1, 2 or 3",
                    dataId.ToString(CultureInfo.InvariantCulture));
        }

        at = body + size;
        return 1 + 4 + size;
    }

    /// <summary>Reads a submesh's vertices, compressed or not.</summary>
    /// <remarks>
    /// Read even when the caller does not want the positions, because a compressed frame is
    /// a delta against the previous recorded one: skipping a frame's shape would make every
    /// later frame of that submesh wrong. What the flag saves is keeping them, not reading
    /// them — and the block's declared size means the file can still be walked either way.
    /// </remarks>
    private void Shape(
        ReadOnlySpan<byte> bytes,
        int body,
        int frame,
        int mesh,
        Dictionary<(int Mesh, int Submesh), Vector3[]> last,
        bool wantVertices,
        bool compressed)
    {
        Deforms = true;

        int at = body;
        int submesh = Read16(bytes, ref at);
        int count = Read16(bytes, ref at);

        Vector3[] positions = new Vector3[count];
        last.TryGetValue((mesh, submesh), out Vector3[]? previous);

        if (!compressed)
        {
            for (int k = 0; k < count; k++)
            {
                positions[k] = ReadVector(bytes, ref at);
            }
        }
        else
        {
            // Two bits a vertex, low bits first within each byte.
            int codes = (count / 4) + 1;
            ReadOnlySpan<byte> format = bytes.Slice(at, codes);
            at += codes;

            for (int k = 0; k < count; k++)
            {
                int code = (format[k / 4] >> (2 * (k % 4))) & 0x3;
                Vector3 was = previous is not null && k < previous.Length ? previous[k] : default;

                positions[k] = code switch
                {
                    0 => was,
                    1 => was + new Vector3(
                        FromByte(bytes[at++]), FromByte(bytes[at++]), FromByte(bytes[at++])),
                    2 => was + new Vector3(
                        FromUShort(Read16(bytes, ref at)),
                        FromUShort(Read16(bytes, ref at)),
                        FromUShort(Read16(bytes, ref at))),
                    _ => was + ReadVector(bytes, ref at),
                };
            }
        }

        last[(mesh, submesh)] = positions;

        if (!wantVertices)
        {
            return;
        }

        var pose = new VertexPose(frame, mesh, submesh, positions);

        Vertices.Add(pose);

        if (!_shapes.TryGetValue((mesh, submesh), out List<VertexPose>? poses))
        {
            poses = [];
            _shapes[(mesh, submesh)] = poses;
        }

        poses.Add(pose);
    }

    /// <summary>
    /// Builds a mesh's transform from its three bases and its position.
    /// </summary>
    /// <remarks>
    /// The bases are the <em>columns</em> of the rotation, taken exactly as read. They are
    /// orthonormal but their determinant is −1, which is correct: GK3 authored a left-handed
    /// world and the renderer already draws it that way. "Fixing" the handedness here by
    /// negating or permuting mirrors every character in the game.
    /// </remarks>
    private static Matrix4x4 Basis(Vector3 i, Vector3 j, Vector3 k, Vector3 position) => new(
        i.X, i.Y, i.Z, 0,
        j.X, j.Y, j.Z, 0,
        k.X, k.Y, k.Z, 0,
        position.X, position.Y, position.Z, 1);

    /// <summary>
    /// Decodes a one-byte delta.
    /// </summary>
    /// <remarks>
    /// One sign bit, two whole bits, five fractional. The whole part is masked with
    /// <c>0x7F</c> rather than <c>0x60</c> — the sign bit is not cleared before the shift
    /// discards it anyway — and that quirk is reproduced rather than tidied, because tidying
    /// it is how a reader ends up almost right.
    /// </remarks>
    private static float FromByte(byte value)
    {
        float sign = (value & 0x80) == 0 ? 1f : -1f;
        float whole = (value & 0x7F) >> 5;
        float fraction = (value & 0x1F) / 32f;

        return sign * (whole + fraction);
    }

    /// <summary>Decodes a two-byte delta: one sign bit, seven whole, eight fractional.</summary>
    private static float FromUShort(int value)
    {
        float sign = (value & 0x8000) == 0 ? 1f : -1f;
        float whole = (value & 0x7FFF) >> 8;
        float fraction = (value & 0x00FF) / 256f;

        return sign * (whole + fraction);
    }

    private static uint Read32(ReadOnlySpan<byte> bytes, ref int at)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        at += 4;
        return value;
    }

    private static int Read16(ReadOnlySpan<byte> bytes, ref int at)
    {
        int value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        at += 2;
        return value;
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> bytes, ref int at)
    {
        var value = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(at + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(at + 8)..]));

        at += 12;
        return value;
    }

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);

        return System.Text.Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private FormatParseException Malformed(
        string code, string message, string expected, string actual) =>
        new(new Diagnostic(
            code, DiagnosticSeverity.Error, message, Name, null, expected, actual,
            "See Plan/06-c6-rig-solve.md section 3; all five invariants hold across the corpus."));
}
