using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Navigation;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class MuseumStairsTests
{
    [Fact]
    public void Retail_stairs_reach_the_envelope_landing_and_return_to_the_street()
    {
        string? data = null;
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "GK3", "Data");
            if (Directory.Exists(candidate))
            {
                data = candidate;
                break;
            }
        }
        Assert.SkipUnless(data is not null, "needs local GK3/Data archives");
        using var archives = GameArchives.Open(data);
        var scene = new SceneLoader(archives).Load(new HeadlessSceneSink(), SceneRequest.For("RC2", "205P"), new DiagnosticBag())!;
        var stairs = WalkFloor.From(scene.Geometry, "rc2_museumsteps")!;
        Vector3 start = new(1888.67f, 44.93f, -3288.25f);
        var player = new FirstPerson
        {
            Position = start,
            CanStand = scene.Walkable!.IsWalkable,
            Ground = scene.Ground!.Height,
            Stairs = stairs.Height,
        };
        void Walk(float yaw, int frames)
        {
            player.Yaw = yaw;
            for (int i = 0; i < frames; i++)
            {
                player.Advance(new FirstPersonInput(new Vector2(0, 1), Vector2.Zero, false), 1f / 60);
            }
        }
        Walk(MathF.PI / 2, 90);
        Assert.InRange(player.Position.X, 2128, 2130);
        Assert.InRange(player.Position.Y, 146, 147);
        Assert.False(scene.Walkable.IsWalkable(player.Position));
        Walk(MathF.PI, 20);
        Assert.InRange(player.Position.Z, -3342, -3341);
        Assert.InRange(player.Position.Y, 146, 147);
        Walk(0, 20);
        Walk(-MathF.PI / 2, 90);
        Assert.InRange(Vector3.Distance(start, player.Position), 0, 0.1f);
    }
}
