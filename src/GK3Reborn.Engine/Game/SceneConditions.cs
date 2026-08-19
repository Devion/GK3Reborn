using GK3Reborn.Formats;
using GK3Reborn.Formats.Ini;
using GK3Reborn.Foundation.Diagnostics;
using GK3Reborn.Sheep;

namespace GK3Reborn.Game;

/// <summary>
/// Decides which of a scene file's conditional sections apply right now.
/// </summary>
/// <remarks>
/// <para>
/// Almost everything in a SIF is conditional. R25 alone declares two beds, two states of
/// the wardrobe, a hall backdrop that only exists on the first visit at 202P and a door
/// that is there in every timeblock but one. Read without deciding those conditions a
/// scene is the union of every state it can be in; deciding them is what turns it into a
/// room.
/// </para>
/// <para>
/// The conditions are Sheep expressions in the section headers, the same language the
/// action files use for their cases, so this is <see cref="SheepExpression"/> over the
/// game's state rather than anything scene-specific. Across the 981 conditional headers
/// in the corpus they call sixteen distinct functions, all of them state queries.
/// </para>
/// <para>
/// Results are cached per expression. The same header text repeats across sections and
/// across the accessors that read them — models, actors, cameras and positions are four
/// separate passes over one file — and the expressions must not be re-evaluated each
/// time, both for cost and because a diagnostic would be raised once per pass.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// A malformed expression is reported and treated as false. The alternative — taking
    /// the section anyway — puts a scene into two states at once, which is the failure
    /// this class exists to remove.
    /// </remarks>
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
