namespace GK3Reborn.Foundation.Diagnostics;

/// <summary>Collects diagnostics produced while processing a unit of work.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    /// <summary>All diagnostics recorded so far, in order.</summary>
    public IReadOnlyList<Diagnostic> Items => _items;

    /// <summary>True when at least one <see cref="DiagnosticSeverity.Error"/> was recorded.</summary>
    public bool HasErrors => _items.Exists(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>Records a diagnostic.</summary>
    public void Add(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _items.Add(diagnostic);
    }

    /// <summary>Records diagnostics from another bag.</summary>
    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _items.AddRange(diagnostics);
    }
}
