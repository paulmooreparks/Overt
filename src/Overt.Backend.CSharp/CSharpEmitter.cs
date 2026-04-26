using System.Collections.Immutable;
using System.Text;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Backend.CSharp;

/// <summary>
/// First-pass C# transpiler. Walks the Overt AST and emits C# source text.
///
/// The emitter writes <c>#line</c> directives at every statement and declaration
/// boundary so that the C# compiler's portable PDB maps runtime errors, debug-time
/// stack traces, and set-next-statement back to the original <c>.ov</c> source — not
/// the generated <c>.cs</c> file. This is the anti-hack defense: an exception at
/// runtime points at <c>bst.ov:23</c>, not the generated method, so there is no
/// reason to open the <c>.cs</c> at all.
///
/// Three layers keep edits out of the generated C#:
/// <list type="bullet">
///   <item>Every output file carries an <c>&lt;auto-generated&gt;</c> header, which
///     Roslyn analyzers, StyleCop, and IDE refactorings treat as read-only.</item>
///   <item>The preamble warns against editing explicitly.</item>
///   <item>Consumers are expected to put transpiled <c>.cs</c> under <c>obj/</c> or a
///     dedicated <c>generated/</c> directory so local edits are overwritten on the
///     next build. (Consumer responsibility, not the emitter's to enforce.)</item>
/// </list>
///
/// Deliberately untyped where it can be: no symbol resolution, no effect inference,
/// no type checking. Constructs that need semantic information to emit correctly
/// consume the optional <see cref="TypeCheckResult"/> at construction and otherwise
/// produce placeholder code with a <c>// TODO:</c> comment.
///
/// Rough mapping:
/// <list type="bullet">
///   <item>Module → <c>namespace Overt.Generated.&lt;Name&gt;; public static class Module</c></item>
///   <item>fn → <c>public static</c> method on the module class</item>
///   <item>record → C# <c>sealed record</c></item>
///   <item>enum → abstract base record + one sealed record per variant</item>
///   <item>type alias → <c>using Name = Target;</c> (no refinement enforcement yet)</item>
///   <item>extern → method stub with a <c>// extern</c> comment</item>
///   <item>if/match/with/interp → C# expression forms (<c>?:</c>, <c>switch</c>, <c>with</c>, <c>$"..."</c>)</item>
///   <item>pipe (<c>|&gt;</c>) → call-arg splice at emit time</item>
///   <item>? propagation → <c>.Unwrap()</c> (runtime shim; full lowering comes with the type checker)</item>
///   <item>parallel/race/trace/unsafe → placeholder wrappers</item>
///   <item>effect rows → discarded</item>
/// </list>
/// </summary>
public sealed class CSharpEmitter
{
    private readonly IndentedWriter _w;
    private readonly TypeCheckResult? _types;

    /// <summary>
    /// The Overt source file path that <c>#line</c> directives point at. Null disables
    /// line-directive emission (used by tests that don't care about debug mapping).
    /// </summary>
    private readonly string? _sourcePath;

    /// <summary>
    /// The expected type of the expression currently being emitted, propagated from
    /// parent context (function return, call argument, if/match arm). Used by
    /// generic-call emission to fill in type arguments that C# can't infer on its own
    /// — chiefly no-arg methods like <c>List.empty()</c> and <c>None()</c>.
    /// Null when the expected type is not known.
    /// </summary>
    private TypeRef? _expectedType;

    /// <summary>
    /// Maps each <see cref="PropagateExpr"/> that's been hoisted to a local variable
    /// holding its unwrapped Ok value. When the expression walker later visits the
    /// <c>?</c> node, it emits the local name instead of <c>.Unwrap()</c>, so the
    /// happy path is a field read and the Err path has already returned. Keyed by
    /// source span because AST nodes are records and would compare by structure.
    /// </summary>
    private readonly Dictionary<SourceSpan, string> _hoistMap = new();

    /// <summary>
    /// Maps an <c>if</c>/<c>match</c> subtree's span to a pre-emitted local name
    /// that holds its value. Populated when the statement-level let-lowering
    /// lifts a nested conditional that contains a <c>?</c> site — the nested
    /// if/match can't be hoisted inline (branches would evaluate eagerly), so
    /// we pre-compute it with the same stmt-lowered shape and substitute the
    /// local at the original site during expression emission.
    /// </summary>
    private readonly Dictionary<SourceSpan, string> _liftedConditionals = new();

    /// <summary>Unique-name counter for hoisted <c>?</c> temporaries.</summary>
    private int _propagateCounter;

    /// <summary>Unique-name counter for lifted nested if/match temporaries.</summary>
    private int _liftCounter;

    /// <summary>
    /// Enclosing function's declared return type, set by <see cref="EmitFunction"/>.
    /// Used by the <c>?</c>-hoisting pass to derive the error type to propagate when
    /// the operand's Result shape cannot be recovered from type annotations alone
    /// (e.g., <c>|&gt;?</c> where generic inference across the pipe isn't wired up).
    /// </summary>
    private TypeRef? _currentFnReturn;

    private CSharpEmitter(
        IndentedWriter w,
        TypeCheckResult? types,
        ResolutionResult? resolution,
        string? sourcePath)
    {
        _w = w;
        _types = types;
        _resolution = resolution;
        _sourcePath = sourcePath;
    }

    public static string Emit(
        ModuleDecl module,
        TypeCheckResult? types = null,
        string? sourcePath = null)
        => Emit(module, types, resolution: null, sourcePath);

    public static string Emit(
        ModuleDecl module,
        TypeCheckResult? types,
        ResolutionResult? resolution,
        string? sourcePath = null)
    {
        var sb = new StringBuilder();
        var emitter = new CSharpEmitter(new IndentedWriter(sb), types, resolution, sourcePath);
        emitter.EmitModule(module);
        return sb.ToString();
    }

    /// <summary>
    /// Optional resolution data from the name-resolver pass. Lets the
    /// emitter look up which symbol an <see cref="IdentifierExpr"/>
    /// refers to — used by the let-shadow rebinding to substitute
    /// references to a renamed binding, and only those references.
    /// </summary>
    private readonly ResolutionResult? _resolution;

    /// <summary>
    /// Emit a <c>#line N "path"</c> directive so the C# compiler writes PDB entries
    /// pointing at <paramref name="span"/>'s origin in the <c>.ov</c> file. A no-op
    /// when <see cref="_sourcePath"/> is null. Always writes at column 0 — C#
    /// directives must start a line.
    /// </summary>
    private void EmitLineDirective(SourceSpan span)
    {
        if (_sourcePath is null) return;
        // `#line` directives must begin at column 0. If we're mid-line (inside
        // an IIFE wrapping for block-as-expression, say), break to a new line
        // first so the directive doesn't end up after other tokens — C# rejects
        // CS1040 "Preprocessor directives must appear as the first non-whitespace
        // character on a line" otherwise.
        if (!_w.AtLineStart) _w.WriteLine();

        // C# allows column info via `#line (startLine, startCol) - (endLine, endCol) "path"`.
        // Portable PDBs honor the columns; older tooling ignores extras gracefully.
        _w.WriteLine(
            $"#line ({span.Start.Line}, {span.Start.Column}) - "
                + $"({span.End.Line}, {span.End.Column}) \"{_sourcePath.Replace("\\", "\\\\")}\"");
    }

    /// <summary>Reset to generated-file line numbering. Emitted around preamble
    /// and trailing machinery so stack traces of compile-generated scaffolding
    /// don't accidentally point into the middle of <c>.ov</c> source.</summary>
    private void EmitLineDefault()
    {
        if (_sourcePath is null) return;
        _w.WriteLine("#line default");
    }

    /// <summary>
    /// Best-effort type lookup for an expression. Returns <see cref="UnknownType"/> when
    /// no type-check result is available or the expression wasn't annotated.
    /// </summary>
    private TypeRef TypeOf(Expression e)
        => _types?.ExpressionTypes.TryGetValue(e.Span, out var t) == true
            ? t
            : UnknownType.Instance;

    /// <summary>
    /// Run <paramref name="action"/> with <see cref="_expectedType"/> temporarily set,
    /// then restore the prior value. This is the basic plumbing for expected-type
    /// propagation through nested expressions.
    /// </summary>
    private void WithExpected(TypeRef? expected, Action action)
    {
        var saved = _expectedType;
        _expectedType = expected;
        try { action(); }
        finally { _expectedType = saved; }
    }

    // ------------------------------------------------------------- module

    private void EmitModule(ModuleDecl module)
    {
        _w.WriteLine("// <auto-generated>");
        _w.WriteLine($"// Transpiled from Overt module `{module.Name}`.");
        _w.WriteLine("// DO NOT EDIT THIS FILE. Edits are overwritten on every build.");
        _w.WriteLine("// Debug symbols (portable PDB) map runtime errors and breakpoints");
        _w.WriteLine("// back to the original .ov source via the #line directives below;");
        _w.WriteLine("// stack traces, debuggers, and source-link tooling all resolve to");
        _w.WriteLine("// the Overt file, not this generated C#.");
        _w.WriteLine("// </auto-generated>");
        _w.WriteLine("#nullable enable");
        // Suppress analyzer warnings the user can't act on:
        //   CS8981 — lowercase Overt-side aliases (e.g. `as int32`) become
        //            lowercase C# identifiers; the compiler reserves those
        //            names for future language use, but the alias choice
        //            comes from user-authored Overt source, not us.
        //   CS0618 — bulk-imported BCL types include obsolete members
        //            (e.g. System.String.Copy); BindGenerator surfaces all
        //            renderable methods so the user has the full surface,
        //            but Csc complains about the obsolete bindings whether
        //            or not the user calls them.
        _w.WriteLine("#pragma warning disable CS8981, CS0618");
        _w.WriteLine();
        _w.WriteLine("using System;");
        _w.WriteLine("using System.Threading.Tasks;");
        _w.WriteLine("using Overt.Runtime;");
        _w.WriteLine("using static Overt.Runtime.Prelude;");

        // For each imported module, emit the appropriate C# `using`:
        //   - Selective: `using static Overt.Generated.Path.Module;`
        //     brings the module's static methods into scope unqualified.
        //   - Aliased: `using Alias = Overt.Generated.Path.Module;`
        //     so that Overt-side `alias.fn(...)` lowers to C# `Alias.fn(...)`
        //     without any special-casing in the expression emitter.
        foreach (var use in module.Declarations.OfType<UseDecl>())
        {
            var ns = ToEmittedNamespace(use.ModulePath);
            if (use.Alias is { } alias)
            {
                _w.WriteLine($"using {alias} = {ns}.Module;");
            }
            else
            {
                _w.WriteLine($"using static {ns}.Module;");
            }
        }

        // Extern opaque-type declarations map to C# type aliases so a name
        // like `StringBuilder` in the Overt source lowers to the host type
        // `System.Text.StringBuilder` without any rename pass. The Overt
        // compiler treats the opaque type as a nominal type; the C# side
        // sees only the aliased spelling.
        foreach (var xt in module.Declarations.OfType<ExternTypeDecl>())
        {
            if (xt.Platform == "csharp")
            {
                _w.WriteLine($"using {EscapeId(xt.Name)} = global::{xt.BindsTarget};");
            }
        }

        _w.WriteLine();
        _w.WriteLine($"namespace {ToEmittedNamespace(module.Name)};");
        _w.WriteLine();

        // Type aliases emit as file-scoped using-directives at the top. C# requires them
        // before any type declarations.
        foreach (var decl in module.Declarations.OfType<TypeAliasDecl>())
        {
            EmitTypeAlias(decl);
        }
        if (module.Declarations.OfType<TypeAliasDecl>().Any())
        {
            _w.WriteLine();
        }

        // Non-generic refinement aliases still lower to C# using-aliases (no
        // coercion point), so their runtime checks live in a sibling helper
        // class. The emitter wraps boundary expressions in
        // `__Refinements.{Alias}__Check(...)`; the bodies evaluate the
        // predicate and throw RefinementViolation on failure.
        EmitRefinementChecks(module);

        foreach (var decl in module.Declarations
            .Where(d => d is not TypeAliasDecl and not UseDecl))
        {
            EmitDeclaration(decl);
            _w.WriteLine();
        }

        // Functions and externs go on a single static `Module` class. For clarity we
        // emit that class once, wrapping all functions and externs collected.
        var fnLike = module.Declarations
            .Where(d => d is FunctionDecl or ExternDecl)
            .ToImmutableArray();
        if (fnLike.Length > 0)
        {
            _w.WriteLine("public static class Module");
            _w.WriteLine("{");
            using (_w.Indent())
            {
                foreach (var decl in fnLike)
                {
                    switch (decl)
                    {
                        case FunctionDecl f: EmitFunction(f); break;
                        case ExternDecl x: EmitExtern(x); break;
                    }
                    _w.WriteLine();
                }
            }
            _w.WriteLine("}");
        }
    }

    // ------------------------------------------------------ declarations

    private void EmitDeclaration(Declaration decl)
    {
        switch (decl)
        {
            case RecordDecl r: EmitRecord(r); break;
            case EnumDecl e: EmitEnum(e); break;
            case FunctionDecl:
            case ExternDecl:
                // Emitted inside the Module class by EmitModule.
                break;
        }
    }

    private void EmitRecord(RecordDecl rec)
    {
        EmitAnnotationComments(rec.Annotations);
        EmitDocComment(rec.Annotations);
        EmitCSharpAttributes(rec.Annotations);
        RejectDocOnRecordFields(rec.Fields);
        _w.Write("public sealed record ");
        _w.Write(rec.Name);
        _w.Write("(");
        for (var i = 0; i < rec.Fields.Length; i++)
        {
            if (i > 0) _w.Write(", ");
            EmitInlineCSharpAttributesForRecordField(rec.Fields[i].Annotations);
            EmitType(rec.Fields[i].Type);
            _w.Write(" ");
            _w.Write(EscapeId(rec.Fields[i].Name));
        }
        _w.WriteLine(");");
    }

    private void EmitEnum(EnumDecl e)
    {
        EmitAnnotationComments(e.Annotations);
        EmitDocComment(e.Annotations);
        EmitCSharpAttributes(e.Annotations);
        _w.Write($"public abstract record {e.Name}");
        _w.WriteLine(";");
        foreach (var variant in e.Variants)
        {
            EmitDocComment(variant.Annotations);
            EmitCSharpAttributes(variant.Annotations);
            RejectDocOnRecordFields(variant.Fields);
            _w.Write("public sealed record ");
            _w.Write($"{e.Name}_{variant.Name}");
            if (variant.Fields.Length > 0)
            {
                _w.Write("(");
                for (var i = 0; i < variant.Fields.Length; i++)
                {
                    if (i > 0) _w.Write(", ");
                    EmitInlineCSharpAttributesForRecordField(variant.Fields[i].Annotations);
                    EmitType(variant.Fields[i].Type);
                    _w.Write(" ");
                    _w.Write(EscapeId(variant.Fields[i].Name));
                }
                _w.Write(")");
            }
            _w.Write($" : {e.Name}");
            _w.WriteLine(";");
        }
    }

    /// <summary>
    /// Field-level <c>@doc("...")</c> isn't supported in v1 because the
    /// positional-record parameter list is single-line and an inline XML
    /// doc comment has no clean spot to land. Field-level <c>@csharp</c>
    /// works inline. Flag a clear emitter-time error if anyone tries
    /// <c>@doc</c> on a field; future versions can switch to multi-line
    /// emission when @doc is present and remove this guard.
    /// </summary>
    private static void RejectDocOnRecordFields(ImmutableArray<RecordField> fields)
    {
        foreach (var field in fields)
        {
            foreach (var ann in field.Annotations)
            {
                if (ann.Name == "doc")
                {
                    throw new InvalidOperationException(
                        $"`@doc(\"...\")` is not supported on record fields in v1; saw it on field `{field.Name}`. Move the documentation to the enclosing record or use `@csharp(\"...\")` for a passthrough form.");
                }
            }
        }
    }

    private void EmitTypeAlias(TypeAliasDecl t)
    {
        // C# using-aliases can't be generic and can't carry predicates. Generic or
        // refinement aliases lower to wrapper records instead; non-generic plain aliases
        // stay as using-directives so primitive aliases don't pay for a wrapping type.
        //
        // Non-generic refinements lower to a using-alias today and so have no coercion
        // boundary to check at; literal violations are caught by OV0311 at compile
        // time, and non-literal violations aren't enforced at runtime yet.
        if (t.Predicate is not null && t.TypeParameters.Length == 0)
        {
            _w.WriteLine(
                $"// TODO: non-generic refinement `{t.Name}`: runtime check for "
                + "non-literal values not yet wired (OV0311 covers literal boundaries)");
        }

        if (t.TypeParameters.Length > 0)
        {
            var typeParams = string.Join(", ", t.TypeParameters);
            var innerType = CSharpTypeDisplay(LowerType(t.Target));

            _w.WriteLine($"public sealed record {t.Name}<{typeParams}>({innerType} Inner)");
            _w.WriteLine("{");
            using (_w.Indent())
            {
                if (t.Predicate is { } predicate)
                {
                    // Predicate references `self`; the parameter is named `self` so
                    // the inline emission of the predicate expression substitutes
                    // correctly. Throws RefinementViolation on failure; callers see
                    // the aliased name, the predicate source, and the offending value.
                    var predText = FormatPredicateText(predicate);
                    _w.WriteLine(
                        $"public static implicit operator {t.Name}<{typeParams}>({innerType} self)");
                    _w.WriteLine("{");
                    using (_w.Indent())
                    {
                        _w.Write("if (!(");
                        EmitExpression(predicate);
                        _w.WriteLine("))");
                        _w.WriteLine("{");
                        using (_w.Indent())
                        {
                            _w.WriteLine(
                                $"throw new global::Overt.Runtime.RefinementViolation("
                                + $"\"{t.Name}\", \"{EscapeCsharpString(predText)}\", self);");
                        }
                        _w.WriteLine("}");
                        _w.WriteLine("return new(self);");
                    }
                    _w.WriteLine("}");
                }
                else
                {
                    // Implicit conversion from the inner type into the wrapper so plain
                    // generic aliases accept a bare value wherever they're required.
                    _w.WriteLine(
                        $"public static implicit operator {t.Name}<{typeParams}>({innerType} inner) => new(inner);");
                }
            }
            _w.WriteLine("}");
            return;
        }

        _w.Write($"using {t.Name} = ");
        EmitType(t.Target);
        _w.WriteLine(";");
    }

    /// <summary>Emit a <c>__Refinements</c> static class holding a <c>{Alias}__Check</c>
    /// method for each non-generic refinement alias that carries a predicate.
    /// The method evaluates the predicate against its argument, throws
    /// <see cref="Overt.Runtime.RefinementViolation"/> on failure, otherwise
    /// returns the value untouched. Generic refinements don't participate —
    /// they check inside their wrapper's implicit operator.</summary>
    private void EmitRefinementChecks(ModuleDecl module)
    {
        var refinedAliases = module.Declarations
            .OfType<TypeAliasDecl>()
            .Where(t => t.Predicate is not null && t.TypeParameters.Length == 0)
            .ToArray();
        if (refinedAliases.Length == 0) return;

        _w.WriteLine("internal static class __Refinements");
        _w.WriteLine("{");
        using (_w.Indent())
        {
            foreach (var t in refinedAliases)
            {
                var innerType = CSharpTypeDisplay(LowerType(t.Target));
                var predText = FormatPredicateText(t.Predicate!);
                _w.WriteLine($"public static {innerType} {t.Name}__Check({innerType} self)");
                _w.WriteLine("{");
                using (_w.Indent())
                {
                    _w.Write("if (!(");
                    EmitExpressionBody(t.Predicate!);
                    _w.WriteLine("))");
                    _w.WriteLine("{");
                    using (_w.Indent())
                    {
                        _w.WriteLine(
                            $"throw new global::Overt.Runtime.RefinementViolation("
                            + $"\"{t.Name}\", \"{EscapeCsharpString(predText)}\", self);");
                    }
                    _w.WriteLine("}");
                    _w.WriteLine("return self;");
                }
                _w.WriteLine("}");
            }
        }
        _w.WriteLine("}");
        _w.WriteLine();
    }

    /// <summary>Render a refinement predicate as its Overt source spelling for
    /// inclusion in RefinementViolation messages. Mirrors TypeChecker.FormatPredicate
    /// but kept local so the emitter doesn't reach into TypeChecker internals.</summary>
    private static string FormatPredicateText(Expression e) => e switch
    {
        BinaryExpr be =>
            $"{FormatPredicateText(be.Left)} {FormatPredicateBinOp(be.Op)} {FormatPredicateText(be.Right)}",
        UnaryExpr { Op: UnaryOp.Negate } ue => $"-{FormatPredicateText(ue.Operand)}",
        UnaryExpr { Op: UnaryOp.LogicalNot } ue => $"!{FormatPredicateText(ue.Operand)}",
        IdentifierExpr id => id.Name,
        IntegerLiteralExpr i => i.Lexeme,
        FloatLiteralExpr f => f.Lexeme,
        BooleanLiteralExpr b => b.Value ? "true" : "false",
        CallExpr c => FormatPredicateText(c.Callee) + "("
            + string.Join(", ", c.Arguments.Select(a => FormatPredicateText(a.Value))) + ")",
        _ => "...",
    };

    private static string FormatPredicateBinOp(BinaryOp op) => op switch
    {
        BinaryOp.LogicalAnd => "&&",
        BinaryOp.LogicalOr => "||",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.Less => "<",
        BinaryOp.LessEqual => "<=",
        BinaryOp.Greater => ">",
        BinaryOp.GreaterEqual => ">=",
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Modulo => "%",
        _ => "?",
    };

    private static string EscapeCsharpString(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private void EmitFunction(FunctionDecl fn)
    {
        EmitEffectRowComment(fn.Effects);
        EmitDocComment(fn.Annotations);
        EmitCSharpAttributes(fn.Annotations);
        EmitLineDirective(fn.Span);

        // Functions whose body uses `.await` emit as C# `async Task<ReturnType>`.
        // `.await` lowers to C# `await`; call sites see `Task<T>` and use
        // `.await` to unwrap. Fns that carry `async` in their effect row for
        // other reasons (par_map, etc.) stay sync.
        var isAsync = BodyContainsAwaitExpr(fn.Body);
        _w.Write(isAsync ? "public static async " : "public static ");
        if (isAsync)
        {
            _w.Write("global::System.Threading.Tasks.Task<");
        }
        if (fn.ReturnType is { } rt)
        {
            EmitType(rt);
        }
        else
        {
            _w.Write("Unit");
        }
        if (isAsync)
        {
            _w.Write(">");
        }
        _w.Write($" {EscapeId(fn.Name)}");

        // Drop type parameters that appear only in effect rows — C# has no analog for
        // effect-row phantoms, and their presence as unconstrained type parameters
        // breaks generic inference on callers. `fn apply<T, E>(f: fn(T) !{E} -> T, x: T)`
        // emits as `apply<T>(...)`; E is inferred-out because it never reaches a value
        // position.
        var usedTypeParams = CollectUsedTypeParamNames(fn);
        var emittedTypeParams = fn.TypeParameters
            .Where(p => usedTypeParams.Contains(p))
            .ToArray();
        if (emittedTypeParams.Length > 0)
        {
            _w.Write("<");
            _w.Write(string.Join(", ", emittedTypeParams));
            _w.Write(">");
        }
        _w.Write("(");
        for (var i = 0; i < fn.Parameters.Length; i++)
        {
            if (i > 0) _w.Write(", ");
            EmitType(fn.Parameters[i].Type);
            _w.Write(" ");
            _w.Write(EscapeId(fn.Parameters[i].Name));
        }
        _w.WriteLine(")");
        var savedReturn = _currentFnReturn;
        _currentFnReturn = fn.ReturnType is null ? null : LowerType(fn.ReturnType);
        try
        {
            EmitBlockAsMethodBody(fn.Body, fn.ReturnType);
        }
        finally
        {
            _currentFnReturn = savedReturn;
        }
    }

    /// <summary>Type-parameter names that appear in the function's value-positions
    /// (parameter types, return type) rather than only in effect rows.</summary>
    private static HashSet<string> CollectUsedTypeParamNames(FunctionDecl fn)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in fn.Parameters)
        {
            CollectNamedTypes(p.Type, used);
        }
        if (fn.ReturnType is { } rt)
        {
            CollectNamedTypes(rt, used);
        }
        return used;
    }

    private static void CollectNamedTypes(TypeExpr type, HashSet<string> names)
    {
        switch (type)
        {
            case NamedType nt:
                names.Add(nt.Name);
                foreach (var arg in nt.TypeArguments) CollectNamedTypes(arg, names);
                break;
            case FunctionType ft:
                foreach (var p in ft.Parameters) CollectNamedTypes(p, names);
                CollectNamedTypes(ft.ReturnType, names);
                // Deliberately does not descend into ft.Effects — effect names are
                // tracked separately and don't count as value-position type uses.
                break;
        }
    }

    private void EmitExtern(ExternDecl x)
    {
        var unsafePrefix = x.IsUnsafe ? "// unsafe " : "// ";
        var kindKw = x.Kind switch
        {
            ExternKind.Instance => " instance",
            ExternKind.Constructor => " ctor",
            _ => "",
        };
        _w.WriteLine($"{unsafePrefix}extern \"{x.Platform}\"{kindKw} binds \"{x.BindsTarget}\""
            + (x.FromLibrary is { } lib ? $" from \"{lib}\"" : ""));
        EmitEffectRowComment(x.Effects);
        _w.Write("public static ");
        if (x.ReturnType is { } rt)
        {
            EmitType(rt);
        }
        else
        {
            _w.Write("Unit");
        }
        _w.Write($" {EscapeId(x.Name)}(");
        for (var i = 0; i < x.Parameters.Length; i++)
        {
            if (i > 0) _w.Write(", ");
            EmitType(x.Parameters[i].Type);
            _w.Write(" ");
            _w.Write(EscapeId(x.Parameters[i].Name));
        }
        _w.WriteLine(")");
        _w.WriteLine("{");
        using (_w.Indent())
        {
            EmitExternBody(x);
        }
        _w.WriteLine("}");
    }

    /// <summary>
    /// Lower an <c>extern</c> body to a real invocation of the binds target.
    ///
    /// Three shapes, selected by <see cref="ExternDecl.Kind"/>:
    /// <list type="bullet">
    ///   <item><c>Static</c> — binds target is a dotted path
    ///     <c>Ns.Type.Method</c>. Emits <c>global::Ns.Type.Method(args)</c>,
    ///     or bare member access for properties/fields.</item>
    ///   <item><c>Instance</c> — binds target is <c>Ns.Type.Method</c>; the
    ///     first Overt parameter is the receiver. Emits <c>self.Method(rest)</c>,
    ///     or <c>self.Prop</c> for instance properties/fields.</item>
    ///   <item><c>Constructor</c> — binds target is a type path
    ///     <c>Ns.Type</c>. Emits <c>new global::Ns.Type(args)</c>.</item>
    /// </list>
    /// Return type <c>Result&lt;T, E&gt;</c> wraps the call in try/catch and
    /// converts thrown exceptions into an <c>Err</c>; known narrative-shaped
    /// error types get auto-constructed, otherwise the catch rethrows.
    /// </summary>
    private void EmitExternBody(ExternDecl x)
    {
        if (x.Platform != "csharp")
        {
            // Use the dedicated marker type so `overt run` can recognize the
            // case and render it as a toolchain limitation rather than as an
            // unhandled exception (Overt readers don't have that vocabulary).
            _w.WriteLine(
                $"throw new global::Overt.Runtime.ExternPlatformNotImplemented(\"{x.Platform}\", \"{x.Name}\");");
            return;
        }

        var returnsResult = x.ReturnType is NamedType
        { Name: "Result", TypeArguments.Length: 2 };

        // Try-pattern body is multi-statement (declare an out temp, call
        // the underlying method, branch on the bool, return Some/None);
        // it doesn't fit the single-callExpr machinery used by the other
        // kinds, so handle it here and return early.
        if (x.Kind == ExternKind.Try)
        {
            EmitTryPatternBody(x);
            return;
        }

        string callExpr;
        switch (x.Kind)
        {
            case ExternKind.Constructor:
                {
                    var ctorArgs = string.Join(", ",
                        x.Parameters.Select(p => EscapeId(p.Name)));
                    callExpr = $"new global::{x.BindsTarget}({ctorArgs})";
                    break;
                }
            case ExternKind.Instance:
                {
                    if (x.Parameters.Length == 0)
                    {
                        _w.WriteLine(
                            "throw new InvalidOperationException("
                            + "\"extern instance requires a `self` parameter\");");
                        return;
                    }
                    var lastDot = x.BindsTarget.LastIndexOf('.');
                    if (lastDot < 1)
                    {
                        _w.WriteLine(
                            "throw new InvalidOperationException("
                            + $"\"extern instance binds target '{x.BindsTarget}' must be Type.Member\");");
                        return;
                    }
                    var typeName = x.BindsTarget[..lastDot];
                    var memberName = x.BindsTarget[(lastDot + 1)..];
                    var receiver = EscapeId(x.Parameters[0].Name);

                    if (x.Parameters.Length == 1
                        && IsInstancePropertyOrField(typeName, memberName))
                    {
                        callExpr = $"{receiver}.{memberName}";
                    }
                    else
                    {
                        var rest = string.Join(", ",
                            x.Parameters.Skip(1).Select(p => EscapeId(p.Name)));
                        callExpr = $"{receiver}.{memberName}({rest})";
                    }
                    break;
                }
            default: // ExternKind.Static
                {
                    var args = string.Join(", ",
                        x.Parameters.Select(p => EscapeId(p.Name)));
                    var prefixedTarget = "global::" + x.BindsTarget;
                    callExpr = x.Parameters.Length == 0 && BindsLooksLikeProperty(x.BindsTarget)
                        ? prefixedTarget
                        : $"{prefixedTarget}({args})";
                    break;
                }
        }

        // Hand-bound externs that target another Overt module's emitted
        // surface (the `Module` static class on the consuming side) are a
        // passthrough — the bound method already returns the same
        // `Result<T, E>` we declare on the Overt side, no exception lift.
        // Without this branch, the wrap-and-catch below would produce
        // `Ok(call())` for a call that already returns Result, double-
        // wrapping into `Result<Result<T, E>, E>` and tripping CS0029.
        // The narrow heuristic — bind target is `*.Module.member` — is
        // safe because Overt always emits user fns into a `Module`
        // static class; a hand-bound BCL call to a class genuinely named
        // `Module` would be exotic enough to warrant an explicit opt-out
        // path if it ever materializes.
        var bindsToOvertModule = returnsResult
            && IsBindsToOvertModule(x.BindsTarget);
        if (bindsToOvertModule)
        {
            _w.WriteLine($"return {callExpr};");
            return;
        }

        if (!returnsResult)
        {
            // Void or plain-typed return: pass through. Exceptions fly.
            if (x.ReturnType is UnitType || x.ReturnType is null)
            {
                _w.WriteLine($"{callExpr};");
                _w.WriteLine("return Unit.Value;");
            }
            else if (x.ReturnType is NamedType { Name: "Option", TypeArguments.Length: 1 } optType)
            {
                // The convention layer maps a nullable BCL return (`T?`)
                // onto Overt `Option<T>`. The underlying C# call expression
                // still produces a possibly-null reference, so lift it into
                // `Option<T>` here — emitting a bare `return callExpr` would
                // hit CS0029 (string -> Option<string>).
                var inner = LowerTypeToCSharpString(optType.TypeArguments[0]);
                _w.WriteLine($"var __overt_extern_result = {callExpr};");
                _w.WriteLine(
                    $"return __overt_extern_result is null"
                    + $" ? (Option<{inner}>)new OptionNone<{inner}>()"
                    + $" : (Option<{inner}>)new OptionSome<{inner}>(__overt_extern_result);");
            }
            else
            {
                _w.WriteLine($"return {callExpr};");
            }
            return;
        }

        // Result<T, E> return: exception → Err(error). We construct the Err
        // from the thrown exception's message when the error type has a
        // single-string constructor (IoError, HttpError, LlmError, etc.);
        // other cases would need a custom mapping table. For now this covers
        // the common "IoError { narrative = ... }" shape.
        var errorTypeRef = ((NamedType)x.ReturnType!).TypeArguments[1];
        _w.WriteLine("try");
        _w.WriteLine("{");
        using (_w.Indent())
        {
            _w.WriteLine($"return Ok({callExpr});");
        }
        _w.WriteLine("}");
        _w.WriteLine("catch (Exception __ex)");
        _w.WriteLine("{");
        using (_w.Indent())
        {
            // Construct the error using the known "single-string narrative"
            // shape. Works for IoError, HttpError, LlmError, and any
            // user-declared @derive(Debug) enum variant with a single narrative
            // field — but falls back to rethrow for mismatches.
            EmitExternErrorConversion(errorTypeRef);
        }
        _w.WriteLine("}");
    }

    /// <summary>
    /// Emit the body of a Try-pattern extern (<see cref="ExternKind.Try"/>):
    /// declare an out temp typed for the underlying C# out parameter, call
    /// the bind target, and branch on its bool return into <c>Some</c> or
    /// <c>None</c>.
    ///
    /// Required shape from the AST:
    /// <list type="bullet">
    ///   <item>Return type is <c>Option&lt;T&gt;</c> for some Overt T.</item>
    ///   <item>Bind target is a static method <c>Ns.Type.TryX</c>.</item>
    ///   <item>Overt parameters match the underlying method's input parameters
    ///     (the trailing out parameter is dropped at the Overt level).</item>
    /// </list>
    /// Anything else is a generator bug — emit a runtime exception so the
    /// failure surfaces clearly during testing rather than producing
    /// silently-wrong code.
    /// </summary>
    private void EmitTryPatternBody(ExternDecl x)
    {
        if (x.ReturnType is not NamedType { Name: "Option", TypeArguments.Length: 1 } optType)
        {
            _w.WriteLine(
                "throw new global::System.InvalidOperationException("
                + $"\"extern try '{x.Name}' must return Option<T>; got something else (generator bug)\");");
            return;
        }

        var innerOvert = optType.TypeArguments[0];
        var innerCs = LowerTypeToCSharpString(innerOvert);
        if (innerCs is null)
        {
            _w.WriteLine(
                "throw new global::System.InvalidOperationException("
                + $"\"extern try '{x.Name}' has unsupported Option inner type (generator bug)\");");
            return;
        }

        var args = string.Join(", ", x.Parameters.Select(p => EscapeId(p.Name)));
        var argSep = x.Parameters.Length > 0 ? ", " : "";
        var prefixedTarget = "global::" + x.BindsTarget;

        _w.WriteLine($"{innerCs} __overt_tryout = default!;");
        _w.WriteLine($"return {prefixedTarget}({args}{argSep}out __overt_tryout)");
        using (_w.Indent())
        {
            _w.WriteLine($"? (global::Overt.Runtime.Option<{innerCs}>)new global::Overt.Runtime.OptionSome<{innerCs}>(__overt_tryout)");
            _w.WriteLine($": (global::Overt.Runtime.Option<{innerCs}>)new global::Overt.Runtime.OptionNone<{innerCs}>();");
        }
    }

    /// <summary>
    /// Render an Overt <see cref="TypeExpr"/> as the equivalent C# type
    /// spelling, used by helpers that need to emit raw C# fragments
    /// (e.g. the Try-pattern body declaring its out temp). Reuses the
    /// emitter's existing type-mapping rules but produces a string instead
    /// of writing to <see cref="_w"/>. Returns null when the input is
    /// outside the supported shape (caller surfaces a clear runtime error).
    /// </summary>
    private static string? LowerTypeToCSharpString(TypeExpr type) => type switch
    {
        NamedType { Name: "Int" } => "int",
        NamedType { Name: "Int64" } => "long",
        NamedType { Name: "Float" } => "double",
        NamedType { Name: "Bool" } => "bool",
        NamedType { Name: "String" } => "string",
        _ => null,
    };

    /// <summary>
    /// Emit the Err-side of an extern's exception-to-Result conversion. We
    /// only auto-construct errors for runtime error types with a known
    /// single-<c>narrative</c>-string constructor (IoError, HttpError,
    /// LlmError — defined in <c>Overt.Runtime</c>). Anything else — notably
    /// user-defined enum error types with multiple variants — falls through
    /// to rethrow; mapping arbitrary exceptions to arbitrary user variants
    /// needs a design pass and likely per-binding configuration.
    /// </summary>
    private void EmitExternErrorConversion(TypeExpr errorType)
    {
        if (errorType is NamedType nt && IsKnownNarrativeError(nt.Name))
        {
            _w.WriteLine($"return Err(new {MapTypeName(nt.Name)}(narrative: __ex.Message));");
            return;
        }
        _w.WriteLine(
            "throw; // extern: auto-conversion only supported for known "
            + "narrative-shaped errors; rethrowing");
    }

    /// <summary>Error types in <c>Overt.Runtime</c> that have a single-string
    /// <c>narrative</c> constructor. Keeping this list narrow and explicit is
    /// safer than guessing; adding more is a deliberate act.</summary>
    private static bool IsKnownNarrativeError(string name)
        => name is "IoError" or "HttpError" or "LlmError";

    /// <summary>
    /// True when a binds target points at the static <c>Module</c> class
    /// emitted by another Overt project — i.e. a target of the shape
    /// <c>...Module.&lt;member&gt;</c> or bare <c>Module.&lt;member&gt;</c>.
    /// These bind into Overt-emitted code that already returns Result,
    /// so the extern body should pass through rather than re-wrap.
    /// </summary>
    private static bool IsBindsToOvertModule(string bindsTarget)
    {
        var lastDot = bindsTarget.LastIndexOf('.');
        if (lastDot < 1)
        {
            return false;
        }
        var typePath = bindsTarget[..lastDot];
        if (typePath == "Module")
        {
            return true;
        }
        return typePath.EndsWith(".Module", StringComparison.Ordinal);
    }

    /// <summary>Reflect on the binds target and return true if it resolves to
    /// a public static property or field (accessed bare, without parentheses)
    /// rather than a method (accessed with parentheses). We consult every
    /// loaded assembly in the AppDomain — the BCL plus anything the compiler
    /// has pulled in — and pick the first match. If the type can't be
    /// resolved (e.g. a reference to user code that hasn't been loaded),
    /// we fall back to the method-call shape.
    ///
    /// Cached so repeated facades don't re-reflect on the same types.</summary>
    private static readonly Dictionary<string, bool> _bindsIsProperty = new(StringComparer.Ordinal);

    private static bool BindsLooksLikeProperty(string bindsTarget)
    {
        if (_bindsIsProperty.TryGetValue(bindsTarget, out var cached)) return cached;

        var lastDot = bindsTarget.LastIndexOf('.');
        if (lastDot < 1)
        {
            _bindsIsProperty[bindsTarget] = false;
            return false;
        }
        var typeName = bindsTarget[..lastDot];
        var memberName = bindsTarget[(lastDot + 1)..];

        var result = IsStaticPropertyOrField(typeName, memberName);
        _bindsIsProperty[bindsTarget] = result;
        return result;
    }

    private static bool IsStaticPropertyOrField(string typeName, string memberName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try { type = asm.GetType(typeName, throwOnError: false); }
            catch { continue; }
            if (type is null) continue;
            var members = type.GetMember(
                memberName,
                System.Reflection.MemberTypes.Property | System.Reflection.MemberTypes.Field,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (members.Length > 0) return true;
        }
        return false;
    }

    /// <summary>Reflection check for an instance-side <c>::</c> binds target:
    /// returns true if the member is a public instance property or field
    /// (accessed bare) rather than a method (accessed with parentheses).
    /// Cached; mirrors <see cref="IsStaticPropertyOrField"/> but with
    /// <c>BindingFlags.Instance</c>.</summary>
    private static readonly Dictionary<string, bool> _instanceBindsIsProperty = new(StringComparer.Ordinal);

    private static bool IsInstancePropertyOrField(string typeName, string memberName)
    {
        var key = typeName + "::" + memberName;
        if (_instanceBindsIsProperty.TryGetValue(key, out var cached)) return cached;

        var result = false;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try { type = asm.GetType(typeName, throwOnError: false); }
            catch { continue; }
            if (type is null) continue;
            var members = type.GetMember(
                memberName,
                System.Reflection.MemberTypes.Property | System.Reflection.MemberTypes.Field,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (members.Length > 0) { result = true; break; }
        }
        _instanceBindsIsProperty[key] = result;
        return result;
    }

    // ----------------------------------------------------- type expressions

    private void EmitType(TypeExpr type)
    {
        switch (type)
        {
            case NamedType nt:
                _w.Write(MapTypeName(nt.Name));
                if (nt.TypeArguments.Length > 0)
                {
                    _w.Write("<");
                    for (var i = 0; i < nt.TypeArguments.Length; i++)
                    {
                        if (i > 0) _w.Write(", ");
                        EmitType(nt.TypeArguments[i]);
                    }
                    _w.Write(">");
                }
                break;

            case UnitType:
                _w.Write("Unit");
                break;

            case FunctionType ft:
                // Map to Func<,> (or Action<> for Unit return). Effect row is discarded.
                if (IsUnit(ft.ReturnType))
                {
                    if (ft.Parameters.Length == 0) _w.Write("Action");
                    else
                    {
                        _w.Write("Action<");
                        for (var i = 0; i < ft.Parameters.Length; i++)
                        {
                            if (i > 0) _w.Write(", ");
                            EmitType(ft.Parameters[i]);
                        }
                        _w.Write(">");
                    }
                }
                else
                {
                    _w.Write("Func<");
                    foreach (var p in ft.Parameters)
                    {
                        EmitType(p);
                        _w.Write(", ");
                    }
                    EmitType(ft.ReturnType);
                    _w.Write(">");
                }
                break;
        }
    }

    private static bool IsUnit(TypeExpr t) => t is UnitType;

    private static string MapTypeName(string name) => name switch
    {
        "Int" => "int",
        "Int64" => "long",
        "Float" => "double",
        "Bool" => "bool",
        "String" => "string",
        "Task" => "global::System.Threading.Tasks.Task",
        // `List` collides with `System.Collections.Generic.List<T>` whenever
        // the consuming project has ImplicitUsings enabled (the .NET SDK
        // default). Fully qualify so the generated code stays unambiguous
        // regardless of the consumer's using context. Other Overt.Runtime
        // types (Result, Option, Unit, Tuple) have no System counterpart
        // brought in by default, so they don't need the same treatment.
        "List" => "global::Overt.Runtime.List",
        _ => name,
    };

    // ---------------------------------------------------- blocks + stmts

    private void EmitBlockAsMethodBody(BlockExpr block, TypeExpr? returnType)
    {
        var declaredReturn = returnType is null ? null : LowerType(returnType);
        _w.WriteLine("{");
        using (_w.Indent())
        {
            foreach (var stmt in block.Statements)
            {
                EmitStatement(stmt);
            }
            if (block.TrailingExpression is { } tail)
            {
                EmitLineDirective(tail.Span);
                if (tail is ReturnExpr rx)
                {
                    // The trailing expression is itself a `return X` —
                    // unwrap it so we emit a single `return X;` rather
                    // than the nonsensical `return return X;`.
                    EmitHoistsForExpression(rx.Value);
                    _w.Write("return ");
                    WithExpected(declaredReturn, () => EmitExpression(rx.Value));
                    _w.WriteLine(";");
                }
                else
                {
                    EmitHoistsForExpression(tail);
                    _w.Write("return ");
                    WithExpected(declaredReturn, () => EmitExpression(tail));
                    _w.WriteLine(";");
                }
            }
            else if (returnType is UnitType)
            {
                _w.WriteLine("return Unit.Value;");
            }
        }
        _w.WriteLine("}");
        EmitLineDefault();
    }

    /// <summary>
    /// Best-effort syntactic type lowering for use in expected-type propagation. Full
    /// lowering with generic-param scoping lives in <see cref="TypeChecker"/>; here we
    /// only need to produce a <see cref="TypeRef"/> so calls can pick up the context's
    /// concrete type. Treats every <c>NamedType</c> as a named type ref unless it's a
    /// known primitive; doesn't distinguish type-variables from nominal types.
    /// </summary>
    private static TypeRef LowerType(TypeExpr type) => type switch
    {
        NamedType nt => nt.Name switch
        {
            "Int" => PrimitiveType.Int,
            "Float" => PrimitiveType.Float,
            "Bool" => PrimitiveType.Bool,
            "String" => PrimitiveType.String,
            _ => new NamedTypeRef(nt.Name, nt.TypeArguments.Select(LowerType).ToImmutableArray()),
        },
        UnitType => PrimitiveType.Unit,
        FunctionType ft => new FunctionTypeRef(
            ft.Parameters.Select(LowerType).ToImmutableArray(),
            LowerType(ft.ReturnType),
            ft.Effects is null ? ImmutableArray<string>.Empty : ft.Effects.Effects),
        _ => UnknownType.Instance,
    };

    private void EmitStatement(Statement stmt)
    {
        // Per-statement `#line` directives are the granularity at which PDBs map
        // breakpoints and exception lines. Finer (per-expression) would be noisier
        // without helping; coarser (per-function) loses the information C# debuggers
        // use to step through `.ov` source.
        EmitLineDirective(stmt.Span);
        switch (stmt)
        {
            case LetStmt ls:
                // `let _ = return X` (or any let whose initializer IS a return)
                // is dead-code after the return — emit `return X;` and skip
                // the binding. Without this, EmitExpression would hit a
                // ReturnExpr in an expression slot and have nowhere to lower it.
                if (ls.Initializer is ReturnExpr lsReturn)
                {
                    EmitHoistsForExpression(lsReturn.Value);
                    _w.Write("return ");
                    EmitExpression(lsReturn.Value);
                    _w.WriteLine(";");
                    break;
                }

                // When the initializer is directly an if / match containing `?`,
                // lower to statement-level C# if/else or switch with assignments
                // into a pre-declared temp. This lets the `?`-hoist's early-return
                // reach the enclosing function, not get trapped in an IIFE. See
                // TryEmitStmtLoweredLet for the eligible shapes.
                if (TryEmitStmtLoweredLet(ls)) break;

                // `?` buried inside an if/match that's nested inside a call
                // arg / record field / etc. can't hoist in place (branches
                // shouldn't force). Pre-emit each such conditional as a local,
                // then emit the let normally with the original sites substituted.
                LiftNestedConditionals(CollectLiftableNestedConditionals(ls.Initializer));

                EmitHoistsForExpression(ls.Initializer);
                if (ls.Target is WildcardPattern)
                {
                    // `let _ = expr` — emit the C# discard form so multiple
                    // discards in the same scope don't redeclare `_` (CS0128).
                    // The type annotation, if any, isn't needed for a discard.
                    _w.Write("_ = ");
                    EmitExpression(ls.Initializer);
                    _w.WriteLine(";");
                }
                else
                {
                    _w.Write("var ");
                    EmitPatternForBinding(ls.Target);
                    _w.Write(" = ");
                    EmitExpression(ls.Initializer);
                    _w.WriteLine(";");
                }
                break;

            case AssignmentStmt asn:
                if (TryEmitStmtLoweredAssign(asn)) break;

                LiftNestedConditionals(CollectLiftableNestedConditionals(asn.Value));

                EmitHoistsForExpression(asn.Value);
                _w.Write(EscapeId(asn.Name));
                _w.Write(" = ");
                EmitExpression(asn.Value);
                _w.WriteLine(";");
                break;

            case ExpressionStmt es:
                EmitExpressionAsStatement(es.Expression);
                break;

            case DiscardStmt ds:
                // `_ = expr;` — C# discard form. Repeats freely in scope
                // (no fresh binding) and also accepts non-Result types
                // gracefully. Hoist any `?` inside the expression first
                // (same pre-pass as let/assignment).
                LiftNestedConditionals(CollectLiftableNestedConditionals(ds.Value));
                EmitHoistsForExpression(ds.Value);
                _w.Write("_ = ");
                EmitExpression(ds.Value);
                _w.WriteLine(";");
                break;

            case BreakStmt:
                _w.WriteLine("break;");
                break;

            case ContinueStmt:
                _w.WriteLine("continue;");
                break;
        }
    }

    // ------------------------------------- statement-level if/match lowering
    //
    // When a `let x: T = if cond { foo()? } else { bar() }` (or match) would
    // otherwise trap a `?`-hoist's `return Err<E>(...)` inside an IIFE that
    // returns T (not Result<_, E>), we lower the whole initializer to a C#
    // if-statement (or switch-statement) that assigns into a pre-declared
    // temp. The `?`-hoist then lives inside the branch body and can return
    // from the enclosing function directly.
    //
    // Eligibility (narrow by design — other shapes keep their existing
    // emission):
    //   - Initializer is directly IfExpr or MatchExpr
    //   - At least one arm body contains `?` or `|>?` somewhere
    //   - Target is a single IdentifierPattern (no tuple destructuring)
    //   - Let has an explicit type annotation, OR the context makes it
    //     declarable — in practice we just require an annotation today, to
    //     avoid inferring a type here

    private bool TryEmitStmtLoweredLet(LetStmt ls)
    {
        if (ls.Target is not IdentifierPattern ip) return false;
        if (ls.Type is null) return false;
        if (!NeedsStmtLowering(ls.Initializer)) return false;

        var targetType = LowerType(ls.Type);

        // Shadow guard: if any pattern in the lowered if/match shares
        // its binding name with the let target, the natural C# emit
        // (`Type name; switch { case Pat(var name): ... }`) hits CS0136
        // — C# treats sibling-scope same-name declarations as
        // shadowing, even if textually they don't overlap. Skip the
        // user's name entirely: emit the let target as a synthesized
        // C# variable, and route subsequent IdentifierExpr references
        // (the ones that resolve to *this* let binding) to the same
        // synthesized name. Other bindings sharing the textual name —
        // pattern bindings inside a later match arm, say — keep their
        // own emit because resolution distinguishes them by symbol.
        if (_resolution is not null
            && InitializerBindingsCollideWith(ls.Initializer, ip.Name))
        {
            var temp = $"__let_{ip.Name}_{_letCounter++}";
            _renamedBindings[ip.Span] = temp;
            _w.Write(CSharpTypeDisplay(targetType));
            _w.Write(" ");
            _w.Write(temp);
            _w.WriteLine(";");
            AssignInto(temp, ls.Initializer, targetType);
            return true;
        }

        _w.Write(CSharpTypeDisplay(targetType));
        _w.Write(" ");
        _w.Write(EscapeId(ip.Name));
        _w.WriteLine(";");
        AssignInto(EscapeId(ip.Name), ls.Initializer, targetType);
        return true;
    }

    private int _letCounter;

    /// <summary>
    /// Map from a let-binding's declaration span to the synthesized C#
    /// name the emitter chose for it (because the natural name would
    /// collide with a pattern binding inside the let's lowered match).
    /// IdentifierExpr emit consults this via the resolver so only
    /// references to *this specific binding* get rewritten — same-named
    /// pattern bindings inside other arms keep their natural emit.
    /// </summary>
    private readonly Dictionary<SourceSpan, string> _renamedBindings = new();

    /// <summary>
    /// True iff any pattern binding inside the let's initializer (across
    /// every if/match arm we'll lower) reuses <paramref name="targetName"/>.
    /// Drives the shadow-guard rebind in <see cref="TryEmitStmtLoweredLet"/>.
    /// </summary>
    private static bool InitializerBindingsCollideWith(Expression e, string targetName) => e switch
    {
        IfExpr ie => BlockBindingsCollide(ie.Then, targetName)
            || (ie.Else is { } elseBlock && BlockBindingsCollide(elseBlock, targetName)),
        MatchExpr me => me.Arms.Any(arm =>
            PatternBindsName(arm.Pattern, targetName)
            || (arm.Body is BlockExpr ab && BlockBindingsCollide(ab, targetName))),
        _ => false,
    };

    private static bool BlockBindingsCollide(BlockExpr block, string targetName)
        => block.Statements.Any(s => s switch
        {
            LetStmt ls when ls.Target is IdentifierPattern lp => lp.Name == targetName,
            _ => false,
        });

    private static bool PatternBindsName(Pattern p, string name) => p switch
    {
        IdentifierPattern ip => ip.Name == name,
        ConstructorPattern cp => cp.Arguments.Any(a => PatternBindsName(a, name)),
        RecordPattern rp => rp.Fields.Any(f => PatternBindsName(f.Subpattern, name)),
        TuplePattern tp => tp.Elements.Any(el => PatternBindsName(el, name)),
        _ => false,
    };

    private bool TryEmitStmtLoweredAssign(AssignmentStmt asn)
    {
        if (!NeedsStmtLowering(asn.Value)) return false;
        // We know the LHS name exists; assign into it directly.
        AssignInto(EscapeId(asn.Name), asn.Value, UnknownType.Instance);
        return true;
    }

    /// <summary>Pre-emit each liftable nested conditional as a locally-scoped
    /// temp, filling in <see cref="_liftedConditionals"/> so the subsequent
    /// expression emission substitutes the temp name at the original span.</summary>
    private void LiftNestedConditionals(List<Expression> liftables)
    {
        foreach (var lifted in liftables)
        {
            var type = TypeOf(lifted);
            var tempName = $"__lift_{_liftCounter++}";
            _w.Write(CSharpTypeDisplay(type));
            _w.WriteLine($" {tempName};");
            AssignInto(tempName, lifted, type);
            _liftedConditionals[lifted.Span] = tempName;
        }
    }

    /// <summary>Find <c>if</c>/<c>match</c> subtrees that (a) contain a
    /// propagating <c>?</c> site and (b) are nested inside another expression
    /// (not the statement-level root). Those are the cases the regular
    /// expression emitter would have to fall back to <c>.Unwrap()</c> for —
    /// lifting them into pre-computed locals lets the <c>?</c> inside each
    /// branch get the proper early-return treatment.
    ///
    /// Siblings are collected independently; a liftable conditional's own
    /// branches are NOT recursed into because they'll be emitted via
    /// <see cref="AssignInto"/> when we lower the lifted form (which itself
    /// invokes the same helpers and handles any further nesting).</summary>
    private static List<Expression> CollectLiftableNestedConditionals(Expression topLevel)
    {
        var results = new List<Expression>();

        void Visit(Expression e, bool isRoot)
        {
            switch (e)
            {
                case IfExpr ie:
                    if (!isRoot && ContainsPropagate(ie))
                    {
                        results.Add(ie);
                        // Don't cross into branches — they'll emit via stmt-lowering.
                        Visit(ie.Condition, false);
                        return;
                    }
                    // Either top-level (handled by NeedsStmtLowering path) or
                    // propagate-free (no lifting needed). Keep walking subexprs.
                    Visit(ie.Condition, false);
                    foreach (var stmt in ie.Then.Statements) VisitStmt(stmt);
                    if (ie.Then.TrailingExpression is { } tt) Visit(tt, false);
                    if (ie.Else is { } elseBlock)
                    {
                        foreach (var stmt in elseBlock.Statements) VisitStmt(stmt);
                        if (elseBlock.TrailingExpression is { } et) Visit(et, false);
                    }
                    break;

                case MatchExpr me:
                    if (!isRoot && ContainsPropagate(me))
                    {
                        results.Add(me);
                        Visit(me.Scrutinee, false);
                        return;
                    }
                    Visit(me.Scrutinee, false);
                    foreach (var arm in me.Arms) Visit(arm.Body, false);
                    break;

                case CallExpr c:
                    Visit(c.Callee, false);
                    foreach (var arg in c.Arguments) Visit(arg.Value, false);
                    break;

                case BinaryExpr be:
                    Visit(be.Left, false);
                    Visit(be.Right, false);
                    break;

                case UnaryExpr ue:
                    Visit(ue.Operand, false);
                    break;

                case PropagateExpr pr:
                    Visit(pr.Operand, false);
                    break;

                case AwaitExpr aw:
                    Visit(aw.Operand, false);
                    break;

                case FieldAccessExpr fa:
                    Visit(fa.Target, false);
                    break;

                case RecordLiteralExpr rl:
                    foreach (var f in rl.Fields) Visit(f.Value, false);
                    break;

                case WithExpr we:
                    Visit(we.Target, false);
                    foreach (var u in we.Updates) Visit(u.Value, false);
                    break;

                case TupleExpr te:
                    foreach (var el in te.Elements) Visit(el, false);
                    break;

                case InterpolatedStringExpr isx:
                    foreach (var p in isx.Parts)
                    {
                        if (p is StringInterpolationPart ip) Visit(ip.Expression, false);
                    }
                    break;

                // Other forms (blocks, parallel, race, unsafe, trace, loops,
                // identifiers, literals) either have their own evaluation
                // scope or no subexpressions to contribute liftables.
                default:
                    break;
            }
        }

        void VisitStmt(Statement s)
        {
            switch (s)
            {
                case LetStmt ls: Visit(ls.Initializer, false); break;
                case AssignmentStmt asn: Visit(asn.Value, false); break;
                case DiscardStmt ds: Visit(ds.Value, false); break;
                case ExpressionStmt es: Visit(es.Expression, false); break;
            }
        }

        Visit(topLevel, isRoot: true);
        return results;
    }

    /// <summary>True when <paramref name="e"/> is directly an if or match whose
    /// body contains a propagating site. Nested cases (propagate buried inside
    /// a call argument, say) aren't eligible here — they keep their existing
    /// emission.</summary>
    private static bool NeedsStmtLowering(Expression e)
    {
        if (e is IfExpr ie)
        {
            return ContainsPropagate(ie.Then)
                || (ie.Else is { } elseBlock && ContainsPropagate(elseBlock))
                || ContainsReturn(ie.Then)
                || (ie.Else is { } elseBlock2 && ContainsReturn(elseBlock2));
        }
        if (e is MatchExpr me)
        {
            return me.Arms.Any(arm => ContainsPropagate(arm.Body))
                || me.Arms.Any(arm => ContainsReturn(arm.Body));
        }
        return false;
    }

    /// <summary>
    /// True iff a match-arm or if-arm body's flow exits the enclosing
    /// method via <c>return</c> (so the C# emit shouldn't follow the
    /// arm body with a <c>break;</c> — it'd be flagged as unreachable).
    /// Two shapes count: the body is a <see cref="ReturnExpr"/> directly,
    /// or it's a <see cref="BlockExpr"/> whose trailing expression is a
    /// <see cref="ReturnExpr"/>.
    /// </summary>
    private static bool ArmExitsViaReturn(Expression body) => body switch
    {
        ReturnExpr => true,
        BlockExpr b => b.TrailingExpression is ReturnExpr,
        _ => false,
    };

    /// <summary>True iff the expression subtree contains a
    /// <see cref="ReturnExpr"/>. Mirrors <see cref="ContainsPropagate"/>:
    /// signals to <see cref="NeedsStmtLowering"/> that the surrounding
    /// let/if/match initializer must lower to statement form so the
    /// `return` actually exits the C# method.</summary>
    private static bool ContainsReturn(Expression e) => e switch
    {
        ReturnExpr => true,
        PropagateExpr pr => ContainsReturn(pr.Operand),
        AwaitExpr aw => ContainsReturn(aw.Operand),
        UnaryExpr ue => ContainsReturn(ue.Operand),
        BinaryExpr be => ContainsReturn(be.Left) || ContainsReturn(be.Right),
        CallExpr c => ContainsReturn(c.Callee) || c.Arguments.Any(a => ContainsReturn(a.Value)),
        FieldAccessExpr fa => ContainsReturn(fa.Target),
        IfExpr ie => ContainsReturn(ie.Condition) || ContainsReturn(ie.Then)
            || (ie.Else is { } b && ContainsReturn(b)),
        MatchExpr me => ContainsReturn(me.Scrutinee) || me.Arms.Any(a => ContainsReturn(a.Body)),
        BlockExpr b => b.Statements.Any(s => s switch
        {
            LetStmt ls => ContainsReturn(ls.Initializer),
            AssignmentStmt asn => ContainsReturn(asn.Value),
            DiscardStmt ds => ContainsReturn(ds.Value),
            ExpressionStmt es => ContainsReturn(es.Expression),
            _ => false,
        }) || (b.TrailingExpression is { } t && ContainsReturn(t)),
        TupleExpr te => te.Elements.Any(ContainsReturn),
        RecordLiteralExpr rl => rl.Fields.Any(f => ContainsReturn(f.Value)),
        WithExpr we => ContainsReturn(we.Target) || we.Updates.Any(u => ContainsReturn(u.Value)),
        InterpolatedStringExpr isx => isx.Parts.OfType<StringInterpolationPart>()
            .Any(p => ContainsReturn(p.Expression)),
        _ => false,
    };

    /// <summary>Whether an expression subtree contains any <c>?</c> or <c>|&gt;?</c>
    /// site. Used by <see cref="NeedsStmtLowering"/> to decide between the
    /// expression-level and statement-level lowerings.</summary>
    private static bool ContainsPropagate(Expression e) => e switch
    {
        PropagateExpr => true,
        BinaryExpr { Op: BinaryOp.PipePropagate } => true,
        BinaryExpr be => ContainsPropagate(be.Left) || ContainsPropagate(be.Right),
        UnaryExpr ue => ContainsPropagate(ue.Operand),
        AwaitExpr aw => ContainsPropagate(aw.Operand),
        CallExpr c => ContainsPropagate(c.Callee) || c.Arguments.Any(a => ContainsPropagate(a.Value)),
        FieldAccessExpr fa => ContainsPropagate(fa.Target),
        IfExpr ie => ContainsPropagate(ie.Then) || (ie.Else is { } b && ContainsPropagate(b)),
        MatchExpr me => ContainsPropagate(me.Scrutinee) || me.Arms.Any(a => ContainsPropagate(a.Body)),
        BlockExpr b =>
            b.Statements.Any(StmtContainsPropagate)
            || (b.TrailingExpression is { } t && ContainsPropagate(t)),
        TupleExpr te => te.Elements.Any(ContainsPropagate),
        RecordLiteralExpr rl => rl.Fields.Any(f => ContainsPropagate(f.Value)),
        WithExpr we => ContainsPropagate(we.Target) || we.Updates.Any(u => ContainsPropagate(u.Value)),
        InterpolatedStringExpr isx => isx.Parts.OfType<StringInterpolationPart>()
            .Any(p => ContainsPropagate(p.Expression)),
        _ => false,
    };

    private static bool StmtContainsPropagate(Statement s) => s switch
    {
        LetStmt ls => ContainsPropagate(ls.Initializer),
        AssignmentStmt asn => ContainsPropagate(asn.Value),
        DiscardStmt ds => ContainsPropagate(ds.Value),
        ExpressionStmt es => ContainsPropagate(es.Expression),
        _ => false,
    };

    /// <summary>Whether a fn body contains <c>.await</c> anywhere — the signal
    /// that its C# emission is <c>async Task&lt;T&gt;</c> rather than plain
    /// <c>T</c>. Mirrors <see cref="ContainsPropagate"/> but for
    /// <see cref="AwaitExpr"/>.</summary>
    private static bool BodyContainsAwaitExpr(Expression e) => e switch
    {
        AwaitExpr => true,
        PropagateExpr pr => BodyContainsAwaitExpr(pr.Operand),
        UnaryExpr ue => BodyContainsAwaitExpr(ue.Operand),
        BinaryExpr be => BodyContainsAwaitExpr(be.Left) || BodyContainsAwaitExpr(be.Right),
        CallExpr c => BodyContainsAwaitExpr(c.Callee)
            || c.Arguments.Any(a => BodyContainsAwaitExpr(a.Value)),
        FieldAccessExpr fa => BodyContainsAwaitExpr(fa.Target),
        IfExpr ie => BodyContainsAwaitExpr(ie.Condition) || BodyContainsAwaitExpr(ie.Then)
            || (ie.Else is { } b && BodyContainsAwaitExpr(b)),
        MatchExpr me => BodyContainsAwaitExpr(me.Scrutinee)
            || me.Arms.Any(a => BodyContainsAwaitExpr(a.Body)),
        BlockExpr b => b.Statements.Any(s => s switch
        {
            LetStmt ls => BodyContainsAwaitExpr(ls.Initializer),
            AssignmentStmt asn => BodyContainsAwaitExpr(asn.Value),
            DiscardStmt ds => BodyContainsAwaitExpr(ds.Value),
            ExpressionStmt es => BodyContainsAwaitExpr(es.Expression),
            _ => false,
        }) || (b.TrailingExpression is { } t && BodyContainsAwaitExpr(t)),
        TupleExpr te => te.Elements.Any(BodyContainsAwaitExpr),
        RecordLiteralExpr rl => rl.Fields.Any(f => BodyContainsAwaitExpr(f.Value)),
        WithExpr we => BodyContainsAwaitExpr(we.Target)
            || we.Updates.Any(u => BodyContainsAwaitExpr(u.Value)),
        InterpolatedStringExpr isx => isx.Parts.OfType<StringInterpolationPart>()
            .Any(p => BodyContainsAwaitExpr(p.Expression)),
        ParallelExpr pe => pe.Tasks.Any(BodyContainsAwaitExpr),
        RaceExpr re => re.Tasks.Any(BodyContainsAwaitExpr),
        _ => false,
    };

    /// <summary>
    /// Emit a C# statement that assigns <paramref name="e"/> into the lvalue
    /// <paramref name="target"/>. Recurses through if / match to produce real
    /// C# if/else / switch statements; `?`-hoists fire at each leaf position.
    /// For shapes that don't restructure (a plain expression leaf), hoists
    /// run first and then `target = expr;` is emitted.
    /// </summary>
    private void AssignInto(string target, Expression e, TypeRef targetType)
    {
        switch (e)
        {
            case IfExpr ie:
                _w.Write("if (");
                EmitExpression(ie.Condition);
                _w.WriteLine(")");
                _w.WriteLine("{");
                using (_w.Indent()) AssignIntoBlock(target, ie.Then, targetType);
                _w.WriteLine("}");
                if (ie.Else is { } elseBlock)
                {
                    _w.WriteLine("else");
                    _w.WriteLine("{");
                    using (_w.Indent()) AssignIntoBlock(target, elseBlock, targetType);
                    _w.WriteLine("}");
                }
                else
                {
                    _w.WriteLine("else");
                    _w.WriteLine("{");
                    using (_w.Indent()) _w.WriteLine($"{target} = Unit.Value;");
                    _w.WriteLine("}");
                }
                break;

            case MatchExpr me:
                var scrutineeType = TypeOf(me.Scrutinee);
                _w.Write("switch (");
                EmitExpression(me.Scrutinee);
                _w.WriteLine(")");
                _w.WriteLine("{");
                using (_w.Indent())
                {
                    foreach (var arm in me.Arms)
                    {
                        _w.Write("case ");
                        EmitPatternForMatch(arm.Pattern, scrutineeType);
                        _w.WriteLine(":");
                        using (_w.Indent())
                        {
                            AssignIntoExpression(target, arm.Body, targetType);
                            // Skip the `break;` when the arm exits via
                            // `return` — C# would flag it as unreachable
                            // (CS0162), which is an error under
                            // TreatWarningsAsErrors. The default-arm at
                            // the bottom keeps the switch exhaustive for
                            // definite-assignment analysis.
                            if (!ArmExitsViaReturn(arm.Body))
                            {
                                _w.WriteLine("break;");
                            }
                        }
                    }
                    // Default arm so C# definite-assignment analysis sees
                    // an exhaustive switch and the variable is provably
                    // assigned (or the method has exited) after the
                    // switch — the Overt typer rejects non-exhaustive
                    // matches at OV0308, so this default is unreachable
                    // in well-typed programs.
                    _w.WriteLine("default:");
                    using (_w.Indent())
                    {
                        _w.WriteLine(
                            "throw new global::System.InvalidOperationException("
                            + "\"Overt match: unreachable arm\");");
                    }
                }
                _w.WriteLine("}");
                break;

            default:
                // Plain expression leaf — handle any nested conditional-with-`?`
                // the same way the statement-level path does, hoist any top-level
                // `?`, then assign. The lift runs first so lifted sites end up in
                // _liftedConditionals before EmitExpression walks the subtree.
                LiftNestedConditionals(CollectLiftableNestedConditionals(e));
                EmitHoistsForExpression(e);
                _w.Write(target);
                _w.Write(" = ");
                WithExpected(targetType, () => EmitExpression(e));
                _w.WriteLine(";");
                break;
        }
    }

    /// <summary>Assign the value of a block into <paramref name="target"/>. Emits
    /// the block's statements, then assigns its trailing expression (recursing
    /// through nested if/match via <see cref="AssignIntoExpression"/>).</summary>
    private void AssignIntoBlock(string target, BlockExpr block, TypeRef targetType)
    {
        foreach (var stmt in block.Statements) EmitStatement(stmt);
        if (block.TrailingExpression is { } tail)
        {
            AssignIntoExpression(target, tail, targetType);
        }
        else
        {
            _w.WriteLine($"{target} = Unit.Value;");
        }
    }

    /// <summary>Like <see cref="AssignInto"/> but treats a BlockExpr as a block
    /// (inline statements + trailing) rather than as an IIFE-wrapped expression.
    /// This is the right thing when we're already at statement position.</summary>
    private void AssignIntoExpression(string target, Expression e, TypeRef targetType)
    {
        if (e is ReturnExpr rx)
        {
            // The "value" being assigned is itself a return — control
            // flow exits the enclosing method here, no assignment needed.
            EmitHoistsForExpression(rx.Value);
            _w.Write("return ");
            EmitExpression(rx.Value);
            _w.WriteLine(";");
        }
        else if (e is BlockExpr b)
        {
            AssignIntoBlock(target, b, targetType);
        }
        else
        {
            AssignInto(target, e, targetType);
        }
    }

    // --------------------------------------------------- ? propagation lowering
    //
    // Every `?` on a Result<T, E> in an unconditionally-evaluated position is
    // hoisted into a local before the enclosing statement: a temporary holds the
    // Result, an `if (IsErr) return Err(...)` branch propagates, and a second local
    // extracts the Ok value. The expression walker then substitutes the Ok-local at
    // the `?` site. This gives DESIGN.md §11 its real semantics — errors as values,
    // no hidden unwinding.
    //
    // `?` inside conditionally-evaluated subexpressions (if/match/while arms, block
    // expressions) is NOT hoisted by this pass; evaluating both branches would be
    // incorrect. Those sites fall back to .Unwrap() for now and are a known gap for
    // a follow-up that generates per-branch local hoisting.

    /// <summary>
    /// Walk <paramref name="expr"/> collecting every propagating site (<c>?</c> or
    /// <c>|&gt;?</c>) in an always-evaluated position, and emit the hoist preamble
    /// (<c>var __q_N = ...; if (...) return Err(...)</c>) for each. After this
    /// call, the expression walker substitutes the hoisted Ok-local at each site
    /// via <see cref="_hoistMap"/>.
    /// </summary>
    private void EmitHoistsForExpression(Expression expr)
    {
        var hoists = new List<Expression>();
        CollectHoistablePropagates(expr, hoists);
        foreach (var node in hoists)
        {
            EmitSingleHoist(node);
        }
    }

    /// <summary>
    /// Recursively gather propagating sites (<see cref="PropagateExpr"/> and
    /// pipe-propagate <see cref="BinaryExpr"/>s) that are always evaluated when
    /// the containing statement runs. Stops at conditional-evaluation boundaries
    /// (if / match / while / block-as-expression) so branches aren't eagerly
    /// forced.
    /// </summary>
    private static void CollectHoistablePropagates(Expression expr, List<Expression> into)
    {
        switch (expr)
        {
            case PropagateExpr pr:
                CollectHoistablePropagates(pr.Operand, into);
                into.Add(pr);
                break;

            case BinaryExpr { Op: BinaryOp.PipePropagate } be:
                // `a |>? f(b)` — recurse into both sides, then hoist the pipe
                // itself. The "operand" of the implicit `?` is the spliced call,
                // which EmitSingleHoist knows how to emit.
                CollectHoistablePropagates(be.Left, into);
                CollectHoistablePropagates(be.Right, into);
                into.Add(be);
                break;

            case CallExpr c:
                CollectHoistablePropagates(c.Callee, into);
                foreach (var arg in c.Arguments)
                    CollectHoistablePropagates(arg.Value, into);
                break;

            case BinaryExpr be:
                CollectHoistablePropagates(be.Left, into);
                CollectHoistablePropagates(be.Right, into);
                break;

            case UnaryExpr ue:
                CollectHoistablePropagates(ue.Operand, into);
                break;

            case AwaitExpr aw:
                // `.await` doesn't need hoisting — C# `await` IS the hoisting —
                // but its operand might still contain `?` sites that do.
                CollectHoistablePropagates(aw.Operand, into);
                break;

            case FieldAccessExpr fa:
                CollectHoistablePropagates(fa.Target, into);
                break;

            case RecordLiteralExpr rl:
                foreach (var f in rl.Fields) CollectHoistablePropagates(f.Value, into);
                break;

            case WithExpr we:
                CollectHoistablePropagates(we.Target, into);
                foreach (var f in we.Updates) CollectHoistablePropagates(f.Value, into);
                break;

            case TupleExpr te:
                foreach (var e in te.Elements) CollectHoistablePropagates(e, into);
                break;

            case InterpolatedStringExpr isx:
                foreach (var part in isx.Parts)
                    if (part is StringInterpolationPart iep)
                        CollectHoistablePropagates(iep.Expression, into);
                break;

            // Stop at conditional/block boundaries — branches should not be hoisted
            // eagerly. Same for identifier/literal leaves and language forms whose
            // bodies are their own hoisting scope.
            case IfExpr:
            case MatchExpr:
            case WhileExpr:
            case ForEachExpr:
            case LoopExpr:
            case BlockExpr:
            case ParallelExpr:
            case RaceExpr:
            case UnsafeExpr:
            case TraceExpr:
            case IdentifierExpr:
            case IntegerLiteralExpr:
            case FloatLiteralExpr:
            case BooleanLiteralExpr:
            case StringLiteralExpr:
            case UnitExpr:
                break;
        }
    }

    /// <summary>
    /// Emit the preamble for one hoisted propagating site (<c>?</c> or <c>|&gt;?</c>):
    /// evaluate the operand into a temp, early-return <c>Err(...)</c> on failure,
    /// extract the Ok value into a second temp. Records the Ok-local in
    /// <see cref="_hoistMap"/> keyed by the site's span.
    ///
    /// C# infers the <c>Result&lt;_, _&gt;</c> generics via <c>var</c>, so the hoist
    /// doesn't need to spell them — it only needs the enclosing function's return
    /// error type to target-type the <c>Err&lt;E&gt;(...)</c> factory. That means the
    /// only prerequisite is that the enclosing function returns a <c>Result&lt;_, E&gt;</c>.
    /// </summary>
    private void EmitSingleHoist(Expression node)
    {
        Expression operand;
        SourceSpan siteSpan;
        switch (node)
        {
            case PropagateExpr pr:
                operand = pr.Operand;
                siteSpan = pr.Span;
                break;
            case BinaryExpr { Op: BinaryOp.PipePropagate } be:
                operand = be;
                siteSpan = be.Span;
                break;
            default:
                return;
        }

        // Can't hoist if we don't know the enclosing function's error type — fall
        // back to .Unwrap() at the site.
        if (_currentFnReturn is not NamedTypeRef
            { Name: "Result", TypeArguments: { Length: 2 } retArgs })
        {
            return;
        }
        var errCs = CSharpTypeDisplay(retArgs[1]);

        var id = _propagateCounter++;
        var qName = $"__q_{id}";

        _w.Write($"var {qName} = ");
        if (operand is BinaryExpr pipe)
        {
            EmitPipeSpliceOnly(pipe);
        }
        else
        {
            EmitExpression(operand);
        }
        _w.WriteLine(";");
        _w.WriteLine($"if (!{qName}.IsOk) return Err<{errCs}>({qName}.UnwrapErr());");

        // Substitute `__q_N.Unwrap()` at the `?` use site rather than stashing
        // the unwrapped value in a second local. Unwrap() is cheap after the
        // IsOk check above, and eliding the temp removes the bulk of the
        // `__q_N`/`__qv_N` noise from the emitted source.
        _hoistMap[siteSpan] = $"{qName}.Unwrap()";
    }

    /// <summary>
    /// Emit the pipe-splice for a <c>|&gt;?</c> without its trailing <c>.Unwrap()</c>.
    /// Used by hoisting so the Result is captured pre-unwrap for an early-return
    /// check. Mirrors <see cref="EmitPipe"/>'s splice logic.
    /// </summary>
    private void EmitPipeSpliceOnly(BinaryExpr be)
    {
        if (be.Right is CallExpr call)
        {
            _w.Write("(");
            EmitExpression(call.Callee);
            _w.Write("(");
            EmitExpression(be.Left);
            foreach (var arg in call.Arguments)
            {
                _w.Write(", ");
                if (arg.Name is { } name)
                {
                    _w.Write(name);
                    _w.Write(": ");
                }
                EmitExpression(arg.Value);
            }
            _w.Write("))");
        }
        else
        {
            _w.Write("(");
            EmitExpression(be.Right);
            _w.Write("(");
            EmitExpression(be.Left);
            _w.Write("))");
        }
    }

    /// <summary>
    /// Emit an expression as a C# statement. Control-flow constructs that look natural
    /// as expressions in Overt (<c>if</c>, <c>while</c>, <c>match</c>, <c>unsafe</c>,
    /// <c>trace</c>, and blocks) map directly to C#'s statement-level forms when the
    /// value is discarded. Everything else emits as <c>expr;</c>.
    /// </summary>
    private void EmitExpressionAsStatement(Expression expr)
    {
        switch (expr)
        {
            case ReturnExpr rx:
                // `return X` at statement position lowers to a real C#
                // return. Hoist any `?` inside the returned expression
                // first so an inner Err exits via early-return rather
                // than threading the `return` through a Result wrap.
                LiftNestedConditionals(CollectLiftableNestedConditionals(rx.Value));
                EmitHoistsForExpression(rx.Value);
                _w.Write("return ");
                EmitExpression(rx.Value);
                _w.WriteLine(";");
                break;

            case IfExpr ie:
                _w.Write("if (");
                EmitExpression(ie.Condition);
                _w.WriteLine(")");
                EmitBlockAsStatement(ie.Then);
                if (ie.Else is { } elseBlock)
                {
                    _w.WriteLine("else");
                    EmitBlockAsStatement(elseBlock);
                }
                break;

            case WhileExpr we:
                _w.Write("while (");
                EmitExpression(we.Condition);
                _w.WriteLine(")");
                EmitBlockAsStatement(we.Body);
                break;

            case ForEachExpr fe:
                // Overt's List<T> wraps ImmutableArray<T> in .Items, so iterate that.
                _w.Write("foreach (var ");
                EmitPatternForBinding(fe.Binder);
                _w.Write(" in (");
                EmitExpression(fe.Iterable);
                _w.WriteLine(").Items)");
                EmitBlockAsStatement(fe.Body);
                break;

            case LoopExpr lp:
                _w.WriteLine("while (true)");
                EmitBlockAsStatement(lp.Body);
                break;

            case MatchExpr me:
                // `match scrutinee { ... }` in statement position: emit as a switch
                // statement with each arm's body as a case block. Variant patterns still
                // go through EmitPatternForMatch so stdlib variants resolve to their
                // typed C# record forms.
                var scrutineeType = TypeOf(me.Scrutinee);
                _w.Write("switch (");
                EmitExpression(me.Scrutinee);
                _w.WriteLine(")");
                _w.WriteLine("{");
                using (_w.Indent())
                {
                    foreach (var arm in me.Arms)
                    {
                        _w.Write("case ");
                        EmitPatternForMatch(arm.Pattern, scrutineeType);
                        _w.WriteLine(":");
                        using (_w.Indent())
                        {
                            EmitExpressionAsStatement(arm.Body);
                            _w.WriteLine("break;");
                        }
                    }
                }
                _w.WriteLine("}");
                break;

            case BlockExpr b:
                EmitBlockAsStatement(b);
                break;

            case UnsafeExpr ux:
                EmitBlockAsStatement(ux.Body);
                break;

            case TraceExpr tx:
                EmitBlockAsStatement(tx.Body);
                break;

            // Everything else: just write the expression plus a trailing `;`. C# will
            // reject anything that isn't a valid statement-expression (calls, assignments,
            // increments, etc.) — that's the type checker's job later.
            default:
                EmitHoistsForExpression(expr);
                // If the root is a `?` that we just hoisted, the side effect has
                // already run in the hoist preamble and the value is discarded
                // anyway — emitting `__qv_N;` would be a bare-identifier statement
                // (CS0201). Skip the trailing emit in that case.
                if (expr is PropagateExpr pr && _hoistMap.ContainsKey(pr.Span)) break;
                EmitExpression(expr);
                _w.WriteLine(";");
                break;
        }
    }

    /// <summary>
    /// Emits a pattern as the left-hand side of a <c>var</c> binding. Tuple patterns
    /// become C# tuple-var syntax; identifier patterns are just the name.
    /// </summary>
    private void EmitPatternForBinding(Pattern pattern)
    {
        switch (pattern)
        {
            case IdentifierPattern ip:
                _w.Write(EscapeId(ip.Name));
                break;
            case TuplePattern tp:
                _w.Write("(");
                for (var i = 0; i < tp.Elements.Length; i++)
                {
                    if (i > 0) _w.Write(", ");
                    EmitPatternForBinding(tp.Elements[i]);
                }
                _w.Write(")");
                break;
            case WildcardPattern:
                _w.Write("_");
                break;
            default:
                _w.Write("/* TODO pattern */ _");
                break;
        }
    }

    // -------------------------------------------------------- expressions

    private void EmitExpression(Expression expr)
    {
        // Refinement-boundary wrapping: when the type checker has flagged this
        // expression's span as flowing into a non-generic refinement whose
        // predicate it couldn't decide at compile time, wrap the emission in
        // `{Alias}__Check(...)` — a synthesized helper that runs the predicate
        // and throws RefinementViolation on failure.
        if (_types?.RefinementBoundaries is { } boundaries
            && boundaries.TryGetValue(expr.Span, out var aliasName))
        {
            _w.Write($"__Refinements.{aliasName}__Check(");
            EmitExpressionBody(expr);
            _w.Write(")");
            return;
        }

        EmitExpressionBody(expr);
    }

    private void EmitExpressionBody(Expression expr)
    {
        switch (expr)
        {
            case IntegerLiteralExpr i:
                _w.Write(i.Lexeme.Replace("_", ""));
                break;

            case FloatLiteralExpr f:
                _w.Write(f.Lexeme.Replace("_", ""));
                break;

            case BooleanLiteralExpr b:
                _w.Write(b.Value ? "true" : "false");
                break;

            case StringLiteralExpr s:
                _w.Write(s.Value); // already includes surrounding quotes
                break;

            case UnitExpr:
                _w.Write("Unit.Value");
                break;

            case IdentifierExpr id:
                // If this identifier resolves to a let binding the
                // shadow-guard renamed (see TryEmitStmtLoweredLet), write
                // the synthesized C# name instead. Other identifiers —
                // including pattern bindings that happen to share the
                // textual name — go through the default escape path.
                if (_resolution?.Resolutions.TryGetValue(id.Span, out var sym) == true
                    && _renamedBindings.TryGetValue(sym.DeclarationSpan, out var rebound))
                {
                    _w.Write(rebound);
                }
                else
                {
                    _w.Write(EscapeId(id.Name));
                }
                break;

            case FieldAccessExpr fa:
                // Without type info, we can't tell an enum variant reference
                // (<c>ConnectionState.Closed</c>) from an ordinary field access
                // (<c>user.name</c>). Apply a heuristic: when the target is a bare
                // identifier whose name is PascalCase and the accessed name is also
                // PascalCase, treat it as an enum-variant reference and emit the
                // flat <c>Name_Variant</c> form used by the enum lowering, as a
                // constructor call with no args. Everything else is a member access.
                if (IsLikelyEnumVariantRef(fa))
                {
                    // Widening cast to the enum base type: target-typing `Ok(variant)`
                    // needs T to unify with the base, not the specific variant's type.
                    var baseName = ((IdentifierExpr)fa.Target).Name;
                    _w.Write($"(({baseName})new {baseName}_{fa.FieldName}())");
                }
                else if (fa.Target is IdentifierExpr { Name: "String" })
                {
                    // `String.split` / `String.join` / `String.code_at` etc. land
                    // on the static <c>Overt.Runtime.String</c> class. Emit the
                    // fully-qualified name; bare <c>String</c> would collide with
                    // <c>System.String</c> (in scope via the generated `using
                    // System;` line). User code can't add methods to the primitive
                    // String type, so any <c>String.X</c> dotted access must be a
                    // stdlib namespace call — the qualification is safe.
                    _w.Write("global::Overt.Runtime.String.");
                    _w.Write(EscapeId(fa.FieldName));
                }
                else
                {
                    EmitExpression(fa.Target);
                    _w.Write(".");
                    _w.Write(EscapeId(fa.FieldName));
                }
                break;

            case CallExpr c:
                EmitCall(c);
                break;

            case PropagateExpr pr:
                // If a statement-level pre-pass has already hoisted this `?` into a
                // local (the common case — see CollectHoistablePropagates), emit the
                // local's name; the Err path has already returned above this point.
                if (_hoistMap.TryGetValue(pr.Span, out var hoistedName))
                {
                    _w.Write(hoistedName);
                    break;
                }

                // Fallback for `?` nested inside a conditionally-evaluated context
                // (if/match/while arms, or block-as-expression) where eager hoisting
                // would evaluate branches that shouldn't run. These sites still use
                // .Unwrap() — which throws on Err — and are tracked as a known gap
                // for conditional-hoist lowering in a follow-up.
                var innerExpected = _expectedType is not null and not UnknownType
                    ? new NamedTypeRef("Result",
                        ImmutableArray.Create<TypeRef>(_expectedType, UnknownType.Instance))
                    : (TypeRef?)null;
                _w.Write("(");
                WithExpected(innerExpected, () => EmitExpression(pr.Operand));
                _w.Write(").Unwrap()");
                break;

            case AwaitExpr aw:
                // `t.await` → C# `(await t)`. Parentheses so the unwrapped value
                // composes in any expression position without precedence ties.
                _w.Write("(await ");
                EmitExpression(aw.Operand);
                _w.Write(")");
                break;

            case ReturnExpr:
                // ReturnExpr should only be emitted via the dedicated paths
                // in EmitStatement / AssignIntoExpression / EmitExpressionAsStatement
                // / EmitBlockAsMethodBody / NeedsStmtLowering. Hitting this
                // branch means a `return` slipped into an unsupported
                // position (e.g. inside a tuple element or a record field
                // initializer). Surface it at codegen time so the bug is
                // visible rather than producing weird-looking C#.
                throw new InvalidOperationException(
                    "ReturnExpr in unsupported position — `return` must appear "
                    + "in a statement context (block trailing, match/if arm body, "
                    + "or as an expression statement). Lifting `return` out of a "
                    + "deeper expression position is not yet implemented.");

            case BinaryExpr be:
                EmitBinary(be);
                break;

            case UnaryExpr ue:
                _w.Write(ue.Op == UnaryOp.Negate ? "-(" : "!(");
                EmitExpression(ue.Operand);
                _w.Write(")");
                break;

            case InterpolatedStringExpr isx:
                EmitInterpolatedString(isx);
                break;

            case IfExpr ie:
                if (_liftedConditionals.TryGetValue(ie.Span, out var liftedIfName))
                {
                    _w.Write(liftedIfName);
                    break;
                }
                EmitIf(ie);
                break;

            case WhileExpr we:
                _w.Write("((Func<Unit>)(() => { while (");
                EmitExpression(we.Condition);
                _w.Write(") ");
                EmitBlockAsStatement(we.Body);
                _w.Write(" return Unit.Value; }))()");
                break;

            case ForEachExpr fe:
                _w.Write("((Func<Unit>)(() => { foreach (var ");
                EmitPatternForBinding(fe.Binder);
                _w.Write(" in (");
                EmitExpression(fe.Iterable);
                _w.Write(").Items) ");
                EmitBlockAsStatement(fe.Body);
                _w.Write(" return Unit.Value; }))()");
                break;

            case LoopExpr lp:
                _w.Write("((Func<Unit>)(() => { while (true) ");
                EmitBlockAsStatement(lp.Body);
                _w.Write(" return Unit.Value; }))()");
                break;

            case BlockExpr b:
                EmitBlockAsExpression(b);
                break;

            case TupleExpr te:
                _w.Write("(");
                for (var i = 0; i < te.Elements.Length; i++)
                {
                    if (i > 0) _w.Write(", ");
                    EmitExpression(te.Elements[i]);
                }
                _w.Write(")");
                break;

            case RecordLiteralExpr rl:
                EmitRecordLiteral(rl);
                break;

            case WithExpr w:
                _w.Write("(");
                EmitExpression(w.Target);
                _w.Write(" with { ");
                for (var i = 0; i < w.Updates.Length; i++)
                {
                    if (i > 0) _w.Write(", ");
                    _w.Write(EscapeId(w.Updates[i].Name));
                    _w.Write(" = ");
                    EmitExpression(w.Updates[i].Value);
                }
                _w.Write(" })");
                break;

            case MatchExpr me:
                if (_liftedConditionals.TryGetValue(me.Span, out var liftedMatchName))
                {
                    _w.Write(liftedMatchName);
                    break;
                }
                EmitMatch(me);
                break;

            case ParallelExpr pe:
                // parallel returns `Result<(T1, ..., Tn), E>` where each Ti is the
                // Ok-type of the corresponding task and E is the shared error type
                // (DESIGN.md §12). When the tasks type-check as Results, we can derive
                // the exact Result shape; otherwise we fall back to object.
                EmitParallelPlaceholder(pe);
                break;

            case RaceExpr re:
                EmitRacePlaceholder(re);
                break;

            case UnsafeExpr ux:
                // unsafe-block semantics don't exist in C# the same way. Just emit the body.
                EmitBlockAsExpression(ux.Body);
                break;

            case TraceExpr tx:
                // trace is pass-through at the value level for the untyped pass.
                EmitBlockAsExpression(tx.Body);
                break;

            default:
                _w.Write($"/* TODO: {expr.GetType().Name} */ default!");
                break;
        }
    }

    private void EmitCall(CallExpr c)
    {
        // Special-case generic stdlib constructors that C# can't infer without args
        // but whose type is decidable from the expected context: `List.empty()` →
        // `List.empty<T>()`, `None()` → `None<T>()`.
        if (TryEmitInferenceHelper(c)) return;

        // Method-call syntax: `s.method(args)` where the typer resolved
        // the FieldAccess to an aliased instance extern. Route the call
        // as `alias.method(self: receiver, ...args)` so the underlying
        // C# binding still receives `self` as its first parameter, but
        // the call site reads naturally with the receiver leading.
        if (c.Callee is FieldAccessExpr methodFa
            && _types?.MethodCallResolutions.TryGetValue(methodFa.Span, out var methodCall) == true)
        {
            EmitMethodCall(c, methodFa, methodCall);
            return;
        }

        // For stdlib constructors like `Ok(x)` / `Err(e)` / `Some(x)`, the argument
        // expected type comes from the expected Result / Option type.
        var argExpectedTypes = ArgumentExpectedTypes(c);

        EmitExpression(c.Callee);
        _w.Write("(");
        for (var i = 0; i < c.Arguments.Length; i++)
        {
            if (i > 0) _w.Write(", ");
            if (c.Arguments[i].Name is { } name)
            {
                _w.Write(EscapeId(name));
                _w.Write(": ");
            }
            var expected = i < argExpectedTypes.Length ? argExpectedTypes[i] : null;
            WithExpected(expected, () => EmitExpression(c.Arguments[i].Value));
        }
        _w.Write(")");
    }

    /// <summary>
    /// Emit a call site that the typer resolved as method-call syntax —
    /// `receiver.method(args)`. Lowers to
    /// <c>alias.method(receiverParamName: receiver, args...)</c>. Two
    /// resolution sources flow through here:
    /// <list type="bullet">
    ///   <item>Aliased instance externs: <c>alias</c> is the user's
    ///     `as alias` choice, receiver param name is <c>self</c>.</item>
    ///   <item>Stdlib namespace fns: <c>alias</c> is the namespace
    ///     name (<c>String</c>, <c>List</c>); receiver param name is
    ///     the underlying fn's first param name. The <c>String</c>
    ///     case picks up the existing
    ///     <c>global::Overt.Runtime.String</c> qualifier rewrite so
    ///     it doesn't collide with <c>System.String</c>.</item>
    /// </list>
    /// </summary>
    private void EmitMethodCall(CallExpr c, FieldAccessExpr fa, MethodCallResolution res)
    {
        _w.Write(QualifyMethodCallAlias(res.Alias));
        _w.Write(".");
        _w.Write(EscapeId(fa.FieldName));
        _w.Write("(");
        _w.Write(EscapeId(res.ReceiverParamName));
        _w.Write(": ");
        EmitExpression(fa.Target);
        foreach (var arg in c.Arguments)
        {
            _w.Write(", ");
            if (arg.Name is { } name)
            {
                _w.Write(EscapeId(name));
                _w.Write(": ");
            }
            EmitExpression(arg.Value);
        }
        _w.Write(")");
    }

    /// <summary>
    /// Map an Overt-side alias to its C# class spelling. <c>String</c>
    /// resolves to <c>global::Overt.Runtime.String</c> to avoid colliding
    /// with the BCL <c>System.String</c> brought in by <c>using System;</c>.
    /// Other aliases (user-chosen aliases for FFI, plus other stdlib
    /// namespaces like <c>List</c>) emit as-is — the C# code uses them
    /// directly.
    /// </summary>
    private static string QualifyMethodCallAlias(string alias) => alias switch
    {
        "String" => "global::Overt.Runtime.String",
        _ => alias,
    };

    /// <summary>
    /// Emit one of the small number of stdlib no-arg generic factories whose type
    /// parameter comes purely from the expected-type context (not from any argument).
    /// Returns true if emitted, false to fall through to the default call path.
    /// </summary>
    private bool TryEmitInferenceHelper(CallExpr c)
    {
        if (_expectedType is null) return false;

        // `List.empty()` — callee shape is FieldAccess(Ident("List"), "empty").
        if (c.Arguments.Length == 0
            && c.Callee is FieldAccessExpr { FieldName: "empty" } faEmpty
            && faEmpty.Target is IdentifierExpr { Name: "List" }
            && _expectedType is NamedTypeRef { Name: "List", TypeArguments: { Length: 1 } emptyArgs })
        {
            _w.Write($"List.empty<{CSharpTypeDisplay(emptyArgs[0])}>()");
            return true;
        }

        // `None()` — bare identifier, used as a call. Expected is `Option<T>`.
        if (c.Arguments.Length == 0
            && c.Callee is IdentifierExpr { Name: "None" }
            && _expectedType is NamedTypeRef { Name: "Option", TypeArguments: { Length: 1 } noneArgs })
        {
            _w.Write($"None<{CSharpTypeDisplay(noneArgs[0])}>()");
            return true;
        }

        // `Ok(x)` / `Err(e)` / `Some(x)` with a known expected Result / Option type.
        // Emit with an explicit type parameter and cast the argument so the marker
        // carries the exact inner type the caller wants — this both pins the generic
        // inference and triggers any implicit conversions declared on the target type
        // (e.g. the List<T> → NonEmpty<T> lift on wrapper records). The inner type
        // is also threaded into the argument as its expected type so nested helpers
        // like `List.empty()` still receive a concrete type parameter.
        if (c.Arguments.Length == 1
            && c.Callee is IdentifierExpr id
            && _expectedType is NamedTypeRef nt)
        {
            var (ctor, argIndex) = (id.Name, nt.Name) switch
            {
                ("Ok", "Result") => ("Ok", 0),
                ("Err", "Result") => ("Err", 1),
                ("Some", "Option") => ("Some", 0),
                _ => (null, -1),
            };
            if (ctor is not null && argIndex < nt.TypeArguments.Length)
            {
                var innerType = nt.TypeArguments[argIndex];
                var innerCs = CSharpTypeDisplay(innerType);
                _w.Write($"{ctor}<{innerCs}>(({innerCs})");
                WithExpected(innerType, () => EmitExpression(c.Arguments[0].Value));
                _w.Write(")");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compute the expected types for a call's arguments based on the callee. Only
    /// stdlib constructors (Ok / Err / Some) get this treatment today; a richer
    /// lookup using the callee's resolved FunctionTypeRef would generalize this.
    /// </summary>
    private ImmutableArray<TypeRef?> ArgumentExpectedTypes(CallExpr c)
    {
        // For `Ok(x)` / `Err(e)` / `Some(x)` whose expected type is the outer Result /
        // Option, fill in the specific T from the expected's type arguments.
        if (c.Callee is IdentifierExpr id
            && _expectedType is NamedTypeRef nt)
        {
            (string callee, string nameMatch, int argIndex)[] mappings =
            {
                ("Ok", "Result", 0),
                ("Err", "Result", 1),
                ("Some", "Option", 0),
            };
            foreach (var (callee, name, argIndex) in mappings)
            {
                if (id.Name == callee && nt.Name == name
                    && c.Arguments.Length == 1 && argIndex < nt.TypeArguments.Length)
                {
                    return ImmutableArray.Create<TypeRef?>(nt.TypeArguments[argIndex]);
                }
            }
        }

        // Default: no expected types — let child expressions propagate naturally.
        return c.Arguments.Select(_ => (TypeRef?)null).ToImmutableArray();
    }

    private void EmitBinary(BinaryExpr be)
    {
        // If this is a `|>?` we've already hoisted, substitute the hoisted Ok-local.
        // The Err path has early-returned above this point; the value of the pipe
        // here is the unwrapped T.
        if (be.Op == BinaryOp.PipePropagate
            && _hoistMap.TryGetValue(be.Span, out var hoistedPipe))
        {
            _w.Write(hoistedPipe);
            return;
        }

        // Pipes rewrite to call-arg splicing: `x |> f(a, b)` → `f(x, a, b)`.
        if (be.Op is BinaryOp.PipeCompose or BinaryOp.PipePropagate)
        {
            EmitPipe(be);
            return;
        }

        _w.Write("(");
        EmitExpression(be.Left);
        _w.Write(" ");
        _w.Write(BinaryOpToCSharp(be.Op));
        _w.Write(" ");
        EmitExpression(be.Right);
        _w.Write(")");
    }

    private void EmitPipe(BinaryExpr be)
    {
        // Build the "call with x spliced as first arg" form. If the RHS is a call,
        // splice; otherwise treat RHS as unary.
        if (be.Right is CallExpr call)
        {
            _w.Write("(");
            EmitExpression(call.Callee);
            _w.Write("(");
            EmitExpression(be.Left);
            foreach (var arg in call.Arguments)
            {
                _w.Write(", ");
                if (arg.Name is { } name)
                {
                    _w.Write(name);
                    _w.Write(": ");
                }
                EmitExpression(arg.Value);
            }
            _w.Write("))");
        }
        else
        {
            _w.Write("(");
            EmitExpression(be.Right);
            _w.Write("(");
            EmitExpression(be.Left);
            _w.Write("))");
        }
        if (be.Op == BinaryOp.PipePropagate)
        {
            _w.Write(".Unwrap()");
        }
    }

    private static string BinaryOpToCSharp(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Modulo => "%",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.Less => "<",
        BinaryOp.LessEqual => "<=",
        BinaryOp.Greater => ">",
        BinaryOp.GreaterEqual => ">=",
        BinaryOp.LogicalAnd => "&&",
        BinaryOp.LogicalOr => "||",
        _ => "?",
    };

    private void EmitInterpolatedString(InterpolatedStringExpr isx)
    {
        // C# interpolated string: $"...{expr}..."
        _w.Write("$\"");
        for (var i = 0; i < isx.Parts.Length; i++)
        {
            var part = isx.Parts[i];
            switch (part)
            {
                case StringLiteralPart lp:
                    _w.Write(StripQuotesForInterp(lp.Text, isFirst: i == 0, isLast: i == isx.Parts.Length - 1));
                    break;
                case StringInterpolationPart ip:
                    // Wrap the inner expression in parens. C# treats `,`
                    // (alignment) and `:` (format-spec) as terminators
                    // inside an interpolation hole, so any expression
                    // containing them at top level — named call args
                    // (`fn(name = arg)` lowers to `fn(name: arg)`),
                    // ternaries, etc. — gets misparsed without the parens.
                    _w.Write("{(");
                    EmitExpression(ip.Expression);
                    _w.Write(")}");
                    break;
            }
        }
        _w.Write("\"");
    }

    // Head/tail literal parts carry their surrounding quote(s) from the lexer's segmenting;
    // the middle parts don't. Strip them when emitting so the whole thing reads as a single
    // C# interpolated string.
    private static string StripQuotesForInterp(string raw, bool isFirst, bool isLast)
    {
        var s = raw;
        if (isFirst && s.StartsWith('"')) s = s[1..];
        if (isLast && s.EndsWith('"')) s = s[..^1];
        return s;
    }

    private void EmitIf(IfExpr ie)
    {
        // C# has a ternary operator; we lower Overt's if-expression to a ternary when
        // both arms are expressions. If the else is absent, the else-value is Unit.Value.
        // The ternary arms inherit the expected type of the enclosing expression.
        var expected = _expectedType;
        _w.Write("((");
        EmitExpression(ie.Condition);
        _w.Write(") ? ");
        WithExpected(expected, () => EmitBlockAsExpression(ie.Then));
        _w.Write(" : ");
        if (ie.Else is { } elseBlock)
        {
            WithExpected(expected, () => EmitBlockAsExpression(elseBlock));
        }
        else
        {
            _w.Write("Unit.Value");
        }
        _w.Write(")");
    }

    private void EmitMatch(MatchExpr me)
    {
        var scrutineeType = TypeOf(me.Scrutinee);
        var expected = _expectedType;
        _w.Write("(");
        EmitExpression(me.Scrutinee);
        _w.WriteLine(" switch");
        _w.WriteLine("{");
        using (_w.Indent())
        {
            var hasWildcard = false;
            foreach (var arm in me.Arms)
            {
                if (arm.Pattern is WildcardPattern)
                {
                    hasWildcard = true;
                }
                EmitPatternForMatch(arm.Pattern, scrutineeType);
                _w.Write(" => ");
                WithExpected(expected, () => EmitExpression(arm.Body));
                _w.WriteLine(",");
            }
            // Synthetic discard arm. Overt's semantic pass has already
            // proved this match exhaustive; the arm exists so Roslyn's
            // own exhaustiveness check (CS8509) stays silent for
            // consumers that build with TreatWarningsAsErrors. The
            // exception should never fire at runtime.
            if (!hasWildcard)
            {
                _w.WriteLine("_ => throw new global::System.InvalidOperationException(\"Overt match: unreachable arm\"),");
            }
        }
        _w.Write("})");
    }

    private void EmitPatternForMatch(Pattern p, TypeRef scrutineeType)
    {
        switch (p)
        {
            case WildcardPattern: _w.Write("_"); break;
            case IdentifierPattern id:
                // A single identifier in pattern position is a bound variable UNLESS it
                // names a known zero-arg variant on the scrutinee's type (like `None` on
                // Option, or an enum's bare variant). In that case it's a reference.
                var variant = ResolveStdlibVariant(id.Name, scrutineeType);
                if (variant is not null)
                {
                    _w.Write($"{variant} _");
                }
                else
                {
                    _w.Write($"var {EscapeId(id.Name)}");
                }
                break;
            case PathPattern pp:
                // `A.B` with no args — flat sealed-record name (A_B).
                _w.Write(JoinPath(pp.Path));
                _w.Write(" _");
                break;
            case ConstructorPattern cp:
                // Unqualified stdlib constructors (Ok / Err / Some) resolve via the
                // scrutinee's type. Qualified paths (`A.B(x)`) emit as `A_B(...)`.
                var ctorName = ResolveStdlibVariant(cp.Path, scrutineeType) ?? JoinPath(cp.Path);
                _w.Write(ctorName);
                if (cp.Arguments.Length > 0)
                {
                    _w.Write("(");
                    for (var i = 0; i < cp.Arguments.Length; i++)
                    {
                        if (i > 0) _w.Write(", ");
                        // Inside a constructor, subpatterns lose the scrutinee context —
                        // we don't track variant-argument types yet.
                        EmitPatternForMatch(cp.Arguments[i], UnknownType.Instance);
                    }
                    _w.Write(")");
                }
                break;
            case RecordPattern rp:
                _w.Write(JoinPath(rp.Path));
                _w.Write(" { ");
                for (var i = 0; i < rp.Fields.Length; i++)
                {
                    if (i > 0) _w.Write(", ");
                    _w.Write(EscapeId(rp.Fields[i].Name));
                    _w.Write(": ");
                    EmitPatternForMatch(rp.Fields[i].Subpattern, UnknownType.Instance);
                }
                _w.Write(" }");
                break;
            case TuplePattern tp:
                _w.Write("(");
                for (var i = 0; i < tp.Elements.Length; i++)
                {
                    if (i > 0) _w.Write(", ");
                    var elemType = scrutineeType is TupleTypeRef tt && i < tt.Elements.Length
                        ? tt.Elements[i]
                        : UnknownType.Instance;
                    EmitPatternForMatch(tp.Elements[i], elemType);
                }
                _w.Write(")");
                break;
            case LiteralPattern lp:
                // C# switch-pattern matching accepts literal constants directly
                // as patterns (since C# 7.0). Emit the underlying expression —
                // EmitExpression handles int / float / bool / string / negated
                // literals with the right syntax.
                EmitExpression(lp.Value);
                break;
        }
    }

    /// <summary>
    /// Map an unqualified stdlib variant name (<c>Ok</c>, <c>Err</c>, <c>Some</c>,
    /// <c>None</c>) to the fully-typed C# record type for pattern matching, using the
    /// scrutinee's type to fill in generic arguments. Returns null when the name isn't
    /// a stdlib variant or when scrutinee type info isn't rich enough.
    /// </summary>
    private static string? ResolveStdlibVariant(string name, TypeRef scrutineeType)
    {
        if (scrutineeType is not NamedTypeRef nt) return null;
        return (nt.Name, name) switch
        {
            ("Result", "Ok") when nt.TypeArguments.Length == 2 =>
                $"ResultOk<{CSharpTypeDisplay(nt.TypeArguments[0])}, {CSharpTypeDisplay(nt.TypeArguments[1])}>",
            ("Result", "Err") when nt.TypeArguments.Length == 2 =>
                $"ResultErr<{CSharpTypeDisplay(nt.TypeArguments[0])}, {CSharpTypeDisplay(nt.TypeArguments[1])}>",
            ("Option", "Some") when nt.TypeArguments.Length == 1 =>
                $"OptionSome<{CSharpTypeDisplay(nt.TypeArguments[0])}>",
            ("Option", "None") when nt.TypeArguments.Length == 1 =>
                $"OptionNone<{CSharpTypeDisplay(nt.TypeArguments[0])}>",
            _ => null,
        };
    }

    private static string? ResolveStdlibVariant(ImmutableArray<string> path, TypeRef scrutineeType)
        => path.Length == 1 ? ResolveStdlibVariant(path[0], scrutineeType) : null;

    /// <summary>
    /// Join a dotted path for an enum variant reference. Multi-segment paths collapse
    /// to <c>First_Second</c> (the emitted sealed-record name); single-segment paths
    /// pass through so unqualified variants like <c>Ok</c> / <c>Some</c> resolve against
    /// whatever type context (Result / Option) is in scope.
    /// </summary>
    private static string JoinPath(System.Collections.Immutable.ImmutableArray<string> path)
        => path.Length == 1 ? path[0] : string.Join("_", path);

    private void EmitBlockAsStatement(BlockExpr block)
    {
        _w.WriteLine("{");
        using (_w.Indent())
        {
            foreach (var stmt in block.Statements) EmitStatement(stmt);
            if (block.TrailingExpression is { } tail)
            {
                if (tail is ReturnExpr rx)
                {
                    // Trailing-expr-as-statement that's a return: emit a
                    // real `return X;`, not `return X;` wrapped in an
                    // expression-statement (which would re-trigger the
                    // ReturnExpr throw in EmitExpression).
                    EmitHoistsForExpression(rx.Value);
                    _w.Write("return ");
                    EmitExpression(rx.Value);
                    _w.WriteLine(";");
                }
                else
                {
                    EmitExpression(tail);
                    _w.WriteLine(";");
                }
            }
        }
        _w.WriteLine("}");
    }

    /// <summary>
    /// Emit a block as an expression. Pure trailing-expression blocks inline
    /// directly; blocks with statements become an immediately-invoked lambda
    /// typed to the current <see cref="_expectedType"/>. A trailing <c>?</c>/<c>|&gt;?</c>
    /// on a single-expression block is NOT hoisted — the IIFE's return type is
    /// the consumer's expected type, which may not be a <c>Result</c>, so an
    /// early-return <c>Err(...)</c> would either fail to compile or silently
    /// miscompile via implicit boxing to <c>object?</c>. Such sites stay on
    /// <c>.Unwrap()</c> and are covered by the conditional-context note in
    /// CARRYOVER — a proper fix statement-level-restructures the entire `let x
    /// = if cond { foo()? } else { ... }` into C# if/else with an assignment.
    /// </summary>
    private void EmitBlockAsExpression(BlockExpr block)
    {
        if (block.Statements.Length == 0 && block.TrailingExpression is { } only)
        {
            EmitExpression(only);
            return;
        }

        var expected = _expectedType;
        var resultCSharp = expected is not null and not UnknownType
            ? CSharpTypeDisplay(expected)
            : "object?";
        _w.Write($"((Func<{resultCSharp}>)(() => {{ ");
        foreach (var stmt in block.Statements)
        {
            EmitStatement(stmt);
        }
        if (block.TrailingExpression is { } tail)
        {
            // Hoist `?` in the trailing position only when the IIFE's return type
            // is a Result<_, E>. Otherwise the hoist's `return Err<E>(...)` would
            // either fail to compile (if return is a concrete non-Result type) or
            // silently box as object? (if expected is unknown), corrupting the
            // value flowing out. Non-Result trailing `?` keeps its `.Unwrap()`
            // fallback at the site.
            if (expected is NamedTypeRef { Name: "Result" })
            {
                EmitHoistsForExpression(tail);
            }
            _w.Write("return ");
            WithExpected(expected, () => EmitExpression(tail));
            _w.Write(";");
        }
        else
        {
            _w.Write("return Unit.Value;");
        }
        _w.Write(" }))()");
    }

    private void EmitRecordLiteral(RecordLiteralExpr rl)
    {
        // A dotted-path record literal (e.g. `Tree.Node { ... }`) refers to an enum
        // variant, emitted as a flat sealed-record type named `Enum_Variant`. We cast
        // to the enum base type so `Ok(variant)` / `Err(variant)` target-type correctly
        // at the call site — C# will otherwise infer the most-specific type and fail to
        // find an implicit conversion for the marker.
        if (rl.TypeTarget is FieldAccessExpr fa
            && fa.Target is IdentifierExpr enumName
            && IsPascalCase(enumName.Name)
            && IsPascalCase(fa.FieldName))
        {
            var variantFields = LookupVariantFields(enumName.Name, fa.FieldName);
            _w.Write($"(({enumName.Name})new {enumName.Name}_{fa.FieldName}(");
            EmitFieldInits(rl.Fields, variantFields);
            _w.Write("))");
            return;
        }

        var recordFields = rl.TypeTarget is IdentifierExpr recordName
            ? LookupRecordFields(recordName.Name)
            : null;
        _w.Write("new ");
        EmitExpression(rl.TypeTarget);
        _w.Write("(");
        EmitFieldInits(rl.Fields, recordFields);
        _w.Write(")");
    }

    private void EmitFieldInits(
        ImmutableArray<FieldInit> fields,
        ImmutableArray<RecordField>? declared)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) _w.Write(", ");
            _w.Write(EscapeId(fields[i].Name));
            _w.Write(": ");
            // When we know the field's declared type (record or enum-variant
            // construction in this module), thread it as the expected type so
            // generic-inference helpers (List.empty, None) emit with explicit
            // type arguments.
            var expected = declared is { } decls
                ? FindFieldType(decls, fields[i].Name)
                : null;
            if (expected is not null)
            {
                WithExpected(expected, () => EmitExpression(fields[i].Value));
            }
            else
            {
                EmitExpression(fields[i].Value);
            }
        }
    }

    private ImmutableArray<RecordField>? LookupRecordFields(string name)
    {
        if (_types is null) return null;
        foreach (var decl in _types.Module.Declarations)
        {
            if (decl is RecordDecl r && r.Name == name)
            {
                return r.Fields;
            }
        }
        return null;
    }

    private ImmutableArray<RecordField>? LookupVariantFields(string enumName, string variantName)
    {
        if (_types is null) return null;
        foreach (var decl in _types.Module.Declarations)
        {
            if (decl is EnumDecl e && e.Name == enumName)
            {
                foreach (var v in e.Variants)
                {
                    if (v.Name == variantName)
                    {
                        return v.Fields;
                    }
                }
            }
        }
        return null;
    }

    private static TypeRef? FindFieldType(ImmutableArray<RecordField> fields, string name)
    {
        foreach (var f in fields)
        {
            if (f.Name == name)
            {
                return LowerType(f.Type);
            }
        }
        return null;
    }

    // ------------------------------------------------------------ helpers

    private void EmitAnnotationComments(ImmutableArray<Annotation> annotations)
    {
        foreach (var a in annotations)
        {
            var args = a.Arguments.Length > 0 ? $"({string.Join(", ", a.Arguments)})" : "";
            _w.WriteLine($"// @{a.Name}{args}");
        }
    }

    private void EmitEffectRowComment(EffectRow? row)
    {
        if (row is null || row.Effects.Length == 0) return;
        _w.WriteLine($"// !{{{string.Join(", ", row.Effects)}}}");
    }

    /// <summary>
    /// Emit <c>@csharp("...")</c> attributes as raw C# <c>[...]</c> attributes on
    /// a separate line each, immediately before the member. The string content is
    /// passed through opaquely: Overt performs no semantic check; the target
    /// platform's attribute grammar is the user's responsibility.
    /// </summary>
    private void EmitCSharpAttributes(ImmutableArray<Annotation> annotations)
    {
        if (annotations.IsDefaultOrEmpty)
        {
            return;
        }
        foreach (var ann in annotations)
        {
            if (ann.Name != "csharp" || ann.StringArgument is null)
            {
                continue;
            }
            _w.WriteLine($"[{ann.StringArgument}]");
        }
    }

    /// <summary>
    /// Emit <c>@doc("...")</c> annotations as a C# XML documentation comment
    /// (<c>/// &lt;summary&gt;...&lt;/summary&gt;</c>) immediately before the
    /// member. <c>@doc</c> is native (cross-target portable) rather than
    /// passthrough; this helper owns the C# lowering. Multiple <c>@doc</c>
    /// annotations on the same declaration concatenate into a single summary
    /// separated by whitespace.
    /// </summary>
    private void EmitDocComment(ImmutableArray<Annotation> annotations)
    {
        if (annotations.IsDefaultOrEmpty)
        {
            return;
        }
        var first = true;
        foreach (var ann in annotations)
        {
            if (ann.Name != "doc" || ann.StringArgument is null)
            {
                continue;
            }
            var text = EscapeXmlText(ann.StringArgument);
            if (first)
            {
                _w.WriteLine("/// <summary>");
                first = false;
            }
            foreach (var line in text.Split('\n'))
            {
                _w.WriteLine($"/// {line.TrimEnd('\r')}");
            }
        }
        if (!first)
        {
            _w.WriteLine("/// </summary>");
        }
    }

    /// <summary>
    /// Inline form of @csharp emission for a positional record parameter.
    /// Writes <c>[property: X] </c> tokens into the parameter list so the
    /// attribute attaches to the synthesized property, not the constructor
    /// parameter. <c>@doc</c> on record fields is rejected upstream because
    /// the inline form has no clean spot for an XML doc comment; if a future
    /// version needs it, the record-emission path can switch to multi-line
    /// when any field carries <c>@doc</c>.
    /// </summary>
    private void EmitInlineCSharpAttributesForRecordField(ImmutableArray<Annotation> annotations)
    {
        if (annotations.IsDefaultOrEmpty)
        {
            return;
        }
        foreach (var ann in annotations)
        {
            if (ann.Name != "csharp" || ann.StringArgument is null)
            {
                continue;
            }
            _w.Write($"[property: {ann.StringArgument}] ");
        }
    }

    private static string EscapeXmlText(string s)
        => s.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string PascalCase(string snake)
    {
        if (string.IsNullOrEmpty(snake)) return snake;
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>Render a dotted Overt module path as a C# dotted namespace —
    /// <c>["stdlib", "http"]</c> becomes <c>Stdlib.Http</c>.</summary>
    private static string ModulePathToCSharp(ImmutableArray<string> path)
        => string.Join(".", path.Select(PascalCase));

    /// <summary>Render an Overt module name (dot-joined) as a C# dotted
    /// namespace. Strings with no dots behave like <see cref="PascalCase"/>.</summary>
    private static string ModuleNameToCSharp(string name)
        => string.Join(".", name.Split('.').Select(PascalCase));

    /// <summary>
    /// Resolve an Overt module name to the C# namespace it emits into.
    /// A dotted name (e.g. <c>ParksComputing.SemVer</c>) is treated as a
    /// fully-qualified namespace the author has chosen and emits verbatim —
    /// the generated <c>namespace</c> declaration matches the module name
    /// exactly. A single-identifier name (e.g. <c>greeter</c>) is treated
    /// as a short name needing scoping; it emits under the <c>Overt.Generated.</c>
    /// prefix so example code, scripts, and tests don't collide with anything
    /// at the top of the namespace tree.
    /// </summary>
    private static string ToEmittedNamespace(string moduleName)
        => moduleName.Contains('.', StringComparison.Ordinal)
            ? ModuleNameToCSharp(moduleName)
            : $"Overt.Generated.{ModuleNameToCSharp(moduleName)}";

    /// <summary>
    /// Resolve an imported Overt module path (a parsed <c>use</c> path) to the
    /// C# namespace the import targets. Same heuristic as
    /// <see cref="ToEmittedNamespace"/>: multi-segment paths are
    /// fully-qualified and emit verbatim; single-segment paths get the
    /// <c>Overt.Generated.</c> prefix.
    /// </summary>
    private static string ToEmittedNamespace(ImmutableArray<string> modulePath)
        => modulePath.Length > 1
            ? ModulePathToCSharp(modulePath)
            : $"Overt.Generated.{ModulePathToCSharp(modulePath)}";

    private static bool IsLikelyEnumVariantRef(FieldAccessExpr fa)
        => fa.Target is IdentifierExpr id
           && IsPascalCase(id.Name)
           && IsPascalCase(fa.FieldName);

    private static bool IsPascalCase(string s)
        => s.Length > 0 && char.IsUpper(s[0]) && !s.Contains('_');

    private void EmitParallelPlaceholder(ParallelExpr pe)
    {
        var (tupleCs, errorCs) = DeriveTaskGroupResultShape(pe.Tasks);
        _w.Write($"/* TODO: parallel */ default(Result<{tupleCs}, {errorCs}>)!");
    }

    private void EmitRacePlaceholder(RaceExpr re)
    {
        // race's true return shape per DESIGN.md §12 is Result<T, RaceAllFailed<E>>,
        // but the examples expect it to flow into a plain Result<T, E> context, which
        // implies a built-in coercion the runtime hasn't modeled. For the
        // untyped-compile-check pass, honor the expected context when we have one and
        // fall back to the first task's type. The type checker will formalize race's
        // real shape once error-chaining lands.
        if (_expectedType is not null and not UnknownType)
        {
            _w.Write($"/* TODO: race */ default({CSharpTypeDisplay(_expectedType)})!");
            return;
        }
        if (re.Tasks.Length > 0)
        {
            _w.Write($"/* TODO: race */ default({CSharpTypeDisplay(TypeOf(re.Tasks[0]))})!");
            return;
        }
        _w.Write("/* TODO: race */ default(object?)!");
    }

    /// <summary>
    /// Derive the Result&lt;tuple, E&gt; shape for a task-group block. Each task is
    /// expected to be a Result; unpack its Ok-type to populate the tuple, and take the
    /// error type from the first Result-typed task. Unknowns fall back to object.
    /// </summary>
    private (string TupleCs, string ErrorCs) DeriveTaskGroupResultShape(
        ImmutableArray<Expression> tasks)
    {
        var okCSharp = new List<string>();
        string errorCs = "object";
        var sawError = false;
        foreach (var task in tasks)
        {
            var taskType = TypeOf(task);
            if (taskType is NamedTypeRef { Name: "Result", TypeArguments: { Length: 2 } args })
            {
                okCSharp.Add(CSharpTypeDisplay(args[0]));
                if (!sawError)
                {
                    errorCs = CSharpTypeDisplay(args[1]);
                    sawError = true;
                }
            }
            else
            {
                okCSharp.Add(CSharpTypeDisplay(taskType));
            }
        }
        var tupleCs = okCSharp.Count switch
        {
            0 => "()",
            1 => okCSharp[0],
            _ => $"({string.Join(", ", okCSharp)})",
        };
        return (tupleCs, errorCs);
    }

    /// <summary>
    /// Render a <see cref="TypeRef"/> as C# source text. Parallels the compiler's
    /// TypeRef.Display but uses the C# spelling (lowercase primitives, nullable
    /// object fallback for unknowns).
    /// </summary>
    private static string CSharpTypeDisplay(TypeRef t) => t switch
    {
        PrimitiveType { Name: "Int" } => "int",
        PrimitiveType { Name: "Int64" } => "long",
        PrimitiveType { Name: "Float" } => "double",
        PrimitiveType { Name: "Bool" } => "bool",
        PrimitiveType { Name: "String" } => "string",
        PrimitiveType { Name: "Unit" } => "Unit",
        PrimitiveType p => p.Name,
        NamedTypeRef n when n.TypeArguments.Length == 0 => MapTypeName(n.Name),
        NamedTypeRef n =>
            $"{MapTypeName(n.Name)}<{string.Join(", ", n.TypeArguments.Select(CSharpTypeDisplay))}>",
        TupleTypeRef tt =>
            $"({string.Join(", ", tt.Elements.Select(CSharpTypeDisplay))})",
        TypeVarRef tv => tv.Name,
        FunctionTypeRef ft =>
            ft.Return is PrimitiveType { Name: "Unit" }
                ? (ft.Parameters.Length == 0
                    ? "Action"
                    : $"Action<{string.Join(", ", ft.Parameters.Select(CSharpTypeDisplay))}>")
                : $"Func<{string.Join(", ", ft.Parameters.Select(CSharpTypeDisplay))}, "
                    + $"{CSharpTypeDisplay(ft.Return)}>",
        _ => "object?",
    };

    /// <summary>
    /// C# reserved and contextual keywords. Overt identifiers that happen to spell these
    /// must be prefixed with <c>@</c> in emitted code (e.g. <c>event</c> → <c>@event</c>).
    /// </summary>
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate",
        "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
        "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>
    /// Escape an Overt identifier for use in emitted C#. Adds the <c>@</c> prefix when
    /// the name would otherwise collide with a C# keyword. Called on every identifier
    /// slot that the emitter writes — declarations, references, parameters, fields.
    /// Variant paths like <c>Tree.Node</c> stay raw because they match the emitted
    /// variant record names directly.
    /// </summary>
    private static string EscapeId(string name) =>
        CSharpKeywords.Contains(name) ? "@" + name : name;
}
