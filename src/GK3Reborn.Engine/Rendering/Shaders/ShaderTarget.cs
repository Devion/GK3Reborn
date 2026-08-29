namespace GK3Reborn.Rendering.Shaders;

/// <summary>Which intermediate language a shader is compiled down to.</summary>
/// <remarks>
/// The engine's shaders have one source apiece and two destinations. Everything is
/// written once, in the language ADR 0008 chose, and compiled to SPIR-V; the Direct3D 12
/// backend then carries that SPIR-V on through SPIRV-Cross and DXC to DXIL. Writing the
/// shaders twice was the alternative and was rejected: eight thousand lines of shading
/// maintained in two dialects is eight thousand lines that drift, and a divergence
/// between the backends shows up as a lighting difference nobody can attribute.
/// </remarks>
public enum ShaderTarget
{
    /// <summary>SPIR-V, which Vulkan consumes directly.</summary>
    SpirV,

    /// <summary>DXIL, which Direct3D 12 consumes.</summary>
    /// <remarks>
    /// Reached through HLSL: SPIRV-Cross turns the SPIR-V back into HLSL and DXC compiles
    /// that. Windows only, because <c>dxcompiler</c> and the <c>dxil</c> signing library
    /// are the only part of the chain with no other platform to run on.
    /// </remarks>
    Dxil,
}
