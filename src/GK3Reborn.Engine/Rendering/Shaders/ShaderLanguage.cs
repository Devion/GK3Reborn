namespace GK3Reborn.Rendering.Shaders;

/// <summary>Which language a shader is written in.</summary>
public enum ShaderLanguage
{
    /// <summary>HLSL, as the plan chose for the raster shaders.</summary>
    Hlsl,

    /// <summary>
    /// GLSL, which is the only one of the two shaderc can express inline ray tracing in.
    /// </summary>
    Glsl,
}
