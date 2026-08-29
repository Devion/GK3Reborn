using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace GK3Reborn.Rendering.Shaders;

/// <summary>
/// Compiles HLSL to DXIL, which is the only thing Direct3D 12 will load.
/// </summary>
/// <remarks>
/// <para>
/// DXC arrives as a NuGet package of its own rather than from an installed SDK, for the
/// same reason shaderc does: a contributor who wants to change gameplay code should not
/// have to install a graphics SDK to build the project. Two native libraries come with it
/// and both are needed. <c>dxcompiler</c> is the compiler; <c>dxil</c> is the signing
/// library, and without it every module compiles but comes out unsigned — which a device
/// refuses to create a pipeline from unless the machine is in developer mode. That failure
/// arrives at pipeline creation rather than here, which is a long way from its cause, so
/// the signature is checked at the end of every compile instead.
/// </para>
/// <para>
/// Shader model 6.5 throughout. It is the floor for <c>RayQuery</c>, which is the only
/// form of ray tracing the renderer uses, and there is nothing to gain by compiling the
/// raster shaders against an older one.
/// </para>
/// </remarks>
public sealed class DxilCompiler : IDisposable
{
    /// <summary>DXC's class ID for the compiler object, from <c>dxcapi.h</c>.</summary>
    private static readonly Guid CompilerClass = new("73e22d93-e6ce-47f3-b5bf-f0664f39c1b0");

    private readonly DXC _dxc;
    private ComPtr<IDxcCompiler3> _compiler;
    private bool _disposed;

    /// <summary>Creates a compiler.</summary>
    /// <exception cref="ShaderCompilationException">DXC could not be loaded or started.</exception>
    public unsafe DxilCompiler()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new ShaderCompilationException(
                "DXIL can only be compiled on Windows; there is no Direct3D anywhere else.");
        }

        try
        {
            _dxc = DXC.GetApi();
        }
        catch (Exception exception) when (exception is DllNotFoundException or FileNotFoundException)
        {
            throw new ShaderCompilationException(
                "Could not load dxcompiler. It ships beside the game in libs/<rid>; a build "
                + "that has lost it cannot compile a shader for Direct3D.",
                exception);
        }

        Guid classId = CompilerClass;
        Guid interfaceId = IDxcCompiler3.Guid;

        ComPtr<IDxcCompiler3> compiler = default;
        int hr = _dxc.CreateInstance(&classId, &interfaceId, (void**)compiler.GetAddressOf());

        if (hr < 0)
        {
            _dxc.Dispose();
            throw new ShaderCompilationException($"Could not start DXC: 0x{hr:X8}.");
        }

        _compiler = compiler;
    }

    /// <summary>Compiles HLSL to DXIL.</summary>
    /// <param name="hlsl">HLSL source, as SPIRV-Cross wrote it.</param>
    /// <param name="stage">Which stage to compile for.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="entryPoint">Entry point function.</param>
    /// <returns>A signed DXIL container.</returns>
    /// <exception cref="ShaderCompilationException">The shader did not compile.</exception>
    public unsafe byte[] Compile(
        string hlsl,
        ShaderStage stage,
        string name = "shader",
        string entryPoint = "main")
    {
        ArgumentNullException.ThrowIfNull(hlsl);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(entryPoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string profile = stage switch
        {
            ShaderStage.Vertex => "vs_6_5",
            ShaderStage.Fragment => "ps_6_5",
            _ => "cs_6_5",
        };

        // HLSL 2021 is DXC's own default from 1.7 onwards and is stated anyway, because a
        // silent change of language version between package updates would change what the
        // generated source means. -Zpr matches the row-major matrices SPIRV-Cross emits:
        // without it the declarations say row_major and the packing does not.
        string[] arguments =
        [
            "-T", profile,
            "-E", entryPoint,
            "-HV", "2021",
            "-O3",
            "-Zpr",
            "-Qstrip_reflect",
            "-Qstrip_debug",
        ];

        byte[] source = Encoding.UTF8.GetBytes(hlsl);
        nint[] argumentPointers = new nint[arguments.Length];

        try
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                argumentPointers[i] = SilkMarshal.StringToPtr(arguments[i], NativeStringEncoding.LPWStr);
            }

            fixed (byte* text = source)
            fixed (nint* argv = argumentPointers)
            {
                var buffer = new Silk.NET.Direct3D.Compilers.Buffer
                {
                    Ptr = text,
                    Size = (nuint)source.Length,

                    // CP_UTF8. DXC guesses the encoding otherwise, and a non-ASCII
                    // character in a comment becomes a parse error a long way from its line.
                    Encoding = 65001,
                };

                Guid resultId = IDxcResult.Guid;
                ComPtr<IDxcResult> result = default;

                int hr = _compiler.Compile(
                    &buffer,
                    (char**)argv,
                    (uint)arguments.Length,
                    (IDxcIncludeHandler*)null,
                    &resultId,
                    (void**)result.GetAddressOf());

                if (hr < 0)
                {
                    throw new ShaderCompilationException($"Could not compile {name}: 0x{hr:X8}.");
                }

                try
                {
                    int status = 0;
                    result.GetStatus(&status);

                    if (status < 0)
                    {
                        throw new ShaderCompilationException(
                            $"Could not compile {name}: {ErrorsFrom(ref result)}");
                    }

                    Guid blobId = IDxcBlob.Guid;
                    ComPtr<IDxcBlob> dxil = default;
                    result.GetOutput(
                        OutKind.Object, &blobId, (void**)dxil.GetAddressOf(), (IDxcBlobWide**)null);

                    if (dxil.Handle is null)
                    {
                        throw new ShaderCompilationException(
                            $"Could not compile {name}: DXC reported success and produced nothing.");
                    }

                    try
                    {
                        nuint size = dxil.GetBufferSize();
                        byte[] bytes = new byte[size];
                        new ReadOnlySpan<byte>(dxil.GetBufferPointer(), (int)size).CopyTo(bytes);

                        if (!IsSigned(bytes))
                        {
                            throw new ShaderCompilationException(
                                $"{name} compiled but was not signed. The dxil library is missing "
                                + "from beside the game; an unsigned module is refused when a "
                                + "pipeline is made from it.");
                        }

                        return bytes;
                    }
                    finally
                    {
                        dxil.Dispose();
                    }
                }
                finally
                {
                    result.Dispose();
                }
            }
        }
        finally
        {
            foreach (nint pointer in argumentPointers)
            {
                if (pointer != 0)
                {
                    SilkMarshal.Free(pointer);
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compiler.Dispose();
        _dxc.Dispose();
    }

    /// <summary>Whether a DXIL container carries a signature rather than sixteen zero bytes.</summary>
    /// <remarks>
    /// A DXBC container's header is the four-character code, then a sixteen-byte hash, then
    /// the sizes. DXC writes zeroes into the hash and the signing library fills them in; if
    /// it was never loaded they stay zero and the module is refused at pipeline creation
    /// with nothing said about why. Checking here turns that into a sentence.
    /// </remarks>
    private static bool IsSigned(ReadOnlySpan<byte> container)
    {
        if (container.Length < 20 || !container.StartsWith("DXBC"u8))
        {
            return false;
        }

        return container.Slice(4, 16).ContainsAnyExcept((byte)0);
    }

    private static unsafe string ErrorsFrom(ref ComPtr<IDxcResult> result)
    {
        Guid blobId = IDxcBlobUtf8.Guid;
        ComPtr<IDxcBlobUtf8> errors = default;
        result.GetOutput(OutKind.Errors, &blobId, (void**)errors.GetAddressOf(), (IDxcBlobWide**)null);

        if (errors.Handle is null)
        {
            return "no diagnostics.";
        }

        try
        {
            return Marshal.PtrToStringUTF8((nint)errors.GetBufferPointer()) ?? "no diagnostics.";
        }
        finally
        {
            errors.Dispose();
        }
    }
}
