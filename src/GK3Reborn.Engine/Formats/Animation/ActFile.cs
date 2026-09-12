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
public sealed class ActFile
{
    /// <summary>The only version the corpus contains.</summary>
    public const int Version = 258;

    /// <summary>The five bytes a fifth of the corpus ends with, which nothing reads.</summary>
    private static ReadOnlySpan<byte> Trailer => [0x01, 0x00, 0x00, 0x00, 0x00];

    private readonly Dictionary<int, List<MeshPose>> _transforms = [];
    private readonly Dictionary<(int Mesh, int Submesh), List<VertexPose>> _shapes = [];

    /// <summary>
    /// Whether each shaped submesh's last recorded pose runs back into its first. Worked
    /// out on demand and kept, because it is a walk over every vertex of every frame.
    /// </summary>
    private readonly Dictionary<(int Mesh, int Submesh), bool> _closes = [];

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

        (int next, float span) = Next(
            poses.Count,
            previous,
            poses[previous].Frame,
            i => poses[i].Frame,
            cycles && Closes(mesh, submesh, poses));

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
    private (int To, float Span) Next(
        int count, int previous, int at, Func<int, int> frameOf, bool cycles)
    {
        if (previous + 1 < count)
        {
            return frameOf(previous + 1) == at + 1 ? (previous + 1, 1f) : (-1, 0);
        }

        return cycles && count > 1 && at == FrameCount - 1 ? (0, 1f) : (-1, 0);
    }

    /// <summary>
    /// How much bigger than an ordinary step the wrap may be and still be one.
    /// </summary>
    private const float SeamSlack = 2f;

    /// <summary>
    /// Whether a shaped submesh's last recorded pose runs straight back into its first.
    /// </summary>
    /// <param name="mesh">Which mesh group.</param>
    /// <param name="submesh">Which submesh within it.</param>
    /// <param name="poses">Its recorded shapes, in frame order.</param>
    /// <returns>True when the two ends may be interpolated together.</returns>
    private bool Closes(int mesh, int submesh, List<VertexPose> poses)
    {
        if (_closes.TryGetValue((mesh, submesh), out bool already))
        {
            return already;
        }

        bool closes = Seamless(poses);
        _closes[(mesh, submesh)] = closes;

        return closes;
    }

    /// <summary>Whether the wrap of a track of shapes is no bigger than a step within it.</summary>
    private static bool Seamless(List<VertexPose> poses)
    {
        // A track the whole of the clip is not recorded on has holes in it, and the frame
        // after the last one is not the first: nothing is interpolated across the wrap
        // anyway, so the answer does not matter and this is the cheap one.
        if (poses.Count < 3)
        {
            return true;
        }

        float widest = 0;

        for (int i = 1; i < poses.Count; i++)
        {
            widest = Math.Max(widest, Apart(poses[i - 1].Positions, poses[i].Positions));
        }

        float wrap = Apart(poses[^1].Positions, poses[0].Positions);

        // A track that does not move at all: every step is nought and so is the wrap, and
        // nought is not more than nought times anything.
        return wrap <= widest * SeamSlack;
    }

    /// <summary>How far a shape moves, as the average distance its vertices travel.</summary>
    private static float Apart(IReadOnlyList<Vector3> from, IReadOnlyList<Vector3> to)
    {
        int count = Math.Min(from.Count, to.Count);

        if (count == 0)
        {
            return 0;
        }

        float total = 0;

        for (int i = 0; i < count; i++)
        {
            total += Vector3.Distance(from[i], to[i]);
        }

        return total / count;
    }

    /// <summary>Mixes two mesh transforms, turning the shorter way round.</summary>
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
    private static Matrix4x4 Basis(Vector3 i, Vector3 j, Vector3 k, Vector3 position) => new(
        i.X, i.Y, i.Z, 0,
        j.X, j.Y, j.Z, 0,
        k.X, k.Y, k.Z, 0,
        position.X, position.Y, position.Z, 1);

    /// <summary>
    /// Decodes a one-byte delta.
    /// </summary>
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
