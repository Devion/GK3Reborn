using System.Globalization;
using System.Text.RegularExpressions;
using GK3Reborn.Rendering.Geometry;
using GK3Reborn.Rendering.Shaders;
using Xunit;

namespace GK3Reborn.Tests.Rendering;

/// <summary>
/// A pipeline's declared layout has to say what its shaders actually bind.
/// </summary>
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
