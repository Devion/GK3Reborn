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
/// Whether to read the lightmap, what to multiply it by, two flags packed into one number
/// — one for self-lit and two for a model standing in the room — and how deep this
/// surface's height map goes.
/// </param>
/// <param name="Material">
/// The finish measured for this texture, which the shader uses where no map overrides it.
/// </param>
/// <param name="Wind">How far a leaf sways, how fast, and the clock as it stood a frame ago.</param>
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
/// <remarks>
/// <para>
/// Push constants on Vulkan and root constants on Direct3D, which are the same thing under
/// two names: a small block that travels with a draw and needs no buffer, no descriptor and
/// no synchronisation between frames in flight.
/// </para>
/// <para>
/// <b>Two hundred and eight bytes, which is past the hundred and twenty-eight Vulkan
/// guarantees.</b> Every desktop driver this renderer has run on offers 256, and the two
/// matrices alone were already past the floor. Direct3D counts a root signature in
/// thirty-two-bit words and allows sixty-four of them, so this is fifty-two of the
/// sixty-four and the descriptor tables have to fit in what is left — which they do, at one
/// word each. It is the number to look at first if either API ever refuses the layout, and
/// the fix is a uniform buffer rather than a smaller struct.
/// </para>
/// <para>
/// <b>Nothing here is written by hand twice.</b> Both backends take the size from this
/// struct, so a member added to it travels without either of them being told — which is
/// what the Direct3D side used to need, in two places, as a literal count of words.
/// </para>
/// </remarks>
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
/// <remarks>
/// <para>
/// The seam between deciding what to draw and issuing it. Everything about a batch that
/// takes thought — which pose is current, whether the lightmap applies, how much of the
/// height field is left after the geometry took its share, whether a leaf sways, how many
/// shells of fur stand over a skin — is worked out once and lands here. What is left for a
/// backend is binding two vertex streams, an index buffer and a descriptor, and calling
/// draw.
/// </para>
/// <para>
/// Two vertex streams rather than one, always. The second is the previous pose, which is
/// what lets a deforming character report its own movement to a temporal filter; a batch
/// nothing has animated binds the same buffer twice, which is the truth about it — its
/// vertices are where they have always been and only its transform can have moved.
/// </para>
/// </remarks>
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
/// <remarks>
/// Thirty-two bytes, and the same thirty-two on both backends: the input layout Direct3D
/// builds and the attribute descriptions Vulkan builds are two spellings of this one
/// declaration. A vertex is bound twice per draw — this pose and the one before it — so a
/// stride that disagreed with the shader would not fail, it would read the previous pose
/// from halfway through a vertex and report movement nothing made.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord,
    Vector2 LightmapCoord);
