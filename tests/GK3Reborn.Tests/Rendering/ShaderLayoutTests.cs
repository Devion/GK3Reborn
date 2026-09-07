using System.Globalization;
using System.Text.RegularExpressions;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// A pipeline's declared layout has to say what its shaders actually bind.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="ShaderLayout"/> is one statement built two ways: Direct3D turns it into a
/// root signature and Vulkan into descriptor set layouts. Nothing checks it against the
/// shaders it describes, and neither API will. Direct3D refuses a root signature that does
/// not satisfy a shader, so a shortfall there is a startup failure somebody sees. Vulkan
/// binds what it is given and reads what it is asked for: a binding the layout leaves out is
/// a descriptor the shader reads and nothing backs, with no error, no validation message on
/// a machine without the layers, and a picture that is merely wrong.
/// </para>
/// <para>
/// That is not hypothetical. The reflection pass's Vulkan layout listed ten bindings where
/// the shaders and the root signature have eleven, leaving <c>planarReflection</c> unbacked
/// while a descriptor was written for it anyway. Every prop in the room came out reflecting
/// the frame back at itself — the march answered with full confidence where it should have
/// answered with none — and it brightened over about a second and stayed. Direct3D was
/// correct throughout, which is why it went unnoticed.
/// </para>
/// <para>
/// The Vulkan side is derived from the shared layout now, so the two backends cannot come
/// apart. This is the other half: that the shared layout matches the GLSL.
/// </para>
/// </remarks>
public sealed partial class ShaderLayoutTests
{
    /// <summary>Each shared layout, and the shaders built against it.</summary>
    public static TheoryData<string> Pipelines() => new()
    {
        "reflect.downsample",
        "reflect.march",
        "denoise.trace",
        "denoise.classify",
        "denoise.filter",
    };

    [Theory]
    [MemberData(nameof(Pipelines))]
    public void A_shader_binds_what_its_layout_declares(string pipeline)
    {
        (string source, ShaderLayout layout) = Pipeline(pipeline);

        // Set zero is the only one any of these use, and taking it explicitly keeps the
        // test honest about a layout that one day spans two.
        int[] declared =
        [
            .. layout.Bindings.Where(b => b.Set == 0).Select(b => (int)b.Binding).Order(),
        ];

        int[] used = [.. BindingsIn(source).Order()];

        Assert.Equal(declared, used);
    }

    /// <summary>The set-zero binding numbers a GLSL source declares.</summary>
    /// <remarks>
    /// <para>
    /// Read out of the source rather than out of the compiled module: the point is to check
    /// what was written, and a compiler that dropped an unused binding would hide exactly the
    /// mistake this is looking for.
    /// </para>
    /// <para>
    /// The whole qualifier list, then the two qualifiers out of it, because their order is
    /// the author's — the denoiser's light rig is <c>layout(std430, set = 0, binding = 5)</c>
    /// and everything around it puts the set first. A pattern that expected one order read
    /// that binding as absent, which is the failure this test exists to report.
    /// </para>
    /// </remarks>
    private static IEnumerable<int> BindingsIn(string source) =>
        LayoutQualifier().Matches(source)
            .Select(m => m.Groups["qualifiers"].Value)
            .Where(qualifiers => InSetZero().IsMatch(qualifiers))
            .Select(qualifiers => BindingIndex().Match(qualifiers))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups["binding"].Value, CultureInfo.InvariantCulture))
            .Distinct();

    private static (string Source, ShaderLayout Layout) Pipeline(string name) => name switch
    {
        "reflect.downsample" => (ReflectionShaders.ComposeDownsample(), ReflectLayout.Bindings),
        "reflect.march" => (ReflectionShaders.ComposeMarch(), ReflectLayout.Bindings),
        "denoise.trace" => (DenoiserShaders.ComposeTrace(), DenoiseLayout.Trace),
        "denoise.classify" => (DenoiserShaders.ComposeClassify(), DenoiseLayout.Denoise),
        "denoise.filter" => (DenoiserShaders.ComposeFilter(), DenoiseLayout.Denoise),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such pipeline."),
    };

    [GeneratedRegex(@"layout\s*\((?<qualifiers>[^)]*)\)")]
    private static partial Regex LayoutQualifier();

    [GeneratedRegex(@"\bset\s*=\s*0\b")]
    private static partial Regex InSetZero();

    [GeneratedRegex(@"\bbinding\s*=\s*(?<binding>\d+)")]
    private static partial Regex BindingIndex();
}
