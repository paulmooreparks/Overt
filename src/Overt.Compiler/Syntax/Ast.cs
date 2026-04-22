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

/// <summary>
/// FFI import — a function declared in Overt but implemented in a host language. The
/// shape, per DESIGN.md §17:
/// <code>
///   [unsafe] extern "platform" fn name(params) !{effects} -> Type
///       binds "target.symbol"
///       [from  "libname"]        // C only
/// </code>
/// Only C FFI requires <c>unsafe</c> and the <c>from</c> clause.
/// </summary>
public sealed record ExternDecl(
    string Platform,
    bool IsUnsafe,
    string Name,
    ImmutableArray<Parameter> Parameters,
    EffectRow? Effects,
    TypeExpr? ReturnType,
    string BindsTarget,
    string? FromLibrary,
    SourceSpan Span) : Declaration(Span);

public sealed record RecordDecl(
    string Name,
    ImmutableArray<Annotation> Annotations,
    ImmutableArray<RecordField> Fields,
    SourceSpan Span) : Declaration(Span);

public sealed record RecordField(
    string Name,
    TypeExpr Type,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>
/// Sum type: <c>enum Name { Variant1, Variant2 { field: Type, ... }, ... }</c>. Variants
/// with no fields are "bare" (e.g. <c>Pending</c>); variants with fields carry named data
/// (Rust-struct-like; tuple-struct variants are not used in v1).
/// </summary>
public sealed record EnumDecl(
    string Name,
    ImmutableArray<Annotation> Annotations,
    ImmutableArray<EnumVariant> Variants,
    SourceSpan Span) : Declaration(Span);

public sealed record EnumVariant(
    string Name,
    ImmutableArray<RecordField> Fields,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>
/// An annotation attached to a declaration: <c>@name(arg, arg, ...)</c>. V1 uses attributes
/// only for <c>@derive</c>, which takes a list of identifier arguments naming stdlib
/// derive kinds (Debug, Clone, etc — DESIGN.md §15). Arguments are stored as raw
/// identifier strings and interpreted by later passes.
/// </summary>
public sealed record Annotation(
    string Name,
    ImmutableArray<string> Arguments,
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
/// A <c>let</c> or <c>let mut</c> binding. The target is a pattern — most commonly a
/// single <see cref="IdentifierPattern"/>, but tuple-destructuring lets like
/// <c>let (users, orders) = parallel { ... }</c> land as <see cref="TuplePattern"/>.
/// Irrefutability is enforced by the type checker; the parser is liberal about pattern
/// shape. <see cref="IsMutable"/> permits subsequent rebinding via
/// <see cref="AssignmentStmt"/> and is only valid for single-identifier targets.
/// </summary>
public sealed record LetStmt(
    Pattern Target,
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
/// Record literal: <c>Name { field = value, ... }</c> or <c>Mod.Type { ... }</c>. The type
/// is represented as an expression (always an <see cref="IdentifierExpr"/> or a
/// <see cref="FieldAccessExpr"/> chain of identifiers) so dotted paths like
/// <c>Tree.Node { ... }</c> and enum-variant-record literals like
/// <c>OrderStatus.Delivered { ... }</c> fit the same node. Disambiguated from a bare
/// identifier followed by a block at parse time by the field-initializer shape
/// <c>Ident =</c>; in condition-like positions (<c>if</c>, <c>while</c>, <c>match</c>)
/// record literals are disabled entirely to avoid ambiguity with the form's opening brace.
/// </summary>
public sealed record RecordLiteralExpr(
    Expression TypeTarget,
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

/// <summary>
/// <c>parallel { expr, expr, ... }</c> — each expression runs as an independent task.
/// Block yields a tuple of results in source order; any failure cancels siblings and
/// propagates (DESIGN.md §12).
/// </summary>
public sealed record ParallelExpr(
    ImmutableArray<Expression> Tasks,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// <c>race { expr, expr, ... }</c> — returns the first task to succeed; remaining tasks
/// are cancelled. If all fail, yields <c>Err(RaceAllFailed&lt;E&gt;)</c> (DESIGN.md §12).
/// </summary>
public sealed record RaceExpr(
    ImmutableArray<Expression> Tasks,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// <c>unsafe { body }</c> — a block whose value is the body's value, tagged as an
/// unsafe region (DESIGN.md §17). Required to wrap calls into C FFI bindings.
/// </summary>
public sealed record UnsafeExpr(
    BlockExpr Body,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// <c>trace { body }</c> — a trace block (DESIGN.md §14). Evaluates to the body's
/// value; emits structured trace events to any registered consumer. Zero-cost when
/// no consumer is subscribed.
/// </summary>
public sealed record TraceExpr(
    BlockExpr Body,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// Tuple literal: <c>(a, b, c)</c> with two or more elements. One-element tuples are not
/// a thing in Overt; <c>(a)</c> is a parenthesized expression, <c>()</c> is the unit value.
/// </summary>
public sealed record TupleExpr(
    ImmutableArray<Expression> Elements,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// <c>match scrutinee { pattern =&gt; expr, ... }</c>. Exhaustiveness is enforced later.
/// </summary>
public sealed record MatchExpr(
    Expression Scrutinee,
    ImmutableArray<MatchArm> Arms,
    SourceSpan Span) : Expression(Span);

public sealed record MatchArm(
    Pattern Pattern,
    Expression Body,
    SourceSpan Span) : SyntaxNode(Span);

// ---------- Patterns ----------

public abstract record Pattern(SourceSpan Span) : SyntaxNode(Span);

/// <summary>The <c>_</c> pattern — matches anything, binds nothing.</summary>
public sealed record WildcardPattern(SourceSpan Span) : Pattern(Span);

/// <summary>
/// A single-identifier pattern. Ambiguous at parse time: could be a binding or a
/// zero-argument variant reference (e.g. <c>Empty</c> for <c>Tree.Empty</c> in scope).
/// Later semantic passes resolve which.
/// </summary>
public sealed record IdentifierPattern(string Name, SourceSpan Span) : Pattern(Span);

/// <summary>
/// A dotted path pattern with no arguments: <c>Tree.Empty</c>, <c>OrderStatus.Pending</c>.
/// Always resolves to a constant / zero-argument variant reference (no binding).
/// </summary>
public sealed record PathPattern(
    ImmutableArray<string> Path,
    SourceSpan Span) : Pattern(Span);

/// <summary>
/// A constructor pattern with positional arguments: <c>Ok(value)</c>, <c>Err(cause)</c>.
/// </summary>
public sealed record ConstructorPattern(
    ImmutableArray<string> Path,
    ImmutableArray<Pattern> Arguments,
    SourceSpan Span) : Pattern(Span);

/// <summary>
/// A record-destructuring pattern: <c>Tree.Node { value = v, left = l, right = r }</c>.
/// Each field pattern names a field of the record/variant and supplies a subpattern for it.
/// </summary>
public sealed record RecordPattern(
    ImmutableArray<string> Path,
    ImmutableArray<FieldPattern> Fields,
    SourceSpan Span) : Pattern(Span);

public sealed record FieldPattern(
    string Name,
    Pattern Subpattern,
    SourceSpan Span) : SyntaxNode(Span);

/// <summary>
/// A tuple pattern: <c>(p1, p2, ...)</c>. Arity must match the scrutinee.
/// </summary>
public sealed record TuplePattern(
    ImmutableArray<Pattern> Elements,
    SourceSpan Span) : Pattern(Span);

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
