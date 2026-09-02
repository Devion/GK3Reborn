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
/// Shader model 6.5 by default, which is the floor for <c>RayQuery</c>, the only form of
/// ray tracing the renderer uses. It is not a floor for the device: a card that reports
/// less is given modules compiled for what it has, down to 6.0, because the raster
/// shaders need nothing newer and a ray query is never in them on a card without the
/// tier. See <see cref="Direct3D12.D3D12Context.DxilShaderModel"/>.
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
            _dxc = ShaderToolchain.Dxc;
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
            throw new ShaderCompilationException($"Could not start DXC: 0x{hr:X8}.");
        }

        _compiler = compiler;
    }

    /// <summary>The shader model compiled for when nobody names one: 6.5.</summary>
    public const uint DefaultShaderModel = 0x65;

    /// <summary>The DXC profile for a stage at a shader model.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="shaderModel">The model as D3D writes it: 0x60 through 0x69.</param>
    /// <returns>What <c>-T</c> is given, such as <c>ps_6_1</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The model is not one DXIL has.</exception>
    public static string ProfileFor(ShaderStage stage, uint shaderModel)
    {
        if (shaderModel is < 0x60 or > 0x69)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shaderModel), shaderModel,
                "DXIL shader models run from 6.0 (0x60) to 6.9 (0x69).");
        }

        string kind = stage switch
        {
            ShaderStage.Vertex => "vs",
            ShaderStage.Fragment => "ps",
            _ => "cs",
        };

        return $"{kind}_{shaderModel >> 4}_{shaderModel & 0xF}";
    }

    /// <summary>Compiles HLSL to DXIL.</summary>
    /// <param name="hlsl">HLSL source, as SPIRV-Cross wrote it.</param>
    /// <param name="stage">Which stage to compile for.</param>
    /// <param name="name">Name used in error messages.</param>
    /// <param name="entryPoint">Entry point function.</param>
    /// <param name="shaderModel">
    /// The shader model to compile for, as D3D writes it. The device's own, capped at 6.5:
    /// a module compiled for a newer model than the driver reports is refused when a
    /// pipeline is made from it, and the refusal names neither the module nor the model.
    /// </param>
    /// <returns>A signed DXIL container.</returns>
    /// <exception cref="ShaderCompilationException">The shader did not compile.</exception>
    public unsafe byte[] Compile(
        string hlsl,
        ShaderStage stage,
        string name = "shader",
        string entryPoint = "main",
        uint shaderModel = DefaultShaderModel)
    {
        ArgumentNullException.ThrowIfNull(hlsl);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(entryPoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string profile = ProfileFor(stage, shaderModel);

        // HLSL 2021 is DXC's own default from 1.7 onwards and is stated anyway, because a
        // silent change of language version between package updates would change what the
        // generated source means. -Zpr matches the row-major matrices SPIRV-Cross emits:
        // without it the declarations say row_major and the packing does not.
        //
        // Nothing is stripped from the container. -Qstrip_reflect was here and had to go: it
        // leaves the feature-flag part describing a module that is no longer there, and the
        // signer then refuses its own output with "Flags must match usage", naming two bit
        // masks and no shader. The reflection part is a few hundred bytes and the cache
        // holds one copy of it.
        string[] arguments =
        [
            "-T", profile,
            "-E", entryPoint,
            "-HV", "2021",
            "-O3",
            "-Zpr",
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

        // The compiler object is this instance's and is released here. The library handle
        // is not: it belongs to ShaderToolchain and is held for the life of the process,
        // for the reasons written down there.
        _compiler.Dispose();
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
