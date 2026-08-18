using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace GK3Reborn.Foundation;

/// <summary>
/// A normalized, case-insensitive logical asset identifier.
/// </summary>
/// <remarks>
/// GK3 data references assets case-insensitively and inconsistently
/// ("DAY3-3.BIK", "day3-3", "Day3-3.bik"). Every subsystem addresses content
/// through this type so that exactly one canonical form exists, while the
/// original spelling stays available for diagnostics and manifests.
/// </remarks>
public readonly struct AssetId : IEquatable<AssetId>
{
    private readonly string? _normalized;

    private AssetId(string normalized, string original)
    {
        _normalized = normalized;
        Original = original;
    }

    /// <summary>The canonical uppercase-invariant form. Never null.</summary>
    public string Value => _normalized ?? string.Empty;

    /// <summary>The spelling this id was created from, for diagnostics.</summary>
    public string? Original { get; }

    /// <summary>True when this id carries no name.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_normalized);

    /// <summary>
    /// Creates an id from a name, with or without an extension. The extension is
    /// dropped: GK3 references videos and many assets without one, so the logical
    /// identity is the bare name.
    /// </summary>
    public static AssetId From(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        ReadOnlySpan<char> span = name.AsSpan().Trim();
        int slash = span.LastIndexOfAny('/', '\\');
        if (slash >= 0)
        {
            span = span[(slash + 1)..];
        }

        int dot = span.LastIndexOf('.');
        if (dot > 0)
        {
            span = span[..dot];
        }

        return new AssetId(span.ToString().ToUpperInvariant(), name);
    }

    /// <summary>Creates an id that keeps its extension as part of the identity.</summary>
    public static AssetId FromExact(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new AssetId(name.Trim().ToUpperInvariant(), name);
    }

    public bool Equals(AssetId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is AssetId other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;

    public static bool operator ==(AssetId left, AssetId right) => left.Equals(right);

    public static bool operator !=(AssetId left, AssetId right) => !left.Equals(right);
}
