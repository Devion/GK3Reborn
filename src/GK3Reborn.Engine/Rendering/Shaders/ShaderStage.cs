namespace GK3Reborn.Rendering.Shaders;

/// <summary>Which stage a shader is compiled for.</summary>
public enum ShaderStage
{
    /// <summary>Vertex shader.</summary>
    Vertex,

    /// <summary>Fragment shader. Called a pixel shader by Direct3D.</summary>
    Fragment,

    /// <summary>Compute shader.</summary>
    Compute,
}
