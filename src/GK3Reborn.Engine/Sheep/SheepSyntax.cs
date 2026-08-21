namespace GK3Reborn.Sheep;

/// <summary>Anything that can appear as an expression.</summary>
/// <remarks>
/// A closed hierarchy: the grammar in the language reference has exactly these forms, and
/// sealing it is what lets the compiler switch over them without a default case that means
/// "something was added and this was not updated".
/// </remarks>
public abstract record SheepExpressionNode
{
    private protected SheepExpressionNode()
    {
    }
}

/// <summary>A whole-number constant.</summary>
/// <param name="Value">Its value.</param>
public sealed record SheepIntegerNode(int Value) : SheepExpressionNode;

/// <summary>A constant with a fractional part.</summary>
/// <param name="Value">Its value.</param>
public sealed record SheepFloatNode(float Value) : SheepExpressionNode;

/// <summary>A quoted string constant.</summary>
/// <param name="Value">Its contents, without the quotes.</param>
public sealed record SheepStringNode(string Value) : SheepExpressionNode;

/// <summary>A reference to a declared variable.</summary>
/// <param name="Name">Its name, as written.</param>
public sealed record SheepVariableNode(string Name) : SheepExpressionNode;

/// <summary>A call to a system function.</summary>
/// <param name="Name">Its name, as written.</param>
/// <param name="Arguments">Its arguments, in order.</param>
public sealed record SheepCallNode(
    string Name, IReadOnlyList<SheepExpressionNode> Arguments) : SheepExpressionNode;

/// <summary>One of the language's unary operators.</summary>
/// <param name="Operator">Either <c>-</c> or <c>!</c>.</param>
/// <param name="Operand">What it applies to.</param>
public sealed record SheepUnaryNode(string Operator, SheepExpressionNode Operand)
    : SheepExpressionNode;

/// <summary>One of the language's binary operators.</summary>
/// <param name="Operator">The operator, as written.</param>
/// <param name="Left">Its left operand.</param>
/// <param name="Right">Its right operand.</param>
public sealed record SheepBinaryNode(
    string Operator, SheepExpressionNode Left, SheepExpressionNode Right) : SheepExpressionNode;

/// <summary>Anything that can appear as a statement.</summary>
public abstract record SheepStatementNode
{
    private protected SheepStatementNode()
    {
    }

    /// <summary>Which line of the source it came from, for diagnostics.</summary>
    public int Line { get; init; }
}

/// <summary>A braced group of statements.</summary>
/// <param name="Statements">What is inside it.</param>
public sealed record SheepBlockNode(IReadOnlyList<SheepStatementNode> Statements)
    : SheepStatementNode;

/// <summary>An expression evaluated for what it does rather than what it is.</summary>
/// <param name="Expression">The expression, in practice always a call.</param>
public sealed record SheepExpressionStatementNode(SheepExpressionNode Expression)
    : SheepStatementNode;

/// <summary>Putting a value in a variable.</summary>
/// <param name="Name">The variable.</param>
/// <param name="Value">What to put in it.</param>
public sealed record SheepAssignmentNode(string Name, SheepExpressionNode Value)
    : SheepStatementNode;

/// <summary>A conditional.</summary>
/// <param name="Condition">What decides.</param>
/// <param name="Then">What happens when it holds.</param>
/// <param name="Else">What happens when it does not, or null.</param>
public sealed record SheepIfNode(
    SheepExpressionNode Condition, SheepStatementNode Then, SheepStatementNode? Else)
    : SheepStatementNode;

/// <summary>Leaving the function.</summary>
public sealed record SheepReturnNode : SheepStatementNode;

/// <summary>A place a <c>goto</c> can name.</summary>
/// <param name="Name">Its name, which ends in a dollar like any user identifier.</param>
public sealed record SheepLabelNode(string Name) : SheepStatementNode;

/// <summary>Jumping to a label.</summary>
/// <param name="Label">Where to jump.</param>
public sealed record SheepGotoNode(string Label) : SheepStatementNode;

/// <summary>Stopping the thread where it stands.</summary>
public sealed record SheepSitnSpinNode : SheepStatementNode;

/// <summary>Handing control to the debugger.</summary>
public sealed record SheepBreakpointNode : SheepStatementNode;

/// <summary>
/// Waiting for the calls inside it to finish before going on.
/// </summary>
/// <param name="Body">
/// What to wait for. The language allows three forms — a bare <c>wait;</c>, a wait applied
/// to one call, and a wait applied to a braced group — and the first is a wait with nothing
/// in it.
/// </param>
public sealed record SheepWaitNode(SheepStatementNode? Body) : SheepStatementNode;

/// <summary>A function a script defines.</summary>
/// <param name="Name">Its name, which ends in a dollar.</param>
/// <param name="Body">Its statements.</param>
/// <param name="Line">Which line it was declared on.</param>
public sealed record SheepFunctionNode(
    string Name, IReadOnlyList<SheepStatementNode> Body, int Line);

/// <summary>A variable a script declares.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Its declared type.</param>
/// <param name="Initial">Its initial value, or null for the type's own zero.</param>
/// <param name="Line">Which line it was declared on.</param>
public sealed record SheepSymbolNode(
    string Name, SheepValueKind Kind, SheepExpressionNode? Initial, int Line);

/// <summary>A whole script: its symbols and its functions.</summary>
/// <param name="Name">The name it was parsed under.</param>
/// <param name="Symbols">What it declares, in declaration order.</param>
/// <param name="Functions">What it defines, in definition order.</param>
/// <remarks>
/// Both halves are optional in the grammar. A script with a <c>symbols</c> block and no
/// <c>code</c> block declares state and does nothing with it, which is legal and useless;
/// the other way round is ordinary and common.
/// </remarks>
public sealed record SheepScriptNode(
    string Name,
    IReadOnlyList<SheepSymbolNode> Symbols,
    IReadOnlyList<SheepFunctionNode> Functions);
