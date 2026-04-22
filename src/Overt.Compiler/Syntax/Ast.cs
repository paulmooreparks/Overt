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

public sealed record RecordDecl(
    string Name,
    ImmutableArray<RecordField> Fields,
    SourceSpan Span) : Declaration(Span);

public sealed record RecordField(
    string Name,
    TypeExpr Type,
    SourceSpan Span) : SyntaxNode(Span);

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

/// <summary>
/// A <c>let</c> or <c>let mut</c> binding. Per DESIGN.md §10, <see cref="IsMutable"/>
/// permits subsequent rebinding via <see cref="AssignmentStmt"/>, but the binding remains
/// scalar-local; no shared mutable state.
/// </summary>
public sealed record LetStmt(
    string Name,
    bool IsMutable,
    TypeExpr? Type,
    Expression Initializer,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// Rebinding assignment for a <c>let mut</c> binding. Not an expression — assignment
/// does not produce a value in Overt (unlike C-family languages).
/// </summary>
public sealed record AssignmentStmt(
    string Name,
    Expression Value,
    SourceSpan Span) : Statement(Span);

// ---------- Expressions ----------

public abstract record Expression(SourceSpan Span) : SyntaxNode(Span);

public sealed record IdentifierExpr(string Name, SourceSpan Span) : Expression(Span);

/// <summary>A string literal with no interpolations. <see cref="Value"/> is the raw lexeme
/// including surrounding quotes; escape-sequence decoding happens at a later pass.</summary>
public sealed record StringLiteralExpr(string Value, SourceSpan Span) : Expression(Span);

/// <summary>
/// An interpolated string: <c>"literal $name literal ${expr} literal"</c>. The parts
/// sequence alternates literal/interpolation — starts and ends with a literal part
/// (possibly empty), so a string with N interpolations has 2N+1 parts.
/// </summary>
public sealed record InterpolatedStringExpr(
    ImmutableArray<StringPart> Parts,
    SourceSpan Span) : Expression(Span);

public abstract record StringPart(SourceSpan Span) : SyntaxNode(Span);

/// <summary>A run of literal text inside an interpolated string. May be empty.</summary>
public sealed record StringLiteralPart(string Text, SourceSpan Span) : StringPart(Span);

/// <summary>An interpolated expression — either the <c>$ident</c> shorthand (which is
/// always an <see cref="IdentifierExpr"/>) or a full <c>${expr}</c> with any expression.</summary>
public sealed record StringInterpolationPart(
    Expression Expression,
    SourceSpan Span) : StringPart(Span);

/// <summary>Integer literal. <see cref="Lexeme"/> is the raw source form including any
/// underscore separators and base prefix (<c>0x</c>/<c>0b</c>). Semantic value is computed
/// at a later pass so the original spelling survives for diagnostics and formatting.</summary>
public sealed record IntegerLiteralExpr(string Lexeme, SourceSpan Span) : Expression(Span);

/// <summary>Float literal. <see cref="Lexeme"/> is the raw source form.</summary>
public sealed record FloatLiteralExpr(string Lexeme, SourceSpan Span) : Expression(Span);

public sealed record BooleanLiteralExpr(bool Value, SourceSpan Span) : Expression(Span);

/// <summary>The unit value, spelled <c>()</c>.</summary>
public sealed record UnitExpr(SourceSpan Span) : Expression(Span);

public sealed record FieldAccessExpr(
    Expression Target,
    string FieldName,
    SourceSpan Span) : Expression(Span);

public enum BinaryOp
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    LogicalAnd,
    LogicalOr,
    PipeCompose,
    PipePropagate,
}

public sealed record BinaryExpr(
    BinaryOp Op,
    Expression Left,
    Expression Right,
    SourceSpan Span) : Expression(Span);

public enum UnaryOp
{
    Negate,
    LogicalNot,
}

public sealed record UnaryExpr(
    UnaryOp Op,
    Expression Operand,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// <c>if cond { then } else? { else }</c>. The <c>else</c> arm is optional (DESIGN.md §4);
/// when <see cref="Else"/> is <c>null</c>, the form desugars to an implicit <c>else { () }</c>
/// and the then block's value-type must be <c>()</c> — the type checker enforces.
/// </summary>
public sealed record IfExpr(
    Expression Condition,
    BlockExpr Then,
    BlockExpr? Else,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// Record literal: <c>Name { field = value, ... }</c>. Disambiguated from a bare identifier
/// followed by a block at parse time by the field-initializer shape <c>Ident =</c>; in
/// condition-like positions (<c>if</c>, <c>while</c>, <c>match</c>) record literals are
/// disabled entirely to avoid ambiguity with the form's opening brace.
/// </summary>
public sealed record RecordLiteralExpr(
    string TypeName,
    ImmutableArray<FieldInit> Fields,
    SourceSpan Span) : Expression(Span);

public sealed record FieldInit(
    string Name,
    Expression Value,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>
/// Record copy-with-modification: <c>record_expr with { field = value, ... }</c>.
/// Produces a new record identical to <see cref="Target"/> except for the listed updates.
/// Postfix in the operator grammar: binds tighter than any infix operator, chains with
/// calls, field access, and propagate.
/// </summary>
public sealed record WithExpr(
    Expression Target,
    ImmutableArray<FieldInit> Updates,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// <c>while cond { body }</c>. Evaluates to <c>()</c>; the body's trailing expression,
/// if any, is discarded each iteration. Per DESIGN.md §4, distinct from <c>loop</c> and
/// <c>for each</c>.
/// </summary>
public sealed record WhileExpr(
    Expression Condition,
    BlockExpr Body,
    SourceSpan Span) : Expression(Span);

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
