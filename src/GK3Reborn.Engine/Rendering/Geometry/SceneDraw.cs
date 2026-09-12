using System.Numerics;
using System.Runtime.InteropServices;

namespace GK3Reborn.Rendering.Geometry;

/// <summary>What the shader needs to know that changes from one draw to the next.</summary>
/// <param name="Model">Where this batch stands in the world.</param>
/// <param name="PreviousModel">
/// Where it stood last frame. Half of a motion vector: where a point that is here now
/// would have been on the screen a frame ago.
/// </param>
/// <param name="Shading">
/// Whether to read the lightmap, what to multiply it by, four flags packed into one number
/// — one for self-lit, two for a model standing in the room, four for a mirror and eight
/// for the ground of a room out of doors — and how deep this surface's height map goes.
/// </param>
/// <param name="Material">
/// The finish measured for this texture, which the shader uses where no map overrides it.
/// </param>
/// <param name="Wind">
/// How far a leaf sways, how fast, and the clock as it stood a frame ago. The block takes
/// lodgers where a surface plainly cannot be the thing the slot was named for: w is how
/// much of a mirror is frame rather than glass, and y — free while x, which switches the
/// sway off, is nought — is how far a piece of outdoor ground may vary.
/// </param>
/// <param name="Fur">
/// Which shell of a coat this draw is: x how far up the fur it stands, from zero at the
/// skin to one at the tips, y how deep the whole coat is in world units, z how many strands
/// cross one turn of the texture. All zero for everything that is not an animal, which is
/// everything but the cat.
/// </param>
/// <param name="Screen">
/// Where the lit glass of a CRT is inside this surface's texture: u and v of one corner,
/// then the other. A rectangle with no area for everything that is not a screen, which is
/// everything but Larry's monitor.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DrawConstants(
    Matrix4x4 Model,
    Matrix4x4 PreviousModel,
    Vector4 Shading,
    Vector4 Material,
    Vector4 Wind,
    Vector4 Fur,
    Vector4 Screen)
{
    /// <summary>How many bytes of push constants one of these is.</summary>
    public static uint Bytes { get; } = (uint)Marshal.SizeOf<DrawConstants>();

    /// <summary>And how many thirty-two-bit words, which is what Direct3D counts in.</summary>
    public static uint Words { get; } = Bytes / 4;
}

/// <summary>One batch, ready to be drawn, with nothing left to decide.</summary>
/// <param name="Vertices">This pose.</param>
/// <param name="Previous">
/// The pose before it, which a batch nothing has animated reports as the same buffer.
/// </param>
/// <param name="Indices">Which vertices, in which order.</param>
/// <param name="IndexCount">How many indices.</param>
/// <param name="ShortIndices">Whether they are sixteen bits each rather than thirty-two.</param>
/// <param name="Material">The textures it draws with.</param>
/// <param name="Constants">What the shader is told about it.</param>
/// <param name="Shells">
/// The coat over it, a shell at a time, or empty for a surface with no fur. Each is another
/// draw of the same triangles with only the constants changed.
/// </param>
/// <param name="DoubleSided">
/// Whether both faces of these triangles are drawn, rather than only the one their winding
/// says is the front.
/// </param>
public readonly record struct SceneDraw(
    IGeometryBuffer Vertices,
    IGeometryBuffer Previous,
    IGeometryBuffer Indices,
    uint IndexCount,
    bool ShortIndices,
    IGeometryMaterial Material,
    DrawConstants Constants,
    IReadOnlyList<DrawConstants> Shells,
    bool DoubleSided = true);

/// <summary>One vertex of a mesh, as both backends receive it.</summary>
/// <param name="Position">Where it is, in the model's own space.</param>
/// <param name="Normal">Which way the surface faces there.</param>
/// <param name="TexCoord">Where to read the surface's own texture.</param>
/// <param name="LightmapCoord">Where to read the baked light, in the room's atlas.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord,
    Vector2 LightmapCoord);
