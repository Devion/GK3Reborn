using GK3Reborn.Formats;
using GK3Reborn.Formats.Ini;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>
/// Decides which of a scene file's conditional sections apply right now.
/// </summary>
public sealed class SceneConditions
{
    private readonly ISheepApi _api;
    private readonly Dictionary<string, bool> _decided = new(StringComparer.Ordinal);

    /// <summary>Creates an evaluator.</summary>
    /// <param name="api">Host used to resolve the functions a condition calls.</param>
    public SceneConditions(ISheepApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>Diagnostics raised while deciding.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    /// <summary>The filter to read a scene file through.</summary>
    public SectionFilter Applies => Holds;

    /// <summary>Whether a section's condition holds.</summary>
    /// <param name="condition">The expression, or null for an unconditional section.</param>
    /// <returns>True when the section's lines count.</returns>
    public bool Holds(string? condition)
    {
        if (condition is null)
        {
            return true;
        }

        if (_decided.TryGetValue(condition, out bool cached))
        {
            return cached;
        }

        bool result;

        try
        {
            result = SheepExpression.IsTrue(condition, _api);
        }
        catch (FormatParseException ex)
        {
            Diagnostics.Add(ex.Diagnostic);
            result = false;
        }

        _decided[condition] = result;
        return result;
    }
}
