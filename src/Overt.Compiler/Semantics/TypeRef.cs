using System.Collections.Immutable;

namespace Overt.Compiler.Semantics;

/// <summary>
/// A type. The IR the type checker produces and later passes consume. Separate from
/// <c>TypeExpr</c> (which is the syntactic form in the AST) so that resolution,
/// instantiation, and unification can happen without mutating source-level nodes.
/// </summary>
public abstract record TypeRef
{
    /// <summary>One-line display form for diagnostics and the <c>--emit=typed</c> dump.</summary>
    public abstract string Display { get; }
    public override string ToString() => Display;
}

/// <summary>
/// Placeholder for a type the checker could not infer. Propagates through expressions
/// so one unknown doesn't cascade into noise; downstream code treats this as "no type
/// constraint." NOT emitted as a user-facing error on its own.
/// </summary>
public sealed record UnknownType : TypeRef
{
    public static readonly UnknownType Instance = new();
    public override string Display => "?";
    private UnknownType() { }
}

/// <summary>
/// The "bottom" type — the type of expressions that don't return a
/// value to the surrounding context (currently just <c>return expr</c>;
/// future <c>panic</c> / infinite-loop forms would share it). Unifies
/// with any expected type because the surrounding code never observes
/// a value of this type — control flow has already left.
/// </summary>
public sealed record NeverType : TypeRef
{
    public static readonly NeverType Instance = new();
    public override string Display => "Never";
    private NeverType() { }
}

/// <summary>
/// A primitive type (<c>Int</c>, <c>Float</c>, <c>Bool</c>, <c>String</c>, <c>Unit</c>).
/// Identified by name; the backends map these to host-language primitives.
/// </summary>
public sealed record PrimitiveType(string Name) : TypeRef
{
    public static readonly PrimitiveType Int = new("Int");
    public static readonly PrimitiveType Int64 = new("Int64");
    public static readonly PrimitiveType Float = new("Float");
    public static readonly PrimitiveType Bool = new("Bool");
    public static readonly PrimitiveType String = new("String");
    public static readonly PrimitiveType Unit = new("Unit");

    public override string Display => Name;
}

/// <summary>
/// A reference to a declared type — user-defined record, enum, type alias, or a stdlib
/// type the resolver's allow-list knows about (Result, Option, List, IoError, etc).
/// Generic instantiation is captured by <see cref="TypeArguments"/>; arity correctness
/// is the checker's job.
/// </summary>
public sealed record NamedTypeRef(
    string Name,
    ImmutableArray<TypeRef> TypeArguments) : TypeRef
{
    public NamedTypeRef(string name) : this(name, ImmutableArray<TypeRef>.Empty) { }

    public override string Display =>
        TypeArguments.Length == 0
            ? Name
            : $"{Name}<{string.Join(", ", TypeArguments.Select(a => a.Display))}>";
}

/// <summary>
/// A function type, used for both first-class <c>fn(...)</c> parameter types and for
/// the effective type of declared functions. Effects are captured as a list of names
/// (concrete effects like <c>io</c> and effect-row type variables like <c>E</c>);
/// v1 does not distinguish them at the type level.
/// </summary>
public sealed record FunctionTypeRef(
    ImmutableArray<TypeRef> Parameters,
    TypeRef Return,
    ImmutableArray<string> Effects) : TypeRef
{
    public override string Display
    {
        get
        {
            var parts = string.Join(", ", Parameters.Select(p => p.Display));
            var eff = Effects.Length == 0 ? "" : $" !{{{string.Join(", ", Effects)}}}";
            return $"fn({parts}){eff} -> {Return.Display}";
        }
    }
}

/// <summary>Tuple type with two or more element types.</summary>
public sealed record TupleTypeRef(ImmutableArray<TypeRef> Elements) : TypeRef
{
    public override string Display =>
        $"({string.Join(", ", Elements.Select(e => e.Display))})";
}

/// <summary>
/// A reference to a generic type parameter bound in the current declaration scope
/// (e.g. <c>T</c>, <c>E</c> on a generic function). Unified during inference.
/// </summary>
public sealed record TypeVarRef(string Name) : TypeRef
{
    public override string Display => Name;
}
