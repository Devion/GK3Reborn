// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

using System.Numerics;
using GK3Reborn.Formats.Scenes;
using GK3Reborn.Game.Navigation;

namespace GK3Reborn.Game;

/// <summary>What is flying over a room, and what it is like.</summary>
/// <param name="Birds">How many are up.</param>
/// <param name="Clearance">
/// The least the middle of the wheel will fly above the room's roofline, in world units.
/// A floor rather than the height itself: how high the flock actually goes is measured off
/// the room's own cameras, and this is only what stops that answer putting birds through a
/// roof. Off the roofline rather than off the highest point of the room, because RC1's
/// highest point is the Tour Magdala and the birds are meant to go past it rather than over
/// it. See <see cref="SceneBirds.Over"/>.
/// </param>
/// <param name="Spread">
/// How wide the wheel is, as a fraction of the room's own half-diagonal. A fraction rather
/// than a distance: the same wheel in RC1 and in the cemetery is two thirds of a village and
/// the whole of a walled yard, and it is the fraction that is the same thing in both.
/// </param>
/// <param name="Rise">
/// How far a bird climbs and falls within it, in world units. Absolute, unlike the spread:
/// how high a bird goes is a fact about the bird and not about how big the square is.
/// </param>
/// <param name="Wingspan">Tip to tip, in world units. A character in this game is 70 tall.</param>
/// <param name="Speed">How fast one flies, in world units a second.</param>
/// <param name="Turning">
/// How hard the flock wheels about its own middle, from nought for a loose mill to one for
/// a tight circle. It is the single number that decides whether a flock reads as birds or
/// as insects: real birds over a village go round.
/// </param>
public readonly record struct Flock(
    int Birds, float Clearance, float Spread, float Rise, float Wingspan, float Speed, float Turning)
{
    /// <summary>Nothing is flying.</summary>
    public static Flock None { get; }

    /// <summary>Whether anything is.</summary>
    public bool Any => Birds > 0;
}

/// <summary>Where a room's flock wheels, measured from the room.</summary>
/// <param name="Centre">The middle of the wheel, in world space.</param>
/// <param name="Radius">How far out from it a bird flies, in world units.</param>
/// <param name="Rise">How far above and below it one climbs, in world units.</param>
public readonly record struct BirdWheel(Vector3 Centre, float Radius, float Rise)
{
    /// <summary>No wheel, for a room with no birds or nothing to measure.</summary>
    public static BirdWheel Nowhere { get; }
}

/// <summary>Where a room's own cameras say the sky is.</summary>
/// <param name="At">The point to put the middle of the flock at, in world space.</param>
/// <param name="Eye">
/// How high the cameras that said so are standing. Wanted because the angle a bird is seen
/// at is measured from the eye, and a room whose cameras stand on a tower has an eye a long
/// way off the ground.
/// </param>
public readonly record struct BirdAim(Vector3 At, float Eye);

/// <summary>
/// Which rooms have birds in the sky over them, and what kind.
/// </summary>
/// <remarks>
/// <para>
/// <b>A table, for the reason <see cref="SceneFog"/> is one.</b> Nothing in GK3's data says
/// where a bird would be. The scene files name a sky, a floor and a light rig and no living
/// thing that is not a character; no texture implies one; and no measurement of the geometry
/// tells a courtyard somebody would sit out in from a courtyard nobody would. What decides
/// that a place has birds over it is what the place <em>is</em>, which is a reading of the
/// game rather than a property of its files.
/// </para>
/// <para>
/// <b>The game does say when, though, and it says it out loud.</b> Five of RC1's, RC2's and
/// the cemetery's ambient soundtracks are birdsong — <c>RC1BIRDAM.STK</c> plays
/// <c>RCBirdAM</c> every ten to sixty seconds through the morning blocks and
/// <c>RC1BIRDAFTNOON.STK</c> three more through the afternoon — and the evening and the
/// small hours get <c>RC1OWL.STK</c> and <c>RC1CRICKETS.STK</c> instead. The artists put
/// birds over these rooms in 1999 and could only afford to do it in sound. So the hours are
/// not a judgement: this puts something in the sky at the hours the room is already singing,
/// and nothing at the hours it hoots.
/// </para>
/// <para>
/// <b>Deliberately short.</b> Fifty-one of the game's scene assets name a daylight sky, the
/// hotel bedrooms and the museum among them, and a bird over a room whose sky is a painting
/// seen through one window is a bird nobody will ever see being simulated all afternoon.
/// The list below is the rooms whose sky is most of the picture, each one looked at. Adding
/// another is one line, and the line should be written by somebody who has just rendered the
/// room.
/// </para>
/// <para>
/// <b>Two kinds, and the difference is the place rather than the species.</b> Over the
/// village the birds are small, fast, close in and tightly bunched — swifts round the
/// rooftops, which is what a French hill village sounds and looks like in summer, and what
/// <c>RCBird1Aftnoon</c> is a recording of. Over open country they are large, slow, far off
/// and few — the soaring birds that hang over a hillside for an hour at a time. The same
/// numbers in both places give a village full of buzzards or a valley full of gnats.
/// </para>
/// </remarks>
public static class SceneBirds
{
    /// <summary>
    /// Swifts round the rooftops: the village, its cemetery and the tower above it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Small, and bigger than a swift.</b> Thirty units is three quarters of a metre at
    /// the scale this game stands its characters at, which is a swift and a half. It is a
    /// legibility figure rather than an ornithological one: a real swift's span over RC1,
    /// at the distance the flock has to fly to clear the Tour Magdala, is five pixels of a
    /// 720-line frame, and five pixels of anything is a speck of dirt.
    /// </para>
    /// <para>
    /// <b>Height is the thing this gets wrong first, in both directions.</b> The first
    /// attempt sat the whole flock 45 degrees above a camera whose frame stops at 27, and
    /// RC1 was a village with nothing over it. The second put it under the roofs, and was
    /// reported as birds going through the buildings in RC3. What is wanted is between
    /// about ten and twenty-five degrees up, over whatever is under it — which is why the
    /// clearance below is a floor and not the height, and why the wheel moves out when the
    /// floor lifts it. See <see cref="Over"/>.
    /// </para>
    /// <para>
    /// <b>Fast and tight.</b> Swifts do not drift about; they go round the same three
    /// rooftops at speed, all together, screaming. The turning figure is what carries that,
    /// and it is most of the difference between this and the other entry below.
    /// </para>
    /// </remarks>
    private static readonly Flock Rooftops = new(
        Birds: 14,
        Clearance: 120f,
        Spread: 0.34f,
        Rise: 120f,
        Wingspan: 30f,
        Speed: 290f,
        Turning: 0.8f);

    /// <summary>
    /// Soaring birds over open country: the tomb, the dig, and the two hilltops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Few, large and unhurried.</b> Six birds at a metre and a half across, going round
    /// slowly on a thermal. A dozen at this size is a kill rather than a landscape, and
    /// they have to be this large to read at all: they fly further off than the village's
    /// do, because there is nothing out there to make them come close.
    /// </para>
    /// <para>
    /// <b>The wheel is wide because the country is.</b> Over half the room's own
    /// half-diagonal, which puts the far side of it out past whatever the room is standing
    /// on — where a bird over a hillside is. The tomb looks out over most of a kilometre
    /// and its birds should not all be directly overhead.
    /// </para>
    /// </remarks>
    private static readonly Flock OpenCountry = new(
        Birds: 6,
        Clearance: 100f,
        Spread: 0.55f,
        Rise: 240f,
        Wingspan: 62f,
        Speed: 170f,
        Turning: 0.62f);

    /// <summary>What is flying over a room at a point in the story.</summary>
    /// <param name="scene">The scene's name, as the SIF has it.</param>
    /// <param name="when">
    /// Where the story stands. A caller with no story state gets the daylight answer, for
    /// the reason <see cref="SceneFog.For"/> gives: a room drawn without an hour is drawn
    /// as it is most often seen.
    /// </param>
    /// <returns>The flock, or <see cref="Flock.None"/> for a room or an hour with none.</returns>
    public static Flock For(string? scene, Timeblock? when = null)
    {
        if (scene is null || when is { } hour && !IsDaylight(hour))
        {
            return Flock.None;
        }

        return scene switch
        {
            _ when Named(scene, Village) => Rooftops,
            _ when Named(scene, Country) => OpenCountry,
            _ => Flock.None,
        };
    }

    /// <summary>Whether birds are up at an hour of the story.</summary>
    /// <param name="when">The point in the story.</param>
    /// <returns>True through the day, false in the evening and the small hours.</returns>
    /// <remarks>
    /// <para>
    /// The same window <see cref="Sunlight"/> places a sun in, and it has to be: a bird in
    /// the sky of a room lit for dusk is lit by a sun that is not there. Seven in the
    /// morning to six in the evening covers sixteen of the game's seventeen blocks; the
    /// seventeenth is <c>309P</c> and reaches two hotel bedrooms.
    /// </para>
    /// <para>
    /// It is also what the art says. Every daylight asset in the corpus is painted against
    /// a sky named <c>_M</c> or <c>_A</c> and every other against <c>_E</c> or <c>_N</c>,
    /// and those skies are measurably different things: <c>RLC_M</c> and <c>RLC_A</c>
    /// average 204 and 214 over their upper face, <c>RLC_E</c> 55 and <c>RLC_N</c> 21.
    /// </para>
    /// </remarks>
    public static bool IsDaylight(Timeblock when)
    {
        int hour = (when.IsAfternoon && when.Hour != 12 ? when.Hour + 12 : when.Hour) % 24;

        return hour is >= 7 and < 18;
    }

    /// <summary>The village rooms, where the birds are swifts.</summary>
    /// <remarks>
    /// RC1 to RC4 are the four streets of Rennes-le-Château, MAG the square below the Tour
    /// Magdala, MA3 the tower's own lookout, and CEM the walled cemetery beside the church.
    /// Every one of them is roofed on some sides and open above, which is the shape this
    /// flock is for.
    /// </remarks>
    public static IReadOnlyList<string> Village { get; } =
        ["RC1", "RC2", "RC3", "RC4", "MAG", "MA3", "CEM"];

    /// <summary>The open ones, where they are soaring birds.</summary>
    /// <remarks>
    /// <para>
    /// POU is Poussin's tomb on its hillside, WOD Lady Howard and Estelle's dig, CD1 the
    /// ruin on top of Blanchefort and MCF the site on Mount Cardou. All four look out over
    /// a valley with nothing in the way, and three of them are on a summit.
    /// </para>
    /// <para>
    /// <b>Coume Sourde and L'Ermitage are two places apiece.</b> The driving map's own
    /// names for PL2 and PL4 are "Coume Sourde" and "L'Ermitage", and CSD and LER are the
    /// ground the player walks on to after parking at them — the ruins under the cliff and
    /// the hermit's cave. Adding the destination without the roadside, or the other way
    /// round, would put birds over one half of a place and take them away fifty metres
    /// later, which is a thing the player would notice crossing between the two.
    /// </para>
    /// <para>
    /// All four are hillside with the sky standing over a low horizon, which is what the
    /// soaring flock is for; none of them is roofed, so none wants the village's swifts.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Country { get; } =
        ["POU", "WOD", "CD1", "MCF", "CSD", "PL2", "LER", "PL4"];

    private static bool Named(string scene, IReadOnlyList<string> rooms)
    {
        for (int i = 0; i < rooms.Count; i++)
        {
            if (scene.Equals(rooms[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Where a room's flock wheels, measured from the room itself.</summary>
    /// <param name="flock">What is flying, from <see cref="For"/>.</param>
    /// <param name="room">The room's geometry, as the BSP has it.</param>
    /// <param name="walkable">Its walk boundary, where it has one.</param>
    /// <param name="cameras">
    /// The room's own fixed cameras, which decide which part of the sky is ever looked at.
    /// See <see cref="Framed"/>, which is what makes the difference between a flock and an
    /// empty sky.
    /// </param>
    /// <returns>
    /// The wheel, or <see cref="BirdWheel.Nowhere"/> when there are no birds or nothing to
    /// measure them against.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Derived rather than tabled, which is the opposite of the decision above and for a
    /// reason: <em>whether</em> a place has birds over it is a reading of the game, and
    /// <em>where</em> the sky is over that place is a measurement of it. A height written
    /// down in the table would be a number nobody could check without loading the room, and
    /// it would be wrong the first time the room's geometry was improved under it.
    /// </para>
    /// <para>
    /// <b>The room's own corners, not the scene's.</b> The scene's box has every placed
    /// model in it — a van, a suitcase, a hotel sign hung out over the street — and the
    /// question here is how high the <em>buildings</em> go. The same distinction
    /// <see cref="Sunlight"/> makes, for the same reason.
    /// </para>
    /// <para>
    /// <b>And the middle of the open ground, not of the box.</b> RC1's box is centred inside
    /// a building: the street is an L, and the middle of a bounding box round an L is the
    /// corner that is not in it. Where the player can walk is where the room is open, which
    /// is where the sky is.
    /// </para>
    /// </remarks>
    public static BirdWheel Over(
        Flock flock,
        BspFile? room,
        WalkBoundary? walkable,
        IReadOnlyList<SceneCamera>? cameras = null)
    {
        if (!flock.Any || room is not { Vertices.Length: > 0 })
        {
            return BirdWheel.Nowhere;
        }

        Vector3 low = room.Vertices[0];
        Vector3 high = low;

        foreach (Vector3 corner in room.Vertices)
        {
            low = Vector3.Min(low, corner);
            high = Vector3.Max(high, corner);
        }

        // How far a bird may get from the middle. Off the room's own half-diagonal, so that
        // the wheel is the same shape in a village and in a walled yard, and never smaller
        // than a flock can actually fly in: fourteen birds three wingspans apart need room.
        var across = new Vector2(high.X - low.X, high.Z - low.Z);

        float reach = MathF.Max(
            across.Length() * 0.5f * flock.Spread, flock.Wingspan * flock.Birds * 1.5f);

        // Where the room is looking, and how high above the roofs the flock has to be to
        // be over them rather than among them. The two argue, and the way they are settled
        // is the whole of what follows.
        BirdAim? aimed = Framed(cameras, reach);

        if (aimed is not { } first)
        {
            Vector3 anywhere = Middle(walkable) ?? ((low + high) / 2f);

            return new BirdWheel(
                new Vector3(
                    anywhere.X,
                    Roofline(room, anywhere, reach) + flock.Clearance,
                    anywhere.Z),
                reach,
                flock.Rise);
        }

        float floor = Roofline(room, first.At, reach) + flock.Clearance;

        // **Lifting the flock without moving it out puts it straight overhead.** The aim
        // above stands the birds a fixed part of the way up the frame, which is the right
        // angle; raising them to clear a roof and leaving them where they were turns that
        // angle into whatever it happens to become — measured, seven times the height of
        // the frame over the Tour Magdala's square, and nothing in the shot at all. So the
        // wheel is pushed out by however much the roofs pushed the flock up, and the angle
        // is what is preserved.
        if (floor > first.At.Y)
        {
            float wanted = MathF.Max(floor - first.Eye, 1f);
            float had = MathF.Max(first.At.Y - first.Eye, 1f);

            // And no further than seven tenths again. The angle is what this is trying to
            // hold, but distance is what it costs: at two and a half times, RC1's flock
            // cleared the Tour Magdala from two thousand six hundred units away and every
            // bird in it was six pixels of haze. Seventeen hundred is twelve pixels and
            // twenty-two degrees up, which is in the shot and legible in it, and that is
            // the trade the whole of this is for.
            reach *= Math.Clamp(wanted / had, 1f, 1.7f);
        }

        BirdAim aim = Framed(cameras, reach) ?? first;
        float over = Roofline(room, aim.At, reach) + flock.Clearance;

        return new BirdWheel(
            new Vector3(aim.At.X, MathF.Max(aim.At.Y, over), aim.At.Z),
            reach,
            flock.Rise);
    }

    /// <summary>Half the vertical angle a GK3 camera sees, in radians.</summary>
    /// <remarks>
    /// The original renders at sixty degrees vertically on a 4:3 screen, and this port keeps
    /// that; see <c>SceneLoader.CameraAt</c>, which is where the number is set.
    /// </remarks>
    private const float HalfView = MathF.PI / 6f;

    /// <summary>How far up the frame the flock is aimed, as a fraction of that.</summary>
    /// <remarks>
    /// A third of the way from the middle of the shot to its top edge, which is high enough
    /// to be over whatever the shot is of and low enough to stay in it. At two thirds the
    /// birds sit against the top border and are cut in half by it every time one climbs.
    /// </remarks>
    private const float Lift = 0.35f;

    /// <summary>The part of the sky a room's own cameras are pointed at.</summary>
    /// <param name="cameras">The room's cameras, from the scene file.</param>
    /// <param name="reach">How far in front of them to measure, in world units.</param>
    /// <returns>Where to put the flock, or null where the room names no cameras.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the difference between birds and no birds, and it is not obvious.</b> The
    /// player never moves the camera in this game: a room is five or six fixed shots and
    /// they point where the artists pointed them. A wheel centred on the middle of the
    /// walkable ground surrounds the shot instead of standing in it, and a ring around the
    /// eye is a ring of which about a fifth is in front — so at Poussin's tomb five of six
    /// birds were <em>behind</em> the camera and the sixth was off the side of the frame,
    /// measured, for every frame of a five-hundred-frame run.
    /// </para>
    /// <para>
    /// So the wheel is put one of its own radii along the way the room looks, which stands
    /// the camera on the near edge of it and leaves the whole far half in the shot. Where
    /// the cameras disagree — a square with shots all round it — the average comes back to
    /// the middle of the square, which is the answer that case wanted anyway.
    /// </para>
    /// <para>
    /// <b>Flattened, and then lifted by a fixed part of the frame rather than by the
    /// camera's own pitch.</b> Following the pitch would put the flock in the middle of
    /// every shot, ground included. Aiming a third of the way from the middle of the frame
    /// to its top puts the birds in the band of sky each shot actually contains, which is
    /// the difference between the tomb's arrival camera — pitched twelve degrees down at a
    /// road — showing three birds and showing none.
    /// </para>
    /// </remarks>
    public static BirdAim? Framed(IReadOnlyList<SceneCamera>? cameras, float reach)
    {
        if (cameras is not { Count: > 0 })
        {
            return null;
        }

        var total = Vector3.Zero;
        float eye = 0f;
        float counted = 0f;

        foreach (SceneCamera camera in cameras)
        {
            Vector3 forward = camera.Forward;
            var flat = new Vector3(forward.X, 0f, forward.Z);

            if (flat.LengthSquared() < 1e-6f)
            {
                continue;
            }

            // And how high, so that the flock lands in the sky each shot actually
            // contains rather than above the top of it. A camera pointed at the ground has
            // its sky in a band just under its top edge, and that is where this puts them.
            float elevation = MathF.Atan2(forward.Y, flat.Length()) + (HalfView * Lift);

            // The shot the room opens on counts for three of the others. It is the one
            // every player sees and the only one some of them ever see, and at Poussin's
            // tomb the plain average of four cameras pointing four ways came back to a
            // patch of sky the arrival shot has its back to.
            float weight = camera.IsDefault ? 3f : 1f;

            total += weight * (camera.Position
                + (Vector3.Normalize(flat) * reach)
                + (Vector3.UnitY * reach * MathF.Tan(Math.Clamp(elevation, -0.6f, 0.9f))));

            eye += weight * camera.Position.Y;
            counted += weight;
        }

        return counted > 0f ? new BirdAim(total / counted, eye / counted) : null;
    }

    /// <summary>How high the roofs stand under a point in the sky.</summary>
    /// <param name="room">The room's geometry.</param>
    /// <param name="over">The middle of the wheel, in world space; only X and Z are read.</param>
    /// <param name="reach">How far out from it the flock flies, in world units.</param>
    /// <returns>The height a bird has to be above to be clear of the buildings there.</returns>
    /// <remarks>
    /// <para>
    /// <b>Under the flock rather than over the room, and that is the whole point.</b>
    /// Reported: <em>"birds are flying too low, going through building geometry in RC3
    /// museum"</em>. A single roofline for a whole village is a number that is right in the
    /// square and wrong in the lane: RC1's flock flies over an open square whose roofs are
    /// about 250 and RC3's over a walled street whose sides run past 500, and the room's
    /// own average is what put the second flock among the walls.
    /// </para>
    /// <para>
    /// <b>The tallest thing there, near enough.</b> A percentile a half-percent off the top
    /// of what is inside the wheel: high enough that a bird clears the roofs it is flying
    /// over, and not the outright maximum, because one aerial or one lightning conductor
    /// should not lift a whole flock by ten metres. Where the flock is aimed out over open
    /// country and there is nothing under it at all, the room's own corners answer instead.
    /// </para>
    /// </remarks>
    public static float Roofline(BspFile? room, Vector3 over, float reach)
    {
        if (room is not { Vertices.Length: > 0 })
        {
            return 0f;
        }

        float square = reach * reach;
        var under = new List<float>(room.Vertices.Length / 4);

        foreach (Vector3 corner in room.Vertices)
        {
            float x = corner.X - over.X;
            float z = corner.Z - over.Z;

            if ((x * x) + (z * z) <= square)
            {
                under.Add(corner.Y);
            }
        }

        // Nothing beneath it — the flock is out over a valley — so there is nothing to
        // clear and the room's own skyline is the honest fallback.
        return Percentile(under.Count > 64 ? under : Heights(room), 0.995f);
    }

    /// <summary>Every corner's height, for a flock with nothing under it.</summary>
    private static List<float> Heights(BspFile room)
    {
        var all = new List<float>(room.Vertices.Length);

        foreach (Vector3 corner in room.Vertices)
        {
            all.Add(corner.Y);
        }

        return all;
    }

    private static float Percentile(List<float> heights, float at)
    {
        if (heights.Count == 0)
        {
            return 0f;
        }

        heights.Sort();

        return heights[Math.Min(heights.Count - 1, (int)(heights.Count * at))];
    }

    /// <summary>The middle of the ground the player can walk on.</summary>
    /// <param name="walkable">The room's walk boundary, or null.</param>
    /// <returns>The point, with its height left at nought, or null where there is none.</returns>
    public static Vector3? Middle(WalkBoundary? walkable)
    {
        if (walkable is null)
        {
            return null;
        }

        var total = Vector3.Zero;
        int open = 0;

        for (int y = 0; y < walkable.Height; y++)
        {
            for (int x = 0; x < walkable.Width; x++)
            {
                if (!walkable.IsTexelWalkable(x, y))
                {
                    continue;
                }

                total += walkable.ToWorld(x, y);
                open++;
            }
        }

        return open > 0 ? total / open : null;
    }
}
