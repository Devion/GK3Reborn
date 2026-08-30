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
    public static uint MaskFor(int part) => part == 0 ? WorldMask : ModelMask;

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
    /// </remarks>
    public static bool FacesBothWays(int part) => part == 0;
}
