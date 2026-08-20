namespace GK3Reborn.Content.Manifests;

/// <summary>Measured lighting for one scene at one time of day.</summary>
public sealed record SceneLighting
{
    /// <summary>Scene the lightmaps belong to.</summary>
    public required string Scene { get; init; }

    /// <summary>Name of the lightmap set.</summary>
    public required string SetName { get; init; }

    /// <summary>Timeblock letter, or "-" when the set has none.</summary>
    public required string Timeblock { get; init; }

    /// <summary>Surfaces measured.</summary>
    public required int Surfaces { get; init; }

    /// <summary>True when the set and the scene disagree on how many surfaces exist.</summary>
    public required bool SurfaceCountMismatch { get; init; }

    /// <summary>Average luminance across every surface, 0 to 1.</summary>
    public required double MeanLuminance { get; init; }

    /// <summary>Average colour of the baked light, as <c>#RRGGBB</c>.</summary>
    public required string MeanColor { get; init; }

    /// <summary>
    /// Surfaces whose lighting varies enough across them to imply a direction. These are
    /// the ones a light position can be derived from.
    /// </summary>
    public required int DirectionalSurfaces { get; init; }

    /// <summary>Surfaces lit evenly, which carry no directional information.</summary>
    public required int FlatSurfaces { get; init; }

    /// <summary>Surfaces receiving almost no light.</summary>
    public required int DarkSurfaces { get; init; }
}

/// <summary>Totals across the whole corpus.</summary>
public sealed record LightingSummary
{
    /// <summary>Lightmap sets analysed.</summary>
    public required int Sets { get; init; }

    /// <summary>Distinct scenes covered.</summary>
    public required int Scenes { get; init; }

    /// <summary>Scenes carrying more than one time of day, where differencing is possible.</summary>
    public required int ScenesWithTimeblockVariants { get; init; }

    /// <summary>Surfaces with usable directional information.</summary>
    public required int DirectionalSurfaces { get; init; }

    /// <summary>Surfaces lit evenly.</summary>
    public required int FlatSurfaces { get; init; }

    /// <summary>Surfaces receiving almost no light.</summary>
    public required int DarkSurfaces { get; init; }

    /// <summary>Fraction of surfaces carrying directional information.</summary>
    public required double DirectionalFraction { get; init; }

    /// <summary>Sets whose surface count disagrees with their scene.</summary>
    public required int SurfaceCountMismatches { get; init; }
}

/// <summary>The C4b lighting analysis.</summary>
public sealed record LightingAnalysisManifest
{
    /// <summary>Schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Pipeline stage that produced it.</summary>
    public required string Stage { get; init; }

    /// <summary>Directory the archives were read from.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Corpus-wide totals.</summary>
    public required LightingSummary Summary { get; init; }

    /// <summary>Per scene and time of day.</summary>
    public required IReadOnlyList<SceneLighting> Scenes { get; init; }
}
