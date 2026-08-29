namespace GK3Reborn.Rendering.Shaders;

/// <summary>A shader did not survive one of the steps between its source and a backend.</summary>
/// <remarks>
/// Backend-neutral on purpose. The same source goes to Vulkan and to Direct3D 12 through
/// the same front end, so a failure to compile it is not a fact about either device and
/// should not arrive as one.
/// </remarks>
public sealed class ShaderCompilationException : Exception
{
    /// <summary>Creates an exception.</summary>
    public ShaderCompilationException()
    {
    }

    /// <summary>Creates an exception.</summary>
    /// <param name="message">What went wrong.</param>
    public ShaderCompilationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">What caused it.</param>
    public ShaderCompilationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
