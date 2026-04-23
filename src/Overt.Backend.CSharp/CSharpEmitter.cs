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

    /// <summary>Unique-name counter for hoisted <c>?</c> temporaries.</summary>
    private int _propagateCounter;

    /// <summary>
    /// Enclosing function's declared return type, set by <see cref="EmitFunction"/>.
    /// Used by the <c>?</c>-hoisting pass to derive the error type to propagate when
    /// the operand's Result shape cannot be recovered from type annotations alone
    /// (e.g., <c>|&gt;?</c> where generic inference across the pipe isn't wired up).
    /// </summary>
    private TypeRef? _currentFnReturn;

    private CSharpEmitter(IndentedWriter w, TypeCheckResult? types, string? sourcePath)
    {
        _w = w;
        _types = types;
        _sourcePath = sourcePath;
    }

    public static string Emit(
        ModuleDecl module,
        TypeCheckResult? types = null,
        string? sourcePath = null)
    {
        var sb = new StringBuilder();
        var emitter = new CSharpEmitter(new IndentedWriter(sb), types, sourcePath);
        emitter.EmitModule(module);
        return sb.ToString();
    }

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
        _w.WriteLine();
        _w.WriteLine("using System;");
        _w.WriteLine("using System.Threading.Tasks;");
        _w.WriteLine("using Overt.Runtime;");
        _w.WriteLine("using static Overt.Runtime.Prelude;");
        _w.WriteLine();
        _w.WriteLine($"namespace Overt.Generated.{PascalCase(module.Name)};");
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

        foreach (var decl in module.Declarations.Where(d => d is not TypeAliasDecl))
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
        _w.Write("public sealed record ");
        _w.Write(rec.Name);
        _w.Write("(");
        for (var i = 0; i < rec.Fields.Length; i++)
        {
            if (i > 0) _w.Write(", ");
            EmitType(rec.Fields[i].Type);
            _w.Write(" ");
            _w.Write(EscapeId(rec.Fields[i].Name));
        }
        _w.WriteLine(");");
    }

    private void EmitEnum(EnumDecl e)
    {
        EmitAnnotationComments(e.Annotations);
        _w.Write($"public abstract record {e.Name}");
        _w.WriteLine(";");
        foreach (var variant in e.Variants)
        {
            _w.Write("public sealed record ");
            _w.Write($"{e.Name}_{variant.Name}");
            if (variant.Fields.Length > 0)
            {
                _w.Write("(");
                for (var i = 0; i < variant.Fields.Length; i++)
                {
                    if (i > 0) _w.Write(", ");
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

    private void EmitTypeAlias(TypeAliasDecl t)
    {
        // C# using-aliases can't be generic and can't carry predicates. Generic or
        // refinement aliases lower to wrapper records instead; non-generic plain aliases
        // stay as using-directives so primitive aliases don't pay for a wrapping type.
        if (t.Predicate is not null)
        {
            _w.Write($"// TODO: refinement `{t.Name}`: where-predicate dropped");
            _w.WriteLine();
        }

        if (t.TypeParameters.Length > 0)
        {
            var typeParams = string.Join(", ", t.TypeParameters);
            var innerType = CSharpTypeDisplay(LowerType(t.Target));

            _w.WriteLine($"public sealed record {t.Name}<{typeParams}>({innerType} Inner)");
            _w.WriteLine("{");
            using (_w.Indent())
            {
                // Implicit conversion from the inner type into the wrapper so refinement
                // aliases accept a bare value wherever they're required. The predicate
                // would be enforced here once the type checker emits runtime assertions.
                _w.WriteLine(
                    $"public static implicit operator {t.Name}<{typeParams}>({innerType} inner) => new(inner);");
            }
            _w.WriteLine("}");
            return;
        }

        _w.Write($"using {t.Name} = ");
        EmitType(t.Target);
        _w.WriteLine(";");
    }

    private void EmitFunction(FunctionDecl fn)
    {
        EmitEffectRowComment(fn.Effects);
        EmitLineDirective(fn.Span);
        _w.Write("public static ");
        if (fn.ReturnType is { } rt)
        {
            EmitType(rt);
        }
        else
        {
            _w.Write("Unit");
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
        _w.WriteLine($"{unsafePrefix}extern \"{x.Platform}\" binds \"{x.BindsTarget}\""
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
    /// Supported today:
    /// <list type="bullet">
    ///   <item>Platform <c>"csharp"</c> — binds target is a dotted C# path:
    ///     <c>System.IO.File.ReadAllText</c>. Emits a call to that static
    ///     member with the extern's parameters passed positionally.</item>
    ///   <item>A static property is recognized as "binds target resolves to a
    ///     property at compile time"; emitted as a bare member access.</item>
    ///   <item>Return type <c>Result&lt;T, E&gt;</c> wraps the call in a
    ///     try/catch that converts thrown exceptions into an <c>Err</c> of
    ///     the declared error type. A few error types have known
    ///     constructors; unknown error types fall through to rethrow, with
    ///     a diagnostic in the generated code.</item>
    /// </list>
    ///
    /// Not yet supported:
    /// <list type="bullet">
    ///   <item>Instance methods (the <c>Namespace.Type::Method</c> convention
    ///     with a <c>self</c> parameter).</item>
    ///   <item>Constructors (<c>..ctor</c> binding target).</item>
    ///   <item>Platform <c>"c"</c> — would become <c>DllImport</c>.</item>
    ///   <item>Platform <c>"go"</c> — no Go backend emission yet.</item>
    /// </list>
    /// </summary>
    private void EmitExternBody(ExternDecl x)
    {
        if (x.Platform != "csharp")
        {
            _w.WriteLine(
                "throw new NotImplementedException("
                + $"\"extern platform '{x.Platform}' not yet wired up\");");
            return;
        }

        var returnsResult = x.ReturnType is NamedType
            { Name: "Result", TypeArguments.Length: 2 };

        // Build the C# call expression: <binds target>(arg1, arg2, ...).
        // Static properties don't take args, so bare member access.
        var args = string.Join(", ",
            x.Parameters.Select(p => EscapeId(p.Name)));
        var callExpr = x.Parameters.Length == 0 && BindsLooksLikeProperty(x.BindsTarget)
            ? x.BindsTarget
            : $"{x.BindsTarget}({args})";

        if (!returnsResult)
        {
            // Void or plain-typed return: pass through. Exceptions fly.
            if (x.ReturnType is UnitType || x.ReturnType is null)
            {
                _w.WriteLine($"{callExpr};");
                _w.WriteLine("return Unit.Value;");
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

    /// <summary>Heuristic: a binds target resolves to a property (not a method)
    /// when we'd otherwise emit a zero-arg call and C# has a property of that
    /// name. We can't check that at emit time without reflection; take the
    /// conservative route of always emitting <c>target()</c> for zero-param
    /// externs so static methods work, and let authors explicitly mark
    /// properties via an extern convention later.</summary>
    private static bool BindsLooksLikeProperty(string _) => false;

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
        "Float" => "double",
        "Bool" => "bool",
        "String" => "string",
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
                EmitHoistsForExpression(tail);
                _w.Write("return ");
                WithExpected(declaredReturn, () => EmitExpression(tail));
                _w.WriteLine(";");
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
                // When the initializer is directly an if / match containing `?`,
                // lower to statement-level C# if/else or switch with assignments
                // into a pre-declared temp. This lets the `?`-hoist's early-return
                // reach the enclosing function, not get trapped in an IIFE. See
                // TryEmitStmtLoweredLet for the eligible shapes.
                if (TryEmitStmtLoweredLet(ls)) break;

                EmitHoistsForExpression(ls.Initializer);
                _w.Write("var ");
                EmitPatternForBinding(ls.Target);
                _w.Write(" = ");
                EmitExpression(ls.Initializer);
                _w.WriteLine(";");
                break;

            case AssignmentStmt asn:
                if (TryEmitStmtLoweredAssign(asn)) break;
                EmitHoistsForExpression(asn.Value);
                _w.Write(EscapeId(asn.Name));
                _w.Write(" = ");
                EmitExpression(asn.Value);
                _w.WriteLine(";");
                break;

            case ExpressionStmt es:
                EmitExpressionAsStatement(es.Expression);
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
        _w.Write(CSharpTypeDisplay(targetType));
        _w.Write(" ");
        _w.Write(EscapeId(ip.Name));
        _w.WriteLine(";");
        AssignInto(EscapeId(ip.Name), ls.Initializer, targetType);
        return true;
    }

    private bool TryEmitStmtLoweredAssign(AssignmentStmt asn)
    {
        if (!NeedsStmtLowering(asn.Value)) return false;
        // We know the LHS name exists; assign into it directly.
        AssignInto(EscapeId(asn.Name), asn.Value, UnknownType.Instance);
        return true;
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
                || (ie.Else is { } elseBlock && ContainsPropagate(elseBlock));
        }
        if (e is MatchExpr me)
        {
            return me.Arms.Any(arm => ContainsPropagate(arm.Body));
        }
        return false;
    }

    /// <summary>Whether an expression subtree contains any <c>?</c> or <c>|&gt;?</c>
    /// site. Used by <see cref="NeedsStmtLowering"/> to decide between the
    /// expression-level and statement-level lowerings.</summary>
    private static bool ContainsPropagate(Expression e) => e switch
    {
        PropagateExpr => true,
        BinaryExpr { Op: BinaryOp.PipePropagate } => true,
        BinaryExpr be => ContainsPropagate(be.Left) || ContainsPropagate(be.Right),
        UnaryExpr ue => ContainsPropagate(ue.Operand),
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
        ExpressionStmt es => ContainsPropagate(es.Expression),
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
                            _w.WriteLine("break;");
                        }
                    }
                }
                _w.WriteLine("}");
                break;

            default:
                // Plain expression leaf — hoist any top-level `?` then assign.
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
        if (e is BlockExpr b)
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
        var vName = $"__qv_{id}";

        _w.Write($"var {qName} = (");
        if (operand is BinaryExpr pipe)
        {
            EmitPipeSpliceOnly(pipe);
        }
        else
        {
            EmitExpression(operand);
        }
        _w.WriteLine(");");
        _w.WriteLine($"if (!{qName}.IsOk) return Err<{errCs}>({qName}.UnwrapErr());");
        _w.WriteLine($"var {vName} = {qName}.Unwrap();");

        _hoistMap[siteSpan] = vName;
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
                _w.Write(EscapeId(id.Name));
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
                    _w.Write("{");
                    EmitExpression(ip.Expression);
                    _w.Write("}");
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
            foreach (var arm in me.Arms)
            {
                EmitPatternForMatch(arm.Pattern, scrutineeType);
                _w.Write(" => ");
                WithExpected(expected, () => EmitExpression(arm.Body));
                _w.WriteLine(",");
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
                EmitExpression(tail);
                _w.WriteLine(";");
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
            _w.Write($"(({enumName.Name})new {enumName.Name}_{fa.FieldName}(");
            EmitFieldInits(rl.Fields);
            _w.Write("))");
            return;
        }

        _w.Write("new ");
        EmitExpression(rl.TypeTarget);
        _w.Write("(");
        EmitFieldInits(rl.Fields);
        _w.Write(")");
    }

    private void EmitFieldInits(ImmutableArray<FieldInit> fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) _w.Write(", ");
            _w.Write(EscapeId(fields[i].Name));
            _w.Write(": ");
            EmitExpression(fields[i].Value);
        }
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

    private static string PascalCase(string snake)
    {
        if (string.IsNullOrEmpty(snake)) return snake;
        var parts = snake.Split('_');
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }

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
        PrimitiveType { Name: "Float" } => "double",
        PrimitiveType { Name: "Bool" } => "bool",
        PrimitiveType { Name: "String" } => "string",
        PrimitiveType { Name: "Unit" } => "Unit",
        PrimitiveType p => p.Name,
        NamedTypeRef n when n.TypeArguments.Length == 0 => n.Name,
        NamedTypeRef n =>
            $"{n.Name}<{string.Join(", ", n.TypeArguments.Select(CSharpTypeDisplay))}>",
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
