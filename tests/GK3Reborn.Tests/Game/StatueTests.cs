using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for standing a sculpted model where GK3 drew a billboard card.
/// </summary>
public sealed class StatueTests
{
    /// <summary>A quad, optionally flagged as a billboard.</summary>
    private static ModFile Card(bool billboard, int quads = 1) =>
        ModFile.FromMeshes("CARD", [Mesh(quads)], billboard);

    /// <summary>A shape: as many quads as asked for, in one submesh.</summary>
    private static ModFile Shape(int quads) =>
        ModFile.FromMeshes("SHAPE", [Mesh(quads)]);

    private static ModMesh Mesh(int quads)
    {
        List<Vector3> positions = [];
        List<ushort> indices = [];

        for (int quad = 0; quad < quads; quad++)
        {
            ushort at = (ushort)positions.Count;

            positions.Add(new Vector3(-9, -21, quad));
            positions.Add(new Vector3(9, -21, quad));
            positions.Add(new Vector3(9, 21, quad));
            positions.Add(new Vector3(-9, 21, quad));

            indices.AddRange([at, (ushort)(at + 1), (ushort)(at + 2)]);
            indices.AddRange([at, (ushort)(at + 2), (ushort)(at + 3)]);
        }

        return new ModMesh
        {
            MeshToLocal = Matrix4x4.Identity,
            BoundsMin = new Vector3(-9, -21, 0),
            BoundsMax = new Vector3(9, 21, quads),
            Submeshes =
            [
                new ModSubmesh
                {
                    TextureName = "CHUROCH",
                    Color = (255, 255, 255),
                    Positions = [.. positions],
                    Normals = [.. Enumerable.Repeat(-Vector3.UnitZ, positions.Count)],
                    TexCoords = [.. Enumerable.Repeat(Vector2.Zero, positions.Count)],
                    Indices = [.. indices],
                },
            ],
        };
    }

    [Fact]
    public void AFlaggedQuadIsACard()
    {
        Assert.True(Statues.IsCard(Card(billboard: true)));
    }

    /// <summary>
    /// The flag is the whole of what makes a card a billboard, and a flat prop without it
    /// is an ordinary flat prop — a poster, a rug, a painted backdrop — which has a facing
    /// the artist chose and must keep it.
    /// </summary>
    [Fact]
    public void AQuadWithoutTheFlagIsNot()
    {
        Assert.False(Statues.IsCard(Card(billboard: false)));
    }

    /// <summary>
    /// A billboard with real geometry on it has already been dealt with — it is a grown
    /// tree or a statue this pass carved on an earlier load — and must not be swapped
    /// again for whatever else shares its name.
    /// </summary>
    [Fact]
    public void AFlaggedShapeIsNotACard()
    {
        Assert.False(Statues.IsCard(Card(billboard: true, quads: 8)));
    }

    [Fact]
    public void NothingIsNotACard()
    {
        Assert.False(Statues.IsCard(null));
    }

    /// <summary>
    /// What the content offers has to be a statue. A file that is itself a card — the
    /// commonest way for this to go wrong, because the card was what was there to begin
    /// with — would swap a billboard for a billboard that no longer turns, which reads as
    /// the statue disappearing again.
    /// </summary>
    [Fact]
    public void ACardIsNotASculpt()
    {
        Assert.False(Statues.IsSculpt(Shape(1)));
    }

    [Fact]
    public void EnoughGeometryIsASculpt()
    {
        Assert.True(Statues.IsSculpt(Shape(Statues.SculptTriangles)));
    }

    [Fact]
    public void NothingIsNotASculpt()
    {
        Assert.False(Statues.IsSculpt(null));
    }

    /// <summary>
    /// The five carved saints are around six thousand triangles and the threshold is
    /// sixty-four, so nothing real is anywhere near the line. The point of the test is
    /// that the line is where a truncated file falls and not where a statue does.
    /// </summary>
    [Fact]
    public void TheThresholdIsFarBelowAnythingReal()
    {
        Assert.True(Statues.SculptTriangles > Statues.CardTriangles * 4);
        Assert.True(Statues.SculptTriangles < 6000);
    }
}
