namespace GK3Reborn.Rendering.Shaders;

/// <summary>Which intermediate language a shader is compiled down to.</summary>
public enum ShaderTarget
{
    /// <summary>SPIR-V, which Vulkan consumes directly.</summary>
    SpirV,

    /// <summary>DXIL, which Direct3D 12 consumes.</summary>
    Dxil,
}
