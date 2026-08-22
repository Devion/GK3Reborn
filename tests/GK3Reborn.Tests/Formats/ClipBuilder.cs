using System.Numerics;
using System.Text;
using GK3Reborn.Formats.Animation;

namespace GK3Reborn.Tests.Formats;

/// <summary>Builds an ACT file a block at a time.</summary>
internal sealed class ClipBuilder
{
    private readonly List<List<(int Mesh, List<byte[]> Blocks)>> _frames = [];

    public ClipBuilder(int meshes, string model = "gab")
    {
        Meshes = meshes;
        Model = model;
    }

    public int Meshes { get; }

    public string Model { get; }

    /// <summary>Written into each frame's declared offset, for the invariant test.</summary>
    public int OffsetDrift { get; set; }

    /// <summary>Written in place of the mesh index, for the invariant test.</summary>
    public int? MeshDrift { get; set; }

    public byte[] Trailer { get; set; } = [];

    public ClipBuilder Frame(params (int Mesh, byte[] Block)[] blocks)
    {
        List<(int, List<byte[]>)> frame = [];

        for (int mesh = 0; mesh < Meshes; mesh++)
        {
            int at = mesh;
            frame.Add((mesh, [.. blocks.Where(b => b.Mesh == at).Select(b => b.Block)]));
        }

        _frames.Add(frame);
        return this;
    }

    public static byte[] Transform(Matrix4x4 basis) =>
        Block(2, [.. Floats(
            basis.M11, basis.M12, basis.M13,
            basis.M21, basis.M22, basis.M23,
            basis.M31, basis.M32, basis.M33,
            basis.M41, basis.M42, basis.M43)]);

    public static byte[] Bounds(Vector3 minimum, Vector3 maximum) =>
        Block(3, [.. Floats(
            minimum.X, minimum.Y, minimum.Z, maximum.X, maximum.Y, maximum.Z)]);

    public static byte[] Shape(int submesh, params Vector3[] positions)
    {
        List<byte> body = [.. BitConverter.GetBytes((ushort)submesh)];
        body.AddRange(BitConverter.GetBytes((ushort)positions.Length));

        foreach (Vector3 p in positions)
        {
            body.AddRange(Floats(p.X, p.Y, p.Z));
        }

        return Block(0, [.. body]);
    }

    /// <summary>A compressed shape: one two-bit code a vertex, then the payloads.</summary>
    public static byte[] Compressed(int submesh, int count, int[] codes, byte[] payload)
    {
        List<byte> body = [.. BitConverter.GetBytes((ushort)submesh)];
        body.AddRange(BitConverter.GetBytes((ushort)count));

        byte[] format = new byte[(count / 4) + 1];

        for (int k = 0; k < count; k++)
        {
            format[k / 4] |= (byte)((codes[k] & 0x3) << (2 * (k % 4)));
        }

        body.AddRange(format);
        body.AddRange(payload);

        return Block(1, [.. body]);
    }

    private static byte[] Block(int dataId, byte[] body)
    {
        List<byte> block = [(byte)dataId];
        block.AddRange(BitConverter.GetBytes(body.Length));
        block.AddRange(body);
        return [.. block];
    }

    private static IEnumerable<byte> Floats(params float[] values) =>
        values.SelectMany(BitConverter.GetBytes);

    public byte[] Build()
    {
        // The body first, so the frame offsets can be worked out and written into a
        // header of known size.
        int header = 20 + 32 + (_frames.Count * 4);
        List<byte> body = [];
        List<int> offsets = [];

        foreach (List<(int Mesh, List<byte[]> Blocks)> frame in _frames)
        {
            offsets.Add(header + body.Count + OffsetDrift);

            foreach ((int mesh, List<byte[]> blocks) in frame)
            {
                body.AddRange(BitConverter.GetBytes((ushort)(MeshDrift ?? mesh)));
                body.AddRange(BitConverter.GetBytes(blocks.Sum(b => b.Length)));

                foreach (byte[] block in blocks)
                {
                    body.AddRange(block);
                }
            }
        }

        List<byte> file = [.. "HTCA"u8];
        file.AddRange(BitConverter.GetBytes(ActFile.Version));
        file.AddRange(BitConverter.GetBytes(_frames.Count));
        file.AddRange(BitConverter.GetBytes(Meshes));
        file.AddRange(BitConverter.GetBytes(body.Count));

        byte[] name = new byte[32];
        Encoding.ASCII.GetBytes(Model).CopyTo(name, 0);
        file.AddRange(name);

        foreach (int offset in offsets)
        {
            file.AddRange(BitConverter.GetBytes(offset));
        }

        file.AddRange(body);
        file.AddRange(Trailer);

        return [.. file];
    }
}
