using System.Numerics;
using GK3Reborn.Content;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Game;
using GK3Reborn.Game.Actors;
using GK3Reborn.Rendering;
using GK3Reborn.Sheep;
using Xunit;

namespace GK3Reborn.Tests.Game;

public sealed class MadeleineHallTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opening_room25_after_hall_entry_brings_Madeleine_onto_the_carpet(bool firstPerson)
    {
        string? data = null;
        for (DirectoryInfo? directory = new(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "GK3", "Data");
            if (Directory.Exists(candidate)) { data = candidate; break; }
        }
        Assert.SkipUnless(data is not null, "needs local GK3/Data archives");
        using var archives = GameArchives.Open(data);
        var request = SceneRequest.For("HAL", "310A");
        var api = request.Api!;
        api.State.FirstPerson = firstPerson;
        api.State.SetLocationCount("GABRIEL", "HAL", 2);
        api.State.SetLocationCount("GABRIEL", "WDB", 1);
        api.State.SetTopicCount("LARRY", "T_MANUSCRIPT", 1);
        api.State.SetActorLocation("BUTHANE", "R33");
        api.Animations = new AnimationLibrary(archives);
        var host = new ScriptHost(api);
        host.Scheduler = new SheepScheduler(host.Machine);
        foreach (string name in new[] { "HAL", "HAL_ALL", "HAL310A" })
        {
            host.Add(SheepScriptFile.Parse(archives.Read(name + ".SHP")!, name));
        }
        var sink = new HeadlessSceneSink();
        var scene = new SceneLoader(archives).Load(sink, request, new DiagnosticBag())!;
        var world = new SceneUpdate(scene, api, new Glances(), sink, scripts: host.Scheduler)
        {
            Animations = api.Animations, Clips = new ClipLibrary(archives) { KeepVertices = true },
            Characters = CharacterLibrary.Open(archives),
        };
        SceneScripting.Attach(api, scene, world: world);
        world.Open();
        var runner = new ActionRunner(api);
        Assert.True(runner.Run(scene.Actions!.Find("SCENE", "ENTER")!).Ran);
        for (int frame = 0; frame < 600 && host.Scheduler.Count > 0; frame++)
        {
            world.Advance(1.0 / 60);
        }
        Assert.Equal(0, host.Scheduler.Count);
        Assert.False(scene.Walkable!.IsRegionOpen(225));
        Assert.False(scene.Walkable.IsRegionOpen(241));
        Assert.False(scene.Walkable.IsRegionOpen(226));

        Assert.True(runner.Run(scene.Actions.Find("GABRIELS_DOOR", "OPEN")!).Ran);
        var madeleine = world.ActorNamed("BUTHANE")!;
        bool greetedFromCarpet = false;
        bool passedAlongHall = false;
        for (int frame = 0; frame < 3600; frame++)
        {
            world.Advance(1.0 / 60);
            Vector3 at = madeleine.Standing.Translation;
            if (madeleine.Visible && Vector2.Distance(new(at.X, at.Z), new(175, 355.74f)) < 2)
            {
                greetedFromCarpet = true;
            }
            if (madeleine.Visible && at.X < 130 && at.Z > 350)
            {
                passedAlongHall = true;
            }
        }
        Assert.True(greetedFromCarpet, "Madeleine must leave room 33 before greeting Gabriel.");
        Assert.True(passedAlongHall, "Madeleine must walk along the hall, not behind its walls.");
        Assert.True(api.State.HasScored("e_310a_hal_see_madeline_exit_r33"));
        Assert.Equal(0, host.Scheduler.Count);
        Assert.False(madeleine.Visible);
        Assert.InRange(Vector3.Distance(madeleine.Standing.Translation, new(284.16f, 1.17f, -12.07f)), 0, 1);
        Assert.False(scene.Walkable.IsRegionOpen(225));
        Assert.False(scene.Walkable.IsRegionOpen(241));
        Assert.False(scene.Walkable.IsRegionOpen(226));
    }
}
