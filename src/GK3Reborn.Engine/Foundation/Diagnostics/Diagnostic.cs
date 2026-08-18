using System.Globalization;

namespace GK3Reborn.Foundation.Diagnostics;

/// <summary>How severe a diagnostic is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Informational; no action needed.</summary>
    Info,

    /// <summary>Something is off but the operation continued.</summary>
    Warning,

    /// <summary>The operation failed for this item.</summary>
    Error,
}

/// <summary>
/// A single actionable diagnostic.
/// </summary>
/// <remarks>
/// Plan/README.md, "no silent compatibility failures": unsupported versions and
/// corrupt assets must produce diagnostics that identify file, offset, expected
/// value and remediation. Those fields are therefore first class rather than
/// interpolated into a message string.
/// </remarks>
/// <param name="Code">Stable machine-readable code, e.g. <c>GK3R1001</c>.</param>
/// <param name="Severity">How severe this is.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="File">File this concerns, if any.</param>
/// <param name="Offset">Byte offset within <paramref name="File"/>, if any.</param>
/// <param name="Expected">What was expected at that position, if any.</param>
/// <param name="Actual">What was found instead, if any.</param>
/// <param name="Remediation">What the user or developer should do about it.</param>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? File = null,
    long? Offset = null,
    string? Expected = null,
    string? Actual = null,
    string? Remediation = null)
{
    /// <summary>Renders a single-line form suitable for logs.</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Severity.ToString().ToUpperInvariant()} {Code}: {Message}");
        if (File is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" [{File}");
            if (Offset is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"+0x{Offset:X}");
            }

            sb.Append(']');
        }

        if (Expected is not null || Actual is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" (expected {Expected ?? "?"}, got {Actual ?? "?"})");
        }

        if (Remediation is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $" -> {Remediation}");
        }

        return sb.ToString();
    }
}
