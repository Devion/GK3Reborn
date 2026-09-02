// Copyright (C) 2026 the GK3Reborn authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms
// of the GNU General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.

namespace GK3Reborn.Rendering;

/// <summary>
/// How the traced world is divided, and what a ray is allowed to see of it.
/// </summary>
/// <remarks>
/// Two numbers and two rules, stated once because the shaders that read them are written
/// once. Both backends build the same acceleration structure out of the same scene, and a
/// backend that gave its instances a different mask would not fail: the trace stages would
/// go on asking for the room and be handed the characters as well, which reads as a
/// character standing in their own shadow rather than as a mask nobody set.
/// </remarks>
public static class TracedWorld
{
    /// <summary>The room's own geometry, for a ray that wants to skip what stands in it.</summary>
    /// <remarks>
    /// Part zero is the room. Everything else is a model placed in it — a character, a prop —
    /// and a model is the thing a shadow ray must be able to ignore.
    /// </remarks>
    public const uint WorldMask = 0x01;

    /// <summary>The models standing in the room.</summary>
    public const uint ModelMask = 0x02;

    /// <summary>
    /// The room's keyed cards, given a silhouette to cast at load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Room geometry that has to be traced as though it were not.</b> A railing, a fence,
    /// a chain is a picture on a quad with the gaps keyed out, and the structure holds no
    /// alpha and runs no any-hit shader, so keyed geometry has always been left out of it
    /// altogether. <c>CutoutCards</c> now builds an opaque copy of the drawn texels at load
    /// — see <c>ThickCard.Occluders</c> — and this is the mask that copy carries.
    /// </para>
    /// <para>
    /// <b>Why not simply <see cref="WorldMask"/>.</b> The composite credits the room's own
    /// occlusion against the 1999 bake and the two cancel exactly: block a light with room
    /// geometry and <c>residual</c> rises by what <c>arrived</c> lost, which is right, because
    /// the artists' lightmap already holds every shadow the room casts on itself. It does not
    /// hold these. A 1999 bake had no alpha in its ray casts either — a keyed card was baked
    /// as its whole quad or as nothing at all — so a fence's shadow is not in the lightmap
    /// to be double-counted, and putting these in the room's half would cancel the shadow
    /// away as fast as it was traced. They go in the half the composite <em>spends</em>: the
    /// same one a character standing in the room is spent from.
    /// </para>
    /// </remarks>
    public const uint UnbakedMask = 0x04;

    /// <summary>The part every card occluder in a room belongs to.</summary>
    /// <remarks>
    /// Negative because the non-negative numbers are taken and mean something: zero is the
    /// room and the rest are placement indices, which <c>Move</c> and <c>SetTraced</c> are
    /// called with. Nothing moves this one — it is the room's own geometry and the room does
    /// not move — so it needs no number in that sequence, and both backends key their
    /// instances by a dictionary rather than an array, which is what makes -1 a legal part
    /// rather than a clever one.
    /// </remarks>
    public const int CardPart = -1;

    /// <summary>Which mask an instance carries.</summary>
    /// <param name="part">The part key; zero is the room.</param>
    /// <returns>The mask.</returns>
    /// <remarks>
    /// <para>
    /// Split so that a shadow ray leaving a character can trace the room and nothing else.
    /// <b>GK3's people are not solid bodies.</b> A character is a dozen separate meshes — a
    /// shirt shell with a torso inside it, arms passing through sleeves — so a ray leaving
    /// one of them starts inside another, and without the split every character is in their
    /// own shadow from the moment the lights come on.
    /// </para>
    /// <para>
    /// The shaders name these numbers as <c>kRoomOnly</c> and <c>kModelsOnly</c>; see
    /// <c>DenoiserShaders</c>.
    /// </para>
    /// </remarks>
    public static uint MaskFor(int part) => part switch
    {
        0 => WorldMask,
        CardPart => UnbakedMask,
        _ => ModelMask,
    };

    /// <summary>
    /// Whether a part's triangles may <em>not</em> be told apart by which side they are met
    /// from.
    /// </summary>
    /// <param name="part">The part key; zero is the room.</param>
    /// <returns>True where facing culling must be disabled for the whole instance.</returns>
    /// <remarks>
    /// <para>
    /// A model keeps its winding, so a ray may cull the faces it meets from within. That is
    /// what lets a character shadow itself: a person is a stack of overlapping shells and
    /// the only thing separating "this shell is around me" from "this arm is in my light" is
    /// which side of the triangle the ray arrives at. See the trace stage's
    /// <c>kSkipShells</c>.
    /// </para>
    /// <para>
    /// The room does not, and nothing asks it to. A BSP's polygons carry no consistent
    /// winding — each triangle is given its own plane's normal at load, which is exactly the
    /// admission that the file does not say — so a room triangle's two sides are not
    /// distinguishable and disabling the test is the honest reading. Every ray that traces
    /// the room today asks for no culling anyway, so this changes nothing for it.
    /// </para>
    /// <para>
    /// Nor do the card occluders, and for them it is load-bearing rather than academic. They
    /// are single-sided patches lying on the plane of a card, so which way they face is
    /// whichever way the artist happened to wind the quad they were fitted to — and the ray
    /// that most needs to hit one is a shadow ray leaving a character, which asks for
    /// <c>kSkipShells</c> and would cull half the fences in the game. An instance that
    /// disables the test overrides the ray's flag, which is exactly what is wanted here:
    /// the shells stay skipped and the fence still stops the light.
    /// </para>
    /// </remarks>
    public static bool FacesBothWays(int part) => part <= 0;

    /// <summary>Whether a part's vertices may be rewritten after the structure is built.</summary>
    /// <param name="part">The part key.</param>
    /// <returns>True for the things a clip or a walk can reshape.</returns>
    /// <remarks>
    /// Models only. The room's geometry is built once and never touched again, and so are
    /// the card occluders — a railing does not animate, and the one that swings is a door,
    /// which is a model. A part built where the host can write it costs memory that stays
    /// mapped for the life of the room.
    /// </remarks>
    public static bool Posable(int part) => part > 0;
}
