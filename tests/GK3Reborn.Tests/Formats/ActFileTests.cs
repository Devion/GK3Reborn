using System.Numerics;
using System.Text;
using GK3Reborn.Formats.Animation;
using GK3Reborn.Foundation.Diagnostics;
using Xunit;
using Clip = GK3Reborn.Tests.Formats.ClipBuilder;

namespace GK3Reborn.Tests.Formats;

/// <summary>
/// Tests for GK3's vertex animation.
/// </summary>
/// <remarks>
/// The five invariants are from <c>Plan/06-c6-rig-solve.md</c> §3.6, which says to use them
/// as reader tests, and all five hold across the corpus. Each one is a way for the reader to
/// have lost its place in the file — after which it is reading noise and would happily
/// carry on, so each is checked rather than assumed.
/// </remarks>
public sealed class ActFileTests
{
    private static ActFile Read(Clip clip, bool vertices = true)
    {
        var bag = new DiagnosticBag();
        ActFile? act = ActFile.Read(clip.Build(), "TEST", bag, vertices);

        Assert.NotNull(act);
        Assert.DoesNotContain(bag.Items, d => d.Severity >= DiagnosticSeverity.Error);

        return act;
    }

    private static string? Refused(Clip clip)
    {
        var bag = new DiagnosticBag();

        Assert.Null(ActFile.Read(clip.Build(), "TEST", bag, vertices: true));

        return bag.Items.Count > 0 ? bag.Items[0].Code : null;
    }

    [Fact]
    public void A_clip_reports_its_model_its_frames_and_its_length()
    {
        ActFile act = Read(new Clip(1, "gra")
            .Frame((0, Clip.Transform(Matrix4x4.Identity)))
            .Frame((0, Clip.Transform(Matrix4x4.Identity))));

        Assert.Equal("gra", act.ModelName);
        Assert.Equal(2, act.FrameCount);
        Assert.Equal(1, act.MeshCount);

        // Fifteen frames a second, the same rate the rest of the game's animation runs at.
        Assert.Equal(2 / 15.0, act.Duration, 6);
    }

    [Fact]
    public void Something_that_is_not_an_animation_is_refused()
    {
        var bag = new DiagnosticBag();

        Assert.Null(ActFile.Read("not an animation"u8, "TEST", bag));
        Assert.Contains(bag.Items, d => d.Code == "GK3R1150");
    }

    [Fact]
    public void A_version_nothing_has_seen_is_refused_rather_than_guessed_at()
    {
        byte[] file = new Clip(1).Frame((0, Clip.Transform(Matrix4x4.Identity))).Build();
        BitConverter.GetBytes(259).CopyTo(file, 4);

        var bag = new DiagnosticBag();

        Assert.Null(ActFile.Read(file, "TEST", bag));
        Assert.Contains(bag.Items, d => d.Code == "GK3R1151");
    }

    [Fact]
    public void A_frame_that_does_not_start_where_the_file_says_is_an_error()
    {
        // Invariant 2, and the one that matters most: a block length misread puts the
        // reader somewhere arbitrary, and everything after it is noise read confidently.
        var clip = new Clip(1) { OffsetDrift = 4 };
        clip.Frame((0, Clip.Transform(Matrix4x4.Identity)));

        Assert.Equal("GK3R1152", Refused(clip));
    }

    [Fact]
    public void Meshes_out_of_order_are_an_error()
    {
        // Invariant 3.
        var clip = new Clip(1) { MeshDrift = 7 };
        clip.Frame((0, Clip.Transform(Matrix4x4.Identity)));

        Assert.Equal("GK3R1153", Refused(clip));
    }

    [Fact]
    public void A_transform_block_of_the_wrong_size_is_an_error()
    {
        // Invariant 4. Built by hand because the helper always writes 48 bytes.
        List<byte> block = [2];
        block.AddRange(BitConverter.GetBytes(36));
        block.AddRange(new byte[36]);

        var clip = new Clip(1);
        clip.Frame((0, [.. block]));

        Assert.Equal("GK3R1156", Refused(clip));
    }

    [Fact]
    public void The_five_byte_trailer_a_fifth_of_the_corpus_has_is_accepted()
    {
        // Invariant 5. 1,201 of the 5,796 files end with these bytes and the reference
        // implementation never noticed them.
        var clip = new Clip(1) { Trailer = [0x01, 0x00, 0x00, 0x00, 0x00] };
        clip.Frame((0, Clip.Transform(Matrix4x4.Identity)));

        Assert.Equal(1, Read(clip).FrameCount);
    }

    [Fact]
    public void Any_other_trailing_bytes_are_an_error()
    {
        var clip = new Clip(1) { Trailer = [0xFF, 0xFF] };
        clip.Frame((0, Clip.Transform(Matrix4x4.Identity)));

        Assert.Equal("GK3R1155", Refused(clip));
    }

    [Fact]
    public void A_mesh_with_nothing_to_say_this_frame_is_legal()
    {
        // byteCount == 0 means the mesh has not moved, and most frames of most clips are
        // mostly that.
        ActFile act = Read(new Clip(2)
            .Frame((0, Clip.Transform(Matrix4x4.Identity)))
            .Frame((1, Clip.Transform(Matrix4x4.Identity))));

        Assert.Equal(2, act.Transforms.Count);
    }

    [Fact]
    public void A_transform_is_read_with_its_bases_as_columns_and_its_handedness_kept()
    {
        // The bases are orthonormal with determinant −1, which is right: GK3 authored a
        // left-handed world. Negating a basis to "fix" it mirrors every character.
        var mirrored = new Matrix4x4(
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, -1, 0,
            5, 6, 7, 1);

        ActFile act = Read(new Clip(1).Frame((0, Clip.Transform(mirrored))));
        Matrix4x4 pose = Assert.Single(act.Transforms).MeshToLocal;

        Assert.Equal(new Vector3(5, 6, 7), pose.Translation);
        Assert.True(pose.GetDeterminant() < 0, "the handedness was flipped on the way in");
    }

    [Fact]
    public void Bounds_are_read()
    {
        ActFile act = Read(new Clip(1)
            .Frame((0, Clip.Bounds(new Vector3(-1, -2, -3), new Vector3(4, 5, 6)))));

        MeshBounds bounds = Assert.Single(act.Bounds);

        Assert.Equal(new Vector3(-1, -2, -3), bounds.Minimum);
        Assert.Equal(new Vector3(4, 5, 6), bounds.Maximum);
    }

    [Fact]
    public void Uncompressed_vertices_are_absolute_positions()
    {
        ActFile act = Read(new Clip(1)
            .Frame((0, Clip.Shape(0, new Vector3(1, 2, 3), new Vector3(4, 5, 6)))));

        VertexPose pose = Assert.Single(act.Vertices);

        Assert.Equal([new Vector3(1, 2, 3), new Vector3(4, 5, 6)], pose.Positions);
        Assert.True(act.Deforms);
    }

    [Fact]
    public void A_clip_of_transforms_alone_is_rigid()
    {
        // 2,188 of the corpus's 5,796 clips. A door, a phone, a go-kart — no skinning
        // needed, which is why they are the part that can be played without a rig.
        Assert.False(Read(new Clip(1).Frame((0, Clip.Transform(Matrix4x4.Identity)))).Deforms);
    }

    [Fact]
    public void An_unchanged_vertex_keeps_the_shape_it_had()
    {
        // Code 0 is 62.2% of the corpus's 92.1 million vertex samples, so this is the
        // common case rather than an edge one.
        ActFile act = Read(new Clip(1)
            .Frame((0, Clip.Shape(0, new Vector3(10, 20, 30))))
            .Frame((0, Clip.Compressed(0, 1, [0], []))));

        Assert.Equal(new Vector3(10, 20, 30), act.Vertices[1].Positions[0]);
    }

    [Fact]
    public void A_one_byte_delta_is_added_to_the_previous_pose()
    {
        // 0x21 is sign +, whole (0x21 & 0x7F) >> 5 == 1, fraction (0x21 & 0x1F) / 32.
        ActFile act = Read(new Clip(1)
            .Frame((0, Clip.Shape(0, new Vector3(10, 0, 0))))
            .Frame((0, Clip.Compressed(0, 1, [1], [0x21, 0x00, 0x00]))));

        Assert.Equal(10f + 1f + (1 / 32f), act.Vertices[1].Positions[0].X, 5);
    }

    [Fact]
    public void A_negative_delta_keeps_its_whole_part()
    {
        // The quirk worth reproducing: the whole part is masked with 0x7F rather than
        // 0x60, so the sign bit survives the mask and is discarded by the shift. Tidying
        // that to 0x60 gives the same answer, which is why it is easy to get almost right.
        ActFile act = Read(new Clip(1)
            .Frame((0, Clip.Shape(0, Vector3.Zero)))
            .Frame((0, Clip.Compressed(0, 1, [1], [0xA1, 0x00, 0x00]))));

        Assert.Equal(-(1f + (1 / 32f)), act.Vertices[1].Positions[0].X, 5);
    }

    [Fact]
    public void A_two_byte_delta_carries_more_of_both_halves()
    {
        // Seven whole bits and eight fractional, for the joints where meshes meet.
        byte[] payload = [.. BitConverter.GetBytes((ushort)0x0280), .. new byte[4]];

        ActFile act = Read(new Clip(1)
            .Frame((0, Clip.Shape(0, Vector3.Zero)))
            .Frame((0, Clip.Compressed(0, 1, [2], payload))));

        Assert.Equal(2f + 0.5f, act.Vertices[1].Positions[0].X, 5);
    }

    [Fact]
    public void Codes_are_read_low_bits_first_within_each_byte()
    {
        // Four vertices to a byte. Reading them the other way round silently swaps which
        // vertex got which delta, which looks like a subtly wrong pose rather than an error.
        ActFile act = Read(new Clip(1)
            .Frame((0, Clip.Shape(0, Vector3.Zero, Vector3.Zero)))
            .Frame((0, Clip.Compressed(0, 2, [0, 1], [0x21, 0x00, 0x00]))));

        Assert.Equal(Vector3.Zero, act.Vertices[1].Positions[0]);
        Assert.Equal(1f + (1 / 32f), act.Vertices[1].Positions[1].X, 5);
    }

    [Fact]
    public void A_pose_is_held_until_the_next_one_is_recorded()
    {
        // The sampling rule: the closest previous recorded pose. A mesh that does not move
        // is simply not written again, so every clip has holes in every mesh's track.
        ActFile act = Read(new Clip(1)
            .Frame((0, Clip.Transform(Matrix4x4.CreateTranslation(1, 0, 0))))
            .Frame()
            .Frame((0, Clip.Transform(Matrix4x4.CreateTranslation(3, 0, 0)))));

        Assert.Equal(1f, act.PoseOf(0, 0)!.Value.Translation.X, 5);
        Assert.Equal(1f, act.PoseOf(0, 1)!.Value.Translation.X, 5);
        Assert.Equal(3f, act.PoseOf(0, 2)!.Value.Translation.X, 5);

        // Past the end holds the last one rather than falling off it.
        Assert.Equal(3f, act.PoseOf(0, 99)!.Value.Translation.X, 5);
    }

    [Fact]
    public void A_mesh_the_clip_never_places_has_no_pose()
    {
        ActFile act = Read(new Clip(2).Frame((0, Clip.Transform(Matrix4x4.Identity))));

        Assert.NotNull(act.PoseOf(0, 0));
        Assert.Null(act.PoseOf(1, 0));
    }

    /// <summary>A basis turned about the vertical and mirrored, as GK3 authors them.</summary>
    /// <remarks>
    /// Every mesh basis in the corpus has a determinant of −1 — the world is left-handed —
    /// so a test that mixes two right-handed bases is not testing anything the game
    /// contains.
    /// </remarks>
    private static Matrix4x4 Spun(float degrees, Vector3 at = default)
    {
        Matrix4x4 basis = Matrix4x4.CreateRotationY(degrees * MathF.PI / 180f);

        basis.M31 = -basis.M31;
        basis.M32 = -basis.M32;
        basis.M33 = -basis.M33;
        basis.Translation = at;

        return basis;
    }

    private static float Determinant(Matrix4x4 basis) => Vector3.Dot(
        Vector3.Cross(
            new Vector3(basis.M11, basis.M12, basis.M13),
            new Vector3(basis.M21, basis.M22, basis.M23)),
        new Vector3(basis.M31, basis.M32, basis.M33));

    [Fact]
    public void A_moment_between_two_recorded_poses_is_the_two_of_them_mixed()
    {
        // Fifteen recorded poses a second against sixty frames on the screen: without this
        // each pose is shown four times over, which on the lobby's fans - six degrees a
        // pose - reads as strobing.
        ActFile act = Read(
            new Clip(1)
                .Frame((0, Clip.Transform(Spun(0, new Vector3(0, 0, 0)))))
                .Frame((0, Clip.Transform(Spun(90, new Vector3(10, 0, 0))))));

        Matrix4x4 half = act.PoseAt(0, 0.5f) ?? default;

        Assert.Equal(5f, half.Translation.X, 3);

        // Halfway round, not halfway across the chord. A component-wise mix of two bases a
        // quarter turn apart is 71% as long, which shrinks the thing it is applied to.
        Assert.Equal(1f, new Vector3(half.M11, half.M12, half.M13).Length(), 3);
        Assert.Equal(45f, MathF.Atan2(-half.M13, half.M11) * 180f / MathF.PI, 2);
    }

    [Fact]
    public void A_mirrored_basis_stays_mirrored_all_the_way_through_the_mix()
    {
        // The whole corpus is mirrored. Decomposing one directly leaves the runtime free to
        // pick a different axis to call negative on each pose, which turns a fan blade
        // inside out between one recorded pose and the next.
        ActFile act = Read(
            new Clip(1)
                .Frame((0, Clip.Transform(Spun(0))))
                .Frame((0, Clip.Transform(Spun(6)))));

        for (float at = 0; at <= 1.001f; at += 0.1f)
        {
            Assert.Equal(-1f, Determinant(act.PoseAt(0, at) ?? default), 3);
        }
    }

    [Fact]
    public void A_clip_that_cycles_runs_its_last_pose_into_its_first()
    {
        // A fan is asked to loop, so the frame after its last is its first. Held instead,
        // it freezes for a fifteenth of a second at the top of every turn.
        ActFile act = Read(
            new Clip(1)
                .Frame((0, Clip.Transform(Spun(0, new Vector3(0, 0, 0)))))
                .Frame((0, Clip.Transform(Spun(0, new Vector3(10, 0, 0))))));

        Assert.Equal(10f, (act.PoseAt(0, 1.5f) ?? default).Translation.X, 3);
        Assert.Equal(5f, (act.PoseAt(0, 1.5f, cycles: true) ?? default).Translation.X, 3);
    }

    [Fact]
    public void A_shape_between_two_recorded_ones_is_the_two_of_them_mixed()
    {
        ActFile act = Read(
            new Clip(1)
                .Frame((0, Clip.Shape(0, new Vector3(0, 0, 0))))
                .Frame((0, Clip.Shape(0, new Vector3(0, 8, 0)))));

        Assert.Equal(2f, Assert.Single(act.ShapeAt(0, 0, 0.25f)!).Y, 3);
    }

    [Fact]
    public void A_mesh_that_is_not_recorded_again_waits_and_then_moves()
    {
        // A mix is only ever between two poses recorded on consecutive frames. A mesh that
        // does not move is not written again, so the gap is a pose held for its length, and
        // sliding across it would set the mesh off the moment the hold began.
        //
        // Reported as Gabriel's right shoe: his walk records it on frames 0 and 4 to 15 and
        // his left on 0 to 5 and 14 to 20 - a foot on the ground does not move, so it is not
        // written - and mixing across those gaps walked each shoe off its own ankle for the
        // stance half of every stride and snapped it back at the far end.
        ActFile act = Read(
            new Clip(2)
                .Frame(
                    (0, Clip.Transform(Spun(0, Vector3.Zero))),
                    (1, Clip.Transform(Spun(0, Vector3.Zero))))
                .Frame((0, Clip.Transform(Spun(0, new Vector3(10, 0, 0)))))
                .Frame(
                    (0, Clip.Transform(Spun(0, new Vector3(20, 0, 0)))),
                    (1, Clip.Transform(Spun(0, new Vector3(20, 0, 0))))));

        Assert.Equal(0f, (act.PoseAt(1, 1f) ?? default).Translation.X, 3);
        Assert.Equal(0f, (act.PoseAt(1, 1.9f) ?? default).Translation.X, 3);
        Assert.Equal(20f, (act.PoseAt(1, 2f) ?? default).Translation.X, 3);

        // The mesh recorded on every frame goes on being mixed as it was, which is what
        // makes the shoe part company with the ankle above it.
        Assert.Equal(5f, (act.PoseAt(0, 0.5f) ?? default).Translation.X, 3);
        Assert.Equal(15f, (act.PoseAt(0, 1.5f) ?? default).Translation.X, 3);
    }

    [Fact]
    public void A_shape_that_is_not_recorded_again_waits_and_then_moves()
    {
        // The same rule, and for the same reason: a submesh whose shape has not changed is
        // not written again either.
        ActFile act = Read(
            new Clip(1)
                .Frame((0, Clip.Shape(0, new Vector3(0, 0, 0))))
                .Frame()
                .Frame((0, Clip.Shape(0, new Vector3(0, 8, 0)))));

        Assert.Equal(0f, Assert.Single(act.ShapeAt(0, 0, 1.5f)!).Y, 3);
        Assert.Equal(8f, Assert.Single(act.ShapeAt(0, 0, 2f)!).Y, 3);
    }

    [Fact]
    public void A_clip_that_cycles_holds_a_mesh_it_stops_recording_early()
    {
        // The wrap is the same step as any other, so it only mixes where the last recorded
        // pose is on the last frame. A stride's planted foot stops being written partway
        // through and must stay where it was put, not set off towards the opening pose.
        ActFile act = Read(
            new Clip(1)
                .Frame((0, Clip.Transform(Spun(0, new Vector3(10, 0, 0)))))
                .Frame()
                .Frame());

        Assert.Equal(10f, (act.PoseAt(0, 2.5f, cycles: true) ?? default).Translation.X, 3);
    }

    [Fact]
    public void Vertices_are_decoded_even_when_they_are_not_kept()
    {
        // They have to be: a compressed frame is a delta against the previous pose, so
        // skipping one makes every later frame of that submesh wrong. What the flag saves
        // is holding them, not reading them.
        ActFile act = Read(
            new Clip(1)
                .Frame((0, Clip.Shape(0, new Vector3(10, 0, 0))))
                .Frame((0, Clip.Compressed(0, 1, [1], [0x21, 0x00, 0x00]))),
            vertices: false);

        Assert.Empty(act.Vertices);
        Assert.True(act.Deforms);
    }
}
