// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game;
using GK3Reborn.Rendering;
using Xunit;

namespace GK3Reborn.Tests.Game;

/// <summary>
/// Tests for the birds over an outdoor room: which rooms have them, where they fly, and
/// what the blended pass is handed.
/// </summary>
public sealed class BirdTests
{
    private static readonly Timeblock Morning = new(1, 10, IsAfternoon: false);
    private static readonly Timeblock Evening = new(1, 6, IsAfternoon: true);
    private static readonly Timeblock SmallHours = new(2, 2, IsAfternoon: false);

    /// <summary>A box of geometry, as a room's own corners.</summary>
    private static BspFile Room(float span = 2000f, float tall = 300f)
    {
        var vertices = new List<Vector3>();

        // A grid over the floor and a second over the roofline, which is enough for the
        // percentile the roofline is measured with to have something to say.
        for (int x = 0; x <= 20; x++)
        {
            for (int z = 0; z <= 20; z++)
            {
                var at = new Vector3((x - 10) * span / 20f, 0f, (z - 10) * span / 20f);

                vertices.Add(at);
                vertices.Add(at with { Y = tall });
            }
        }

        return BspFile.FromParts(
            "room",
            ["room"],
            [
                new BspSurface
                {
                    ObjectIndex = 0,
                    TextureName = "wall",
                    LightmapUvOffset = Vector2.Zero,
                    LightmapUvScale = Vector2.One,
                    Flags = 0,
                },
            ],
            [new BspPolygon { VertexIndexOffset = 0, VertexIndexCount = 3, SurfaceIndex = 0 }],
            [.. vertices],
            [Vector2.Zero],
            [0, 1, 2]);
    }

    private static SceneCamera Looking(Vector3 at, float yawDegrees, float pitchDegrees) =>
        new(
            "cam",
            at,
            float.DegreesToRadians(yawDegrees),
            float.DegreesToRadians(pitchDegrees),
            IsDefault: true);

    [Fact]
    public void The_village_has_birds_over_it_and_the_hotel_lobby_does_not()
    {
        Assert.True(SceneBirds.For("RC1", Morning).Any);
        Assert.True(SceneBirds.For("POU", Morning).Any);
        Assert.False(SceneBirds.For("LBY", Morning).Any);
        Assert.False(SceneBirds.For("TE5", Morning).Any);
    }

    [Fact]
    public void Nothing_flies_in_the_evening_or_the_small_hours()
    {
        // The same window the sun is placed in, and the window the room's own birdsong
        // plays in: RC1 has RC1BirdAM and RC1BirdAftnoon, and an owl after that.
        Assert.False(SceneBirds.For("RC1", Evening).Any);
        Assert.False(SceneBirds.For("RC1", SmallHours).Any);
        Assert.False(SceneBirds.For("POU", SmallHours).Any);
    }

    [Fact]
    public void A_caller_with_no_hour_gets_the_daylight_answer()
    {
        // The rule SceneFog.For follows, for the reason it gives: a room drawn without an
        // hour is drawn as it is most often seen.
        Assert.True(SceneBirds.For("RC1").Any);
    }

    [Fact]
    public void The_village_and_the_open_country_are_different_birds()
    {
        Flock village = SceneBirds.For("RC1", Morning);
        Flock country = SceneBirds.For("POU", Morning);

        Assert.True(village.Birds > country.Birds);
        Assert.True(village.Wingspan < country.Wingspan);
        Assert.True(village.Speed > country.Speed);

        // Small wings beat faster, which is the one part of this that would look plainly
        // wrong reversed.
        Assert.True(
            BirdFlock.FlapsPerSecond(village.Wingspan) >
            BirdFlock.FlapsPerSecond(country.Wingspan));
    }

    [Fact]
    public void The_flock_flies_over_the_roofs_rather_than_among_them()
    {
        // Reported: "birds are flying too low, going through building geometry in RC3
        // museum". The wheel's middle has to clear whatever stands under it.
        BspFile room = Room(span: 2000f, tall: 520f);

        BirdWheel wheel = SceneBirds.Over(
            SceneBirds.For("RC1", Morning),
            room,
            null,
            [Looking(new Vector3(0f, 60f, -900f), 0f, 0f)]);

        Assert.True(wheel.Centre.Y > 520f, $"the flock flies at {wheel.Centre.Y}");
    }

    [Fact]
    public void Roofs_that_lift_the_flock_also_push_it_further_off()
    {
        // Lifting it and leaving it where it was puts it straight overhead, which is a
        // flock nobody in the room can see. The angle is what is held.
        Vector3 eye = new(0f, 60f, -900f);

        BirdWheel low = SceneBirds.Over(
            SceneBirds.For("RC1", Morning), Room(tall: 100f), null, [Looking(eye, 0f, 0f)]);

        BirdWheel high = SceneBirds.Over(
            SceneBirds.For("RC1", Morning), Room(tall: 520f), null, [Looking(eye, 0f, 0f)]);

        Assert.True(high.Centre.Y > low.Centre.Y);
        Assert.True(high.Radius > low.Radius);

        static float Angle(BirdWheel wheel, Vector3 from) =>
            (wheel.Centre.Y - from.Y) /
            new Vector2(wheel.Centre.X - from.X, wheel.Centre.Z - from.Z).Length();

        // Not held exactly: the push-out is capped, because past seven tenths again the
        // birds are haze. But most of the lift is paid for in distance rather than in
        // angle — left where it was, the taller room would have put the flock at 0.92,
        // which is most of the way to straight overhead.
        Assert.True(Angle(high, eye) < 0.6f, $"the flock sits at {Angle(high, eye)}");
    }

    [Fact]
    public void The_wheel_is_put_in_front_of_the_room_rather_than_around_it()
    {
        // Measured at Poussin's tomb: a wheel on the middle of the walkable ground left
        // five of six birds behind the camera, because a ring around the eye is a ring of
        // which about a fifth is in front.
        var eye = new Vector3(0f, 60f, -900f);

        BirdWheel wheel = SceneBirds.Over(
            SceneBirds.For("RC1", Morning), Room(), null, [Looking(eye, 0f, 0f)]);

        // Yaw nought is +Z, so the room is looked at from the south and the flock has to
        // be north of the camera.
        Assert.True(wheel.Centre.Z > eye.Z, $"the flock is at z={wheel.Centre.Z}");
    }

    [Fact]
    public void A_room_with_no_birds_costs_nothing()
    {
        var flock = new BirdFlock(Flock.None, BirdWheel.Nowhere);

        flock.Advance(1f);

        Assert.Equal(0, flock.Count);
        Assert.Empty(flock.Facing(new Camera()));
    }

    [Fact]
    public void The_same_room_at_the_same_time_is_the_same_flock()
    {
        // Two renders of one room have to be comparable, which is the basis of everything
        // in this project. A flock stepped at sixty frames a second and at thirty has to
        // arrive in the same place after the same two seconds.
        Flock kind = SceneBirds.For("RC1", Morning);
        var wheel = new BirdWheel(new Vector3(100f, 500f, -200f), 900f, 120f);

        var fast = new BirdFlock(kind, wheel);
        var slow = new BirdFlock(kind, wheel);

        for (int i = 0; i < 120; i++)
        {
            fast.Advance(1f / 60f);
        }

        for (int i = 0; i < 60; i++)
        {
            slow.Advance(1f / 30f);
        }

        Camera view = Watching();

        IReadOnlyList<Particle> first = [.. fast.Facing(view)];
        IReadOnlyList<Particle> second = [.. slow.Facing(view)];

        Assert.Equal(first.Count, second.Count);

        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Position.X, second[i].Position.X, 2);
            Assert.Equal(first[i].Position.Y, second[i].Position.Y, 2);
            Assert.Equal(first[i].Position.Z, second[i].Position.Z, 2);
        }
    }

    [Fact]
    public void A_frame_that_took_a_second_does_not_teleport_the_flock()
    {
        // A scene load, a movie, a window dragged. Running the flock through the whole of
        // it costs more than the frame that is late already, so the time is dropped and
        // the birds arrive a little behind where they would have been.
        Flock kind = SceneBirds.For("RC1", Morning);
        var wheel = new BirdWheel(new Vector3(0f, 500f, 0f), 900f, 120f);

        var stalled = new BirdFlock(kind, wheel);
        var steady = new BirdFlock(kind, wheel);

        stalled.Advance(2f);

        // A quarter of a second is the cap, and that is what the long frame is worth.
        for (int i = 0; i < 15; i++)
        {
            steady.Advance(1f / 60f);
        }

        Camera view = Watching();

        IReadOnlyList<Particle> late = [.. stalled.Facing(view)];
        IReadOnlyList<Particle> on = [.. steady.Facing(view)];

        for (int i = 0; i < late.Count; i++)
        {
            Assert.Equal(late[i].Position.X, on[i].Position.X, 2);
            Assert.Equal(late[i].Position.Z, on[i].Position.Z, 2);
        }
    }

    [Fact]
    public void Every_bird_stays_in_its_own_sky()
    {
        // A boid that escapes is a bird in a wall, and the wheel is the only thing that
        // stops one. Two minutes is longer than any camera is held on a room.
        Flock kind = SceneBirds.For("RC1", Morning);
        var wheel = new BirdWheel(new Vector3(100f, 500f, -200f), 900f, 120f);
        var flock = new BirdFlock(kind, wheel);

        for (int i = 0; i < 60 * 120; i++)
        {
            flock.Advance(1f / 60f);
        }

        foreach (Particle bird in flock.Facing(Watching()))
        {
            float out_ = new Vector2(
                bird.Position.X - wheel.Centre.X, bird.Position.Z - wheel.Centre.Z).Length();

            Assert.True(out_ < wheel.Radius * 2f, $"a bird got {out_} out");
            Assert.True(
                MathF.Abs(bird.Position.Y - wheel.Centre.Y) < wheel.Rise * 2.5f,
                $"a bird got to {bird.Position.Y}");
        }
    }

    [Fact]
    public void A_bird_is_handed_to_the_pass_as_a_bird_and_not_as_a_disc()
    {
        var flock = new BirdFlock(
            SceneBirds.For("RC1", Morning),
            new BirdWheel(new Vector3(0f, 500f, 0f), 900f, 120f));

        flock.Advance(1f);

        IReadOnlyList<Particle> drawn = flock.Facing(Watching());

        Assert.NotEmpty(drawn);

        foreach (Particle bird in drawn)
        {
            // The shader's test, and the disc range is everything below it.
            Assert.True(bird.Shape >= 1.5f);

            // The beat, which has to stay inside one whole stroke however long the room
            // has been standing: the fragment stage reads it as a phase.
            Assert.InRange(bird.Shape - Particle.Bird, 0f, 1f);

            // Dark, and covering rather than additive.
            Assert.True(bird.Tint.X < 0.2f);
            Assert.True(bird.Tint.W > 0f);
        }
    }

    [Fact]
    public void Birds_arrive_furthest_from_the_eye_first()
    {
        // One hides what is behind it, so two that overlap have to be blended in order.
        var flock = new BirdFlock(
            SceneBirds.For("POU", Morning),
            new BirdWheel(new Vector3(0f, 500f, 0f), 1400f, 200f));

        flock.Advance(3f);

        Camera view = Watching();
        IReadOnlyList<Particle> drawn = flock.Facing(view);

        for (int i = 1; i < drawn.Count; i++)
        {
            Assert.True(
                Vector3.Distance(drawn[i - 1].Position, view.Position) >=
                Vector3.Distance(drawn[i].Position, view.Position) - 0.01f);
        }
    }

    [Fact]
    public void A_bird_lies_along_its_own_wings()
    {
        // A camera at the origin looking along +Z, so its right is +X and its up is +Y.
        Vector3 right = Vector3.UnitX;
        Vector3 up = Vector3.UnitY;

        // Flying straight at the eye: the whole span is across the frame, level.
        float at = BirdFlock.Turned(-Vector3.UnitZ, 0f, right, up);

        Assert.Equal(0f, MathF.Sin(at), 3);

        // And leaning into a turn takes the wings over with it.
        float leaning = BirdFlock.Turned(-Vector3.UnitZ, 0.6f, right, up);

        Assert.True(MathF.Abs(MathF.Sin(leaning)) > 0.4f);
    }

    [Fact]
    public void A_bird_crossing_the_view_is_not_stood_on_its_wingtip()
    {
        // Its wings point at the eye and project to nothing, so an angle taken from them
        // is noise — which is what the vertical marks in an early screenshot of RC1 were.
        // What is seen there is a bird side-on, and that is a dash.
        float across = BirdFlock.Turned(Vector3.UnitX, 0f, Vector3.UnitX, Vector3.UnitY);

        Assert.True(
            MathF.Abs(MathF.Sin(across)) < 0.2f,
            $"a bird crossing the view is turned {across} radians");
    }

    [Fact]
    public void A_flock_hardly_ever_stands_a_bird_on_end()
    {
        // Steep is allowed and has to be: a banked bird crossing the view really does
        // stand up on the screen, and this flock is banked most of the time because it is
        // going round. What is not allowed is *often*, which is the shape the fault took —
        // before the climb was capped and the degenerate span was noticed, birds sat within
        // a degree of the vertical for whole seconds at a time.
        var flock = new BirdFlock(
            SceneBirds.For("RC1", Morning),
            new BirdWheel(new Vector3(0f, 500f, 0f), 900f, 120f));

        Camera view = Watching();
        int steep = 0;
        int seen = 0;

        for (int frame = 0; frame < 60 * 30; frame++)
        {
            flock.Advance(1f / 60f);

            if (frame % 5 != 0)
            {
                continue;
            }

            foreach (Particle bird in flock.Facing(view))
            {
                seen++;

                // Within ten degrees of straight up and down.
                if (MathF.Abs(MathF.Sin(bird.Spin)) > 0.985f)
                {
                    steep++;
                }
            }
        }

        Assert.True(seen > 100);
        Assert.True(steep < seen / 50, $"{steep} of {seen} birds were stood on end");
    }

    /// <summary>A camera standing where a room's would, looking level along +Z.</summary>
    private static Camera Watching() => new()
    {
        Position = new Vector3(0f, 60f, -1600f),
        Target = new Vector3(0f, 60f, 0f),
        Up = Vector3.UnitY,
    };
}
