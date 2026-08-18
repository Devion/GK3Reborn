using System.Globalization;

namespace GK3Reborn.Sheep;

/// <summary>The value types the Sheep virtual machine operates on.</summary>
/// <remarks>
/// The member names deliberately mirror Sheep's own type vocabulary rather than .NET's.
/// A conformance boundary is easier to verify against the original grammar when the
/// names match it, which is why CA1720 is suppressed here.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Member names mirror the Sheep language's own type names.")]
public enum SheepValueKind
{
    /// <summary>32-bit signed integer.</summary>
    Int,

    /// <summary>32-bit float.</summary>
    Float,

    /// <summary>String, compared case-insensitively.</summary>
    String,
}

/// <summary>
/// A value on the Sheep stack.
/// </summary>
/// <remarks>
/// Plan/01-architecture.md section 6: coercion rules, string semantics and error
/// behavior are a compatibility boundary proven by conformance tests, not something
/// to reimplement from intuition. GEngine's compiler is flex/bison generated, so
/// GK3Reborn hand-writes the scanner and parser instead of porting generated code.
/// </remarks>
public readonly record struct SheepValue
{
    private readonly int _int;
    private readonly float _float;
    private readonly string? _string;

    private SheepValue(SheepValueKind kind, int i, float f, string? s)
    {
        Kind = kind;
        _int = i;
        _float = f;
        _string = s;
    }

    /// <summary>Which kind of value this is.</summary>
    public SheepValueKind Kind { get; }

    /// <summary>Creates an integer value.</summary>
    public static SheepValue FromInt(int value) => new(SheepValueKind.Int, value, 0, null);

    /// <summary>Creates a float value.</summary>
    public static SheepValue FromFloat(float value) => new(SheepValueKind.Float, 0, value, null);

    /// <summary>Creates a string value.</summary>
    public static SheepValue FromString(string value) =>
        new(SheepValueKind.String, 0, 0, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Reads this value as an integer, coercing when needed.</summary>
    public int AsInt() => Kind switch
    {
        SheepValueKind.Int => _int,
        SheepValueKind.Float => (int)_float,
        SheepValueKind.String => int.TryParse(_string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0,
        _ => 0,
    };

    /// <summary>Reads this value as a float, coercing when needed.</summary>
    public float AsFloat() => Kind switch
    {
        SheepValueKind.Int => _int,
        SheepValueKind.Float => _float,
        SheepValueKind.String => float.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f,
        _ => 0f,
    };

    /// <summary>Reads this value as a string, coercing when needed.</summary>
    public string AsString() => Kind switch
    {
        SheepValueKind.Int => _int.ToString(CultureInfo.InvariantCulture),
        SheepValueKind.Float => _float.ToString("0.0####", CultureInfo.InvariantCulture),
        SheepValueKind.String => _string ?? string.Empty,
        _ => string.Empty,
    };

    /// <inheritdoc/>
    public override string ToString() => AsString();
}
