using System.Collections.Immutable;

namespace Overt.Compiler.Syntax;

/// <summary>
/// Base type for every AST node. Every node carries a <see cref="SourceSpan"/> covering
/// the full range of source text from which it was parsed. Spans are the primary currency
/// for diagnostics, formatter output, and future LSP integration.
/// </summary>
public abstract record SyntaxNode(SourceSpan Span);

// ---------- Module ----------

public sealed record ModuleDecl(
    string Name,
    ImmutableArray<Declaration> Declarations,
    SourceSpan Span) : SyntaxNode(Span);

// ---------- Declarations ----------

public abstract record Declaration(SourceSpan Span) : SyntaxNode(Span);

public sealed record FunctionDecl(
    string Name,
    ImmutableArray<Parameter> Parameters,
    EffectRow? Effects,
    TypeExpr? ReturnType,
    BlockExpr Body,
    SourceSpan Span) : Declaration(Span);

public sealed record Parameter(
    string Name,
    TypeExpr Type,
    SourceSpan Span) : SyntaxNode(Span);

public sealed record EffectRow(
    ImmutableArray<string> Effects,
    SourceSpan Span) : SyntaxNode(Span);

// ---------- Type expressions ----------

public abstract record TypeExpr(SourceSpan Span) : SyntaxNode(Span);

public sealed record NamedType(
    string Name,
    ImmutableArray<TypeExpr> TypeArguments,
    SourceSpan Span) : TypeExpr(Span);

/// <summary>The unit type, spelled <c>()</c>.</summary>
public sealed record UnitType(SourceSpan Span) : TypeExpr(Span);

// ---------- Statements ----------

public abstract record Statement(SourceSpan Span) : SyntaxNode(Span);

/// <summary>A bare expression in statement position. The expression's value is discarded.</summary>
public sealed record ExpressionStmt(
    Expression Expression,
    SourceSpan Span) : Statement(Span);

// ---------- Expressions ----------

public abstract record Expression(SourceSpan Span) : SyntaxNode(Span);

public sealed record IdentifierExpr(string Name, SourceSpan Span) : Expression(Span);

/// <summary>A string literal with no interpolations. <see cref="Value"/> is the raw lexeme
/// including surrounding quotes; escape-sequence decoding happens at a later pass.</summary>
public sealed record StringLiteralExpr(string Value, SourceSpan Span) : Expression(Span);

/// <summary>The unit value, spelled <c>()</c>.</summary>
public sealed record UnitExpr(SourceSpan Span) : Expression(Span);

public sealed record CallExpr(
    Expression Callee,
    ImmutableArray<Argument> Arguments,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// An argument in a call-expression argument list. <see cref="Name"/> is <c>null</c> for
/// positional arguments — only valid when there is exactly one argument total (§7's
/// single-parameter unambiguous call rule, enforced at parse time).
/// </summary>
public sealed record Argument(
    string? Name,
    Expression Value,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>Postfix <c>?</c>: propagate <c>Err</c>, unwrap <c>Ok</c>.</summary>
public sealed record PropagateExpr(
    Expression Operand,
    SourceSpan Span) : Expression(Span);

/// <summary>A block expression: <c>{ stmt; stmt; trailingExpr? }</c>. If
/// <see cref="TrailingExpression"/> is null the block evaluates to <c>()</c>.</summary>
public sealed record BlockExpr(
    ImmutableArray<Statement> Statements,
    Expression? TrailingExpression,
    SourceSpan Span) : Expression(Span);
