using System.Numerics;
using GK3Reborn.Formats.Models;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the smoke off a lit cigarette.
/// </summary>
public sealed class CigaretteSmokeTests
{
    /// <summary>Where the test's cigarette is held.</summary>
    private static readonly Vector3 Held = new(100f, 60f, -40f);

    [Fact]
    public void ARoomWithNoCigaretteInItHasNone()
    {
        var smoke = new CigaretteSmoke(Cigarettes.In([Prop("madbag"), Prop("lbydoor")]));

        Assert.False(smoke.Any);
        Assert.Equal(0, smoke.Count);

        smoke.Advance(0.1f, Vector3.Zero);

        Assert.Empty(smoke.Facing(Vector3.Zero));
    }

    [Fact]
    public void ALitCigaretteGivesOffSmoke()
    {
        PlacedModel lit = Prop("cigarette");
        var smoke = new CigaretteSmoke(Cigarettes.In([lit, Prop("smoke", visible: false)]));

        Assert.True(smoke.Any);

        // Two seconds of a cigarette burning in somebody's hand, a frame at a time.
        for (int frame = 0; frame < 120; frame++)
        {
            smoke.Advance(1f / 60f, Held);
        }

        IReadOnlyList<Particle> puffs = smoke.Facing(Held);

        Assert.NotEmpty(puffs);

        foreach (Particle puff in puffs)
        {
            // Off the cigarette and above it, never far from it: this is a wisp on a person
            // standing in a room, not a fire.
            Assert.InRange(Vector3.Distance(puff.Position, Held), 0f, 120f);
            Assert.True(puff.Position.Y >= Held.Y, "smoke sank below the cigarette.");

            // Thin, and blended rather than additive — smoke greys what is behind it.
            Assert.InRange(puff.Tint.W, 0f, 0.2f);
            Assert.Equal(0f, puff.Shape);
        }
    }

    [Fact]
    public void ABreathOutIsThickerThanTheWispBetweenBreaths()
    {
        float alone = Thickest(exhaling: false);
        float breathing = Thickest(exhaling: true);

        Assert.True(
            breathing > alone * 1.5f,
            $"a breath out was no thicker than the wisp: {breathing} against {alone}.");
    }

    [Fact]
    public void APutOutCigaretteGivesOffNothingNew()
    {
        var smoke = new CigaretteSmoke(
            Cigarettes.In([Prop("cigarette", visible: false), Prop("smoke", visible: false)]));

        for (int frame = 0; frame < 240; frame++)
        {
            smoke.Advance(1f / 60f, Held);
        }

        Assert.Empty(smoke.Facing(Held));
    }

    [Fact]
    public void SmokeAcrossTheRoomCostsNothing()
    {
        var smoke = new CigaretteSmoke(
            Cigarettes.In([Prop("cigarette"), Prop("smoke", visible: false)]));

        for (int frame = 0; frame < 240; frame++)
        {
            smoke.Advance(1f / 60f, Held + new Vector3(4000f, 0f, 0f));
        }

        Assert.Empty(smoke.Facing(Held));
    }

    /// <summary>The most opaque puff two seconds of smoking produces.</summary>
    /// <param name="exhaling">Whether the original's own puff is showing.</param>
    /// <returns>The largest alpha in the air.</returns>
    private static float Thickest(bool exhaling)
    {
        var smoke = new CigaretteSmoke(
            Cigarettes.In([Prop("cigarette"), Prop("smoke", visible: exhaling)]));

        float most = 0f;

        for (int frame = 0; frame < 120; frame++)
        {
            smoke.Advance(1f / 60f, Held);

            foreach (Particle puff in smoke.Facing(Held))
            {
                most = MathF.Max(most, puff.Tint.W);
            }
        }

        return most;
    }

    /// <summary>A prop standing where the cigarette is held.</summary>
    /// <param name="name">The model's name, which is how these are found.</param>
    /// <param name="visible">Whether the scene is drawing it.</param>
    /// <returns>The placed model.</returns>
    private static PlacedModel Prop(string name, bool visible = true)
    {
        var submesh = new ModSubmesh
        {
            TextureName = string.Empty,
            Color = (255, 255, 255),
            Positions = [new Vector3(-4, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 1, 0)],
            Normals = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ],
            TexCoords = new Vector2[3],
            Indices = [0, 1, 2],
        };

        var mesh = new ModMesh
        {
            MeshToLocal = Matrix4x4.CreateTranslation(Held),
            BoundsMin = new Vector3(-4, 0, 0),
            BoundsMax = new Vector3(4, 1, 0),
            Submeshes = [submesh],
        };

        return new PlacedModel(
            name,
            Noun: null,
            Verb: null,
            ModFile.FromMeshes(name, [mesh]),
            Matrix4x4.Identity,
            PlacedModelKind.Prop)
        {
            Visible = visible,
        };
    }
}
