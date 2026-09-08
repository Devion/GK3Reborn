namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// How a Vulkan binding is spelled in Direct3D, and where the push constants go.
/// </summary>
public static class ShaderBindings
{
    /// <summary>
    /// The register space push constants are given in generated HLSL, and the space the
    /// root signature must declare its root constants in.
    /// </summary>
    public const uint PushConstantSpace = 15;

    /// <summary>The register index push constants are given, within
    /// <see cref="PushConstantSpace"/>.</summary>
    public const uint PushConstantRegister = 0;

    /// <summary>
    /// The descriptor set number SPIRV-Cross uses to mean "the push constant block".
    /// </summary>
    public const uint PushConstantDescriptorSet = uint.MaxValue;

    /// <summary>
    /// The push constant block's binding number under
    /// <see cref="PushConstantDescriptorSet"/>.
    /// </summary>
    public const uint PushConstantBinding = 0;

    /// <summary>
    /// The vertex input semantic SPIRV-Cross gives every attribute in generated HLSL.
    /// </summary>
    public const string VertexInputSemantic = "TEXCOORD";

    /// <summary>The shader model generated HLSL is compiled against.</summary>
    public const uint ShaderModel = 65;
}
