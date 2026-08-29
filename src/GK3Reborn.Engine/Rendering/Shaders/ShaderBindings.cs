namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// How a Vulkan binding is spelled in Direct3D, and where the push constants go.
/// </summary>
/// <remarks>
/// <para>
/// The shaders are written once, against Vulkan's <c>(set, binding)</c> model, and the
/// Direct3D backend reads whatever SPIRV-Cross makes of them. That translation is not
/// arbitrary and it is not discovered at runtime: a descriptor set becomes a register
/// space and a binding becomes a register index within it, so <c>set = 1, binding = 3</c>
/// is <c>space1</c>, index 3 — in whichever register class the resource's own type
/// implies. A uniform buffer lands in <c>b</c>, a sampled image, a read-only storage
/// buffer and an acceleration structure in <c>t</c>, a storage image and a writable
/// storage buffer in <c>u</c>, and a sampler in <c>s</c>.
/// </para>
/// <para>
/// This mapping is collision-free by construction, which is the reason to state it rather
/// than to remap: two resources in one set never share a binding number, so they never
/// share a register index; and two resources that do share an index — a texture and a
/// sampler out of the same combined image sampler — are in different register classes by
/// definition. A root signature can therefore be built from the same layout description
/// the Vulkan descriptor set layout is built from, with no per-shader reflection in the
/// hot path and nothing to keep in step by hand.
/// </para>
/// <para>
/// Push constants are the one thing with no natural home. Vulkan gives them their own
/// storage class outside every set; HLSL has only <c>cbuffer</c>, and SPIRV-Cross will
/// pick a register for one if nobody says otherwise — which is how it comes to sit on top
/// of the frame's uniform buffer at <c>b0, space0</c>. So they are placed deliberately, in
/// a space no descriptor set will ever use.
/// </para>
/// </remarks>
public static class ShaderBindings
{
    /// <summary>
    /// The register space push constants are given in generated HLSL, and the space the
    /// root signature must declare its root constants in.
    /// </summary>
    /// <remarks>
    /// Far above anything a descriptor set will claim. Vulkan promises only four sets and
    /// the renderer uses fewer; a device that offered a thousand would still not reach
    /// this, because the sets are numbered by the passes rather than by the device.
    /// </remarks>
    public const uint PushConstantSpace = 15;

    /// <summary>The register index push constants are given, within
    /// <see cref="PushConstantSpace"/>.</summary>
    public const uint PushConstantRegister = 0;

    /// <summary>
    /// The descriptor set number SPIRV-Cross uses to mean "the push constant block".
    /// </summary>
    /// <remarks>
    /// <c>SPVC_HLSL_PUSH_CONSTANT_DESC_SET</c> in <c>spirv_cross_c.h</c>, which is
    /// <c>~0u</c>. It is not a set that exists; it is the key under which a binding for
    /// the push constant block is registered.
    /// </remarks>
    public const uint PushConstantDescriptorSet = uint.MaxValue;

    /// <summary>
    /// The push constant block's binding number under
    /// <see cref="PushConstantDescriptorSet"/>.
    /// </summary>
    /// <remarks><c>SPVC_HLSL_PUSH_CONSTANT_BINDING</c>, which is zero.</remarks>
    public const uint PushConstantBinding = 0;

    /// <summary>
    /// The vertex input semantic SPIRV-Cross gives every attribute in generated HLSL.
    /// </summary>
    /// <remarks>
    /// It has no other name to give them. A GLSL vertex input has a location and nothing
    /// else, so the HLSL that comes out is <c>TEXCOORD&lt;location&gt;</c> for all of them,
    /// position included. A Direct3D input layout therefore names every element
    /// <c>TEXCOORD</c> and distinguishes them by semantic index, which is the location.
    /// </remarks>
    public const string VertexInputSemantic = "TEXCOORD";

    /// <summary>The shader model generated HLSL is compiled against.</summary>
    /// <remarks>
    /// 6.5 rather than something older, because that is the model that has
    /// <c>RayQuery</c>. Inline ray tracing is the only form the renderer uses (ADR 0008),
    /// so this is the floor for the ray-traced shaders and there is no reason to compile
    /// the rest against anything else.
    /// </remarks>
    public const uint ShaderModel = 65;
}
