namespace GK3Reborn.Rendering.Shaders;

/// <summary>Which stage a shader is compiled for.</summary>
/// <remarks>
/// Only the three stages the renderer actually has. There is no ray generation or hit
/// stage because the ray-traced work is done with inline ray query from a fragment or
/// compute shader rather than with a ray-tracing pipeline; see ADR 0008 and
/// <see cref="ShaderLanguage.Glsl"/>.
/// </remarks>
public enum ShaderStage
{
    /// <summary>Vertex shader.</summary>
    Vertex,

    /// <summary>Fragment shader. Called a pixel shader by Direct3D.</summary>
    Fragment,

    /// <summary>Compute shader.</summary>
    Compute,
}
