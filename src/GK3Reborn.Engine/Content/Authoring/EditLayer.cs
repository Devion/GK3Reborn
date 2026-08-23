using System.Text.Json.Serialization;
using GK3Reborn.Foundation.Diagnostics;

namespace GK3Reborn.Content.Authoring;

/// <summary>Where an authored value came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthoringProvenance>))]
public enum AuthoringProvenance
{
    /// <summary>Produced automatically by a converter. A best-effort guess.</summary>
    [JsonStringEnumMemberName("derived")]
    Derived,

    /// <summary>Derived, then corrected by hand.</summary>
    [JsonStringEnumMemberName("edited")]
    Edited,

    /// <summary>Authored from scratch; no derived original exists.</summary>
    [JsonStringEnumMemberName("authored")]
    Authored,
}

/// <summary>What an edit does to the derived baseline.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EditOperation>))]
public enum EditOperation
{
    /// <summary>Introduces an item the generator did not produce.</summary>
    [JsonStringEnumMemberName("add")]
    Add,

    /// <summary>Changes named fields of a derived item, leaving the rest alone.</summary>
    [JsonStringEnumMemberName("modify")]
    Modify,

    /// <summary>Deletes a derived item.</summary>
    [JsonStringEnumMemberName("remove")]
    Remove,
}

/// <summary>
/// An item that a human can correct after a converter has guessed it.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
/// <typeparam name="TPatch">A sparse patch over that type.</typeparam>
public interface IAuthorable<out TSelf, in TPatch>
{
    /// <summary>Identity within its document. Stable across regeneration.</summary>
    string Id { get; }

    /// <summary>Returns a copy with the patch's set fields applied.</summary>
    TSelf ApplyPatch(TPatch patch);

    /// <summary>Returns a copy marked as hand-corrected.</summary>
    TSelf MarkEdited();
}

/// <summary>One correction to the derived baseline.</summary>
/// <typeparam name="TItem">Item type being edited.</typeparam>
/// <typeparam name="TPatch">Sparse patch type.</typeparam>
public sealed record Edit<TItem, TPatch>
{
    /// <summary>What this edit does.</summary>
    public required EditOperation Operation { get; init; }

    /// <summary>Id of the item to modify or remove, or the id being added.</summary>
    public required string TargetId { get; init; }

    /// <summary>The complete item. Required for <see cref="EditOperation.Add"/>.</summary>
    public TItem? Item { get; init; }

    /// <summary>The fields to change. Required for <see cref="EditOperation.Modify"/>.</summary>
    public TPatch? Patch { get; init; }

    /// <summary>Why this correction was made. Free text, for the next person.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Composes a derived baseline with hand-authored corrections.
/// </summary>
/// <remarks>
/// <para>
/// Converters guess. Lightmap-derived light positions and inferred material
/// roughness are starting points, not answers, and the person looking at the scene
/// in-engine will always know better. The problem is that regenerating the baseline
/// would throw their corrections away.
/// </para>
/// <para>
/// So corrections live in their own file and are replayed over whatever the
/// generator most recently produced. Deleting a derived light, nudging one that
/// sits in the wrong place, dropping in a light the lightmap never implied, or
/// making a material less glossy are all the same mechanism, and none of them is
/// lost when the converter improves and reruns.
/// </para>
/// <para>
/// An edit that no longer applies - because the item it names is gone - is reported
/// and skipped, never silently dropped and never fatal.
/// </para>
/// </remarks>
public static class EditLayer
{
    /// <summary>
    /// Applies <paramref name="edits"/> to <paramref name="baseline"/> in order.
    /// </summary>
    /// <typeparam name="TItem">Item type.</typeparam>
    /// <typeparam name="TPatch">Sparse patch type.</typeparam>
    /// <param name="baseline">The converter's output.</param>
    /// <param name="edits">Hand-authored corrections, applied in order.</param>
    /// <param name="documentId">Document name, used in diagnostics.</param>
    /// <param name="diagnostics">Receives warnings about edits that no longer apply.</param>
    /// <returns>The effective item list.</returns>
    public static IReadOnlyList<TItem> Compose<TItem, TPatch>(
        IReadOnlyList<TItem> baseline,
        IReadOnlyList<Edit<TItem, TPatch>> edits,
        string documentId,
        DiagnosticBag diagnostics)
        where TItem : IAuthorable<TItem, TPatch>
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(diagnostics);

        List<TItem> items = [.. baseline];

        foreach (Edit<TItem, TPatch> edit in edits)
        {
            int index = items.FindIndex(i => string.Equals(i.Id, edit.TargetId, StringComparison.OrdinalIgnoreCase));

            switch (edit.Operation)
            {
                case EditOperation.Add when index >= 0:
                    diagnostics.Add(new Diagnostic(
                        "GK3R3001", DiagnosticSeverity.Warning,
                        $"Edit adds '{edit.TargetId}', which the baseline already contains.",
                        documentId, null, "an id not present in the baseline", edit.TargetId,
                        "The generator now produces this item. Change the edit to a modify, or rename it."));
                    break;

                case EditOperation.Add when edit.Item is null:
                    diagnostics.Add(new Diagnostic(
                        "GK3R3002", DiagnosticSeverity.Warning,
                        $"Edit adds '{edit.TargetId}' but carries no item.",
                        documentId, null, "a complete item", "null",
                        "An add edit must include the item it adds."));
                    break;

                case EditOperation.Add:
                    items.Add(edit.Item!);
                    break;

                case EditOperation.Modify when index < 0:
                    diagnostics.Add(new Diagnostic(
                        "GK3R3003", DiagnosticSeverity.Warning,
                        $"Edit modifies '{edit.TargetId}', which the baseline no longer contains.",
                        documentId, null, "an id present in the baseline", edit.TargetId,
                        "The generator stopped producing this item. Delete the stale edit, or convert it to an add."));
                    break;

                case EditOperation.Modify when edit.Patch is null:
                    diagnostics.Add(new Diagnostic(
                        "GK3R3004", DiagnosticSeverity.Warning,
                        $"Edit modifies '{edit.TargetId}' but carries no patch.",
                        documentId, null, "a patch", "null",
                        "A modify edit must include the fields it changes."));
                    break;

                case EditOperation.Modify:
                    items[index] = items[index].ApplyPatch(edit.Patch!).MarkEdited();
                    break;

                case EditOperation.Remove when index < 0:
                    diagnostics.Add(new Diagnostic(
                        "GK3R3005", DiagnosticSeverity.Warning,
                        $"Edit removes '{edit.TargetId}', which the baseline no longer contains.",
                        documentId, null, "an id present in the baseline", edit.TargetId,
                        "The generator already stopped producing this item. The edit can be deleted."));
                    break;

                case EditOperation.Remove:
                    items.RemoveAt(index);
                    break;

                default:
                    break;
            }
        }

        return items;
    }
}
