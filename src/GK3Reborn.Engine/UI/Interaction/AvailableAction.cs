namespace GK3Reborn.UI.Interaction;

/// <summary>How an action is presented and confirmed.</summary>
public enum ActionCategory
{
    /// <summary>Look at or examine. Bound to left click and never destructive.</summary>
    Inspect,

    /// <summary>The context-appropriate default action for this target.</summary>
    Primary,

    /// <summary>Consumes an item, closes a path, or is otherwise hard to undo.</summary>
    Destructive,
}

/// <summary>
/// One action the player may take on a target right now.
/// </summary>
/// <remarks>
/// <para>
/// Plan/03-gameplay-ui-audio.md section 2.3. The resolver returns these ordered; the
/// UI decides whether to invoke directly or show a chooser. Execution still goes
/// through the original NVC/Sheep semantics: modernizing input must not change what
/// an action does.
/// </para>
/// <para>
/// <see cref="PredictedInventoryUse"/> exists for display only. Nothing in this record
/// may mutate game state - a resolver that consumed an item to find out what would
/// happen would corrupt the save.
/// </para>
/// </remarks>
public sealed record AvailableAction
{
    /// <summary>Stable id for logging, tests and rebinding.</summary>
    public required string ActionId { get; init; }

    /// <summary>Where this action came from in the original data.</summary>
    public required string NvcProvenance { get; init; }

    /// <summary>Localized verb shown to the player.</summary>
    public required string LocalizedVerb { get; init; }

    /// <summary>Longer localized description, when one helps.</summary>
    public string? LocalizedDescription { get; init; }

    /// <summary>Semantic icon name; icons are always paired with text.</summary>
    public required string IconSemantic { get; init; }

    /// <summary>Presentation category.</summary>
    public required ActionCategory Category { get; init; }

    /// <summary>Whether the action can currently be taken.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Why it is disabled, when the player has asked to see reasons.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>Inventory item this would likely consume. Display only.</summary>
    public string? PredictedInventoryUse { get; init; }
}
