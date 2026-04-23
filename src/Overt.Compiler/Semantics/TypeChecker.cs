using System.Collections.Immutable;
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Syntax;

namespace Overt.Compiler.Semantics;

/// <summary>
/// First-pass type checker. Produces:
/// <list type="bullet">
///   <item><see cref="TypeCheckResult.SymbolTypes"/> — the <see cref="TypeRef"/> of every
///     top-level declaration's symbol (function signature, record type, etc).</item>
///   <item><see cref="TypeCheckResult.ExpressionTypes"/> — best-effort type annotation
///     for every expression node reachable from function bodies and type-alias
///     predicates.</item>
/// </list>
///
/// What's done here: declaration-type resolution (lowering <see cref="TypeExpr"/> into
/// <see cref="TypeRef"/>), straightforward expression type inference — literals, binary
/// operators, identifier references via the name resolver, calls via the callee's
/// declared return type, field access against known record types, tuple construction,
/// <c>if</c> / <c>match</c> joining arm types.
///
/// What's NOT done: generic inference across call sites, refinement predicate
/// checking, effect inference / checking, subtype widening, type-mismatch diagnostics.
/// Each of those is its own pass on top of what this produces.
/// </summary>
public sealed class TypeChecker
{
    private readonly ModuleDecl _module;
    private readonly ResolutionResult _resolution;
    private readonly Dictionary<Symbol, TypeRef> _symbolTypes = new();
    private readonly Dictionary<SourceSpan, TypeRef> _expressionTypes = new();
    private readonly List<Diagnostic> _diagnostics = new();

    /// <summary>
    /// Call spans whose arity and argument types should NOT be checked — the call
    /// appears as the right-hand side of a pipe (<c>x |&gt; f(a)</c>) or pipe-propagate
    /// (<c>x |&gt;? f(a)</c>), where the piped value is spliced in as a first positional
    /// argument at lowering time. Checking the syntactic arity would falsely report
    /// an off-by-one.
    /// </summary>
    private readonly HashSet<SourceSpan> _pipeSplicedCalls = new();

    /// <summary>Nested-loop depth, incremented on entry to a while/for-each/loop
    /// body. <see cref="CheckLoopControl"/> reads this to reject <c>break</c> and
    /// <c>continue</c> outside a loop (OV0312).</summary>
    private int _loopDepth;

    // Per-declaration bag of generic parameter names that TypeRefLowering consults to
    // distinguish NamedType("T") as a type variable vs. a named-type reference.
    private HashSet<string> _currentTypeParams = new();

    private TypeChecker(
        ModuleDecl module,
        ResolutionResult resolution,
        ImmutableDictionary<Symbol, TypeRef>? importedSymbolTypes)
    {
        _module = module;
        _resolution = resolution;

        // Seed with the stdlib's synthetic declarations so identifier references into
        // the prelude resolve to real types, not UnknownType.
        foreach (var (symbol, type) in Stdlib.Types)
        {
            _symbolTypes[symbol] = type;
        }

        // Seed with types of symbols pulled in by `use` imports from other modules.
        // The resolver has already placed these symbols in scope; the checker just
        // needs their types so calls through them get real signatures.
        if (importedSymbolTypes is not null)
        {
            foreach (var (symbol, type) in importedSymbolTypes)
            {
                _symbolTypes[symbol] = type;
            }
        }
    }

    public static TypeCheckResult Check(ModuleDecl module, ResolutionResult resolution)
        => Check(module, resolution, importedSymbolTypes: null);

    public static TypeCheckResult Check(
        ModuleDecl module,
        ResolutionResult resolution,
        ImmutableDictionary<Symbol, TypeRef>? importedSymbolTypes)
    {
        var checker = new TypeChecker(module, resolution, importedSymbolTypes);
        checker.CheckModule();
        return new TypeCheckResult(
            module,
            checker._symbolTypes.ToImmutableDictionary(),
            checker._expressionTypes.ToImmutableDictionary(),
            checker._diagnostics.ToImmutableArray());
    }

    // -------------------------------------------------------------- module

    private void CheckModule()
    {
        // Pass 1: record a TypeRef for each top-level declaration's symbol so that
        // references resolve regardless of source order.
        foreach (var decl in _module.Declarations)
        {
            RegisterDeclarationType(decl);
        }

        // Pass 2: walk each declaration's body and annotate expressions.
        foreach (var decl in _module.Declarations)
        {
            AnnotateDeclaration(decl);
        }
    }

    private void RegisterDeclarationType(Declaration decl)
    {
        var symbol = TopLevelSymbolFor(decl);
        if (symbol is null) return;

        _currentTypeParams = TypeParamsFor(decl);
        var type = DeclarationType(decl);
        _symbolTypes[symbol] = type;
        _currentTypeParams = new HashSet<string>();
    }

    private static Symbol? TopLevelSymbolFor(Declaration decl) => decl switch
    {
        FunctionDecl f => new Symbol(SymbolKind.Function, f.Name, f.Span, f),
        RecordDecl r => new Symbol(SymbolKind.Record, r.Name, r.Span, r),
        EnumDecl e => new Symbol(SymbolKind.Enum, e.Name, e.Span, e),
        TypeAliasDecl t => new Symbol(SymbolKind.TypeAlias, t.Name, t.Span, t),
        ExternDecl x => new Symbol(SymbolKind.Extern, x.Name, x.Span, x),
        ExternTypeDecl xt => new Symbol(SymbolKind.Record, xt.Name, xt.Span, xt),
        _ => null,
    };

    private static HashSet<string> TypeParamsFor(Declaration decl) => decl switch
    {
        FunctionDecl f => new HashSet<string>(f.TypeParameters, StringComparer.Ordinal),
        TypeAliasDecl t => new HashSet<string>(t.TypeParameters, StringComparer.Ordinal),
        _ => new HashSet<string>(),
    };

    private TypeRef DeclarationType(Declaration decl) => decl switch
    {
        FunctionDecl f => FunctionSignatureType(f.Parameters, f.Effects, f.ReturnType),
        ExternDecl x => FunctionSignatureType(x.Parameters, x.Effects, x.ReturnType),
        RecordDecl r => new NamedTypeRef(r.Name),
        EnumDecl e => new NamedTypeRef(e.Name),
        TypeAliasDecl t => LowerType(t.Target),
        ExternTypeDecl xt => new NamedTypeRef(xt.Name),
        _ => UnknownType.Instance,
    };

    private FunctionTypeRef FunctionSignatureType(
        ImmutableArray<Parameter> parameters,
        EffectRow? effects,
        TypeExpr? returnType)
    {
        var paramTypes = parameters.Select(p => LowerType(p.Type)).ToImmutableArray();
        var retType = returnType is null ? (TypeRef)PrimitiveType.Unit : LowerType(returnType);
        var effectNames = effects is null
            ? ImmutableArray<string>.Empty
            : effects.Effects;
        return new FunctionTypeRef(paramTypes, retType, effectNames);
    }

    // ------------------------------------------- lowering syntax → type IR

    /// <summary>
    /// Lower a <see cref="TypeExpr"/> (AST form) into a <see cref="TypeRef"/>. Uses
    /// <see cref="_currentTypeParams"/> to recognize type-variable references.
    /// </summary>
    private TypeRef LowerType(TypeExpr type)
    {
        switch (type)
        {
            case NamedType nt:
                if (_currentTypeParams.Contains(nt.Name) && nt.TypeArguments.Length == 0)
                {
                    return new TypeVarRef(nt.Name);
                }
                if (IsPrimitive(nt.Name) && nt.TypeArguments.Length == 0)
                {
                    return PrimitiveFor(nt.Name);
                }
                return new NamedTypeRef(
                    nt.Name,
                    nt.TypeArguments.Select(LowerType).ToImmutableArray());

            case UnitType:
                return PrimitiveType.Unit;

            case FunctionType ft:
                return new FunctionTypeRef(
                    ft.Parameters.Select(LowerType).ToImmutableArray(),
                    LowerType(ft.ReturnType),
                    ft.Effects is null
                        ? ImmutableArray<string>.Empty
                        : ft.Effects.Effects);

            default:
                return UnknownType.Instance;
        }
    }

    private static bool IsPrimitive(string name) =>
        name is "Int" or "Int64" or "Float" or "Bool" or "String";

    private static PrimitiveType PrimitiveFor(string name) => name switch
    {
        "Int" => PrimitiveType.Int,
        "Int64" => PrimitiveType.Int64,
        "Float" => PrimitiveType.Float,
        "Bool" => PrimitiveType.Bool,
        "String" => PrimitiveType.String,
        _ => throw new InvalidOperationException($"not a primitive: {name}"),
    };

    // --------------------------------------------------- body annotation

    private void AnnotateDeclaration(Declaration decl)
    {
        _currentTypeParams = TypeParamsFor(decl);
        switch (decl)
        {
            case FunctionDecl f:
                foreach (var param in f.Parameters)
                {
                    var paramSymbol = new Symbol(
                        SymbolKind.Parameter, param.Name, param.Span, param);
                    _symbolTypes[paramSymbol] = LowerType(param.Type);
                }
                var bodyType = AnnotateExpression(f.Body);
                CheckReturnType(f, bodyType);
                CheckEffectRow(f);
                break;
            case TypeAliasDecl t:
                if (t.Predicate is { } pred) AnnotateExpression(pred);
                break;
            // ExternDecl, RecordDecl, EnumDecl have no bodies to annotate.
        }
        _currentTypeParams = new HashSet<string>();
    }

    // ---------------------------------------------- return type check

    /// <summary>
    /// Check the function body's trailing-expression type against the declared return
    /// type. Only fires when both are concrete — generic returns like <c>Result&lt;T, E&gt;</c>
    /// (still carrying type variables from factory functions like <c>Ok</c>) skip the
    /// check pending unification.
    /// </summary>
    private void CheckReturnType(FunctionDecl fn, TypeRef actualBodyType)
    {
        var declaredReturn = fn.ReturnType is null ? PrimitiveType.Unit : LowerType(fn.ReturnType);
        var declaredUnfolded = UnfoldAlias(declaredReturn);
        var actualUnfolded = UnfoldAlias(actualBodyType);
        if (!IsConcrete(declaredUnfolded) || !IsConcrete(actualUnfolded)) return;
        if (TypesEqual(declaredUnfolded, actualUnfolded)) return;

        var bodySpan = fn.Body.TrailingExpression?.Span ?? fn.Body.Span;
        ReportErrorWithHelp("OV0301",
            $"function `{fn.Name}` declares return type `{declaredReturn.Display}` "
                + $"but body evaluates to `{actualBodyType.Display}`",
            bodySpan,
            $"either change the declared return type or adjust the body to yield `{declaredReturn.Display}`");
    }

    /// <summary>
    /// Replace a non-generic type-alias reference with the lowered target type.
    /// Makes the type-equality and compatibility checks transparent for simple
    /// aliases like <c>type Age = Int</c> — values of type <c>Int</c> pass freely
    /// to <c>Age</c> parameters, and the refinement predicate (if any) is the
    /// only additional check that fires. Generic aliases (<c>type NonEmpty&lt;T&gt;
    /// = List&lt;T&gt;</c>) are emitted as wrapper records by the C# backend and
    /// stay nominal — don't unfold them here.
    /// </summary>
    private TypeRef UnfoldAlias(TypeRef type)
    {
        if (type is not NamedTypeRef nt || nt.TypeArguments.Length > 0) return type;
        var alias = _module.Declarations
            .OfType<TypeAliasDecl>()
            .FirstOrDefault(t => t.Name == nt.Name && t.TypeParameters.Length == 0);
        if (alias is null) return type;
        return LowerType(alias.Target);
    }

    /// <summary>
    /// Annotate an expression node with its inferred type. Returns the type so parents
    /// can chain. Records into <see cref="_expressionTypes"/>. Unknown is returned where
    /// inference can't yet produce a specific type.
    /// </summary>
    private TypeRef AnnotateExpression(Expression expr)
    {
        var type = InferExpression(expr);
        _expressionTypes[expr.Span] = type;
        return type;
    }

    private TypeRef InferExpression(Expression expr) => expr switch
    {
        IntegerLiteralExpr => PrimitiveType.Int,
        FloatLiteralExpr => PrimitiveType.Float,
        BooleanLiteralExpr => PrimitiveType.Bool,
        StringLiteralExpr => PrimitiveType.String,
        InterpolatedStringExpr isx => InferInterpolatedString(isx),
        UnitExpr => PrimitiveType.Unit,

        IdentifierExpr id => InferIdentifier(id),
        FieldAccessExpr fa => InferFieldAccess(fa),
        CallExpr c => InferCall(c),
        PropagateExpr pr => InferPropagate(pr),
        BinaryExpr be => InferBinary(be),
        UnaryExpr ue => InferUnary(ue),

        IfExpr ie => InferIf(ie),
        MatchExpr me => InferMatch(me),
        WhileExpr we => InferWhile(we),
        ForEachExpr fe => InferForEach(fe),
        LoopExpr lp => InferLoop(lp),
        BlockExpr b => InferBlock(b),

        TupleExpr te => InferTuple(te),
        RecordLiteralExpr rl => InferRecordLiteral(rl),
        WithExpr w => InferWith(w),

        ParallelExpr pe => InferParallel(pe),
        RaceExpr re => InferRace(re),
        UnsafeExpr ux => AnnotateExpression(ux.Body),
        TraceExpr tx => AnnotateExpression(tx.Body),

        _ => UnknownType.Instance,
    };

    private TypeRef InferInterpolatedString(InterpolatedStringExpr isx)
    {
        foreach (var part in isx.Parts)
        {
            if (part is StringInterpolationPart ip)
            {
                AnnotateExpression(ip.Expression);
            }
        }
        return PrimitiveType.String;
    }

    private TypeRef InferIdentifier(IdentifierExpr id)
    {
        if (!_resolution.Resolutions.TryGetValue(id.Span, out var symbol))
        {
            return UnknownType.Instance;
        }
        return _symbolTypes.TryGetValue(symbol, out var type) ? type : UnknownType.Instance;
    }

    private TypeRef InferFieldAccess(FieldAccessExpr fa)
    {
        // Module-qualified resolution (e.g. `List.empty`, `Trace.subscribe`) — the
        // resolver already recorded the qualified symbol; if we see it, its signature
        // is the type of this field-access expression.
        if (_resolution.Resolutions.TryGetValue(fa.Span, out var resolved)
            && _symbolTypes.TryGetValue(resolved, out var resolvedType))
        {
            AnnotateExpression(fa.Target); // still walk the target for nested annotation
            return resolvedType;
        }

        var targetType = AnnotateExpression(fa.Target);
        if (targetType is NamedTypeRef nt)
        {
            var decl = _module.Declarations
                .OfType<RecordDecl>()
                .FirstOrDefault(r => r.Name == nt.Name);
            if (decl is not null)
            {
                var field = decl.Fields.FirstOrDefault(f => f.Name == fa.FieldName);
                if (field is not null)
                {
                    return LowerType(field.Type);
                }
                ReportUnknownField(fa, nt.Name, decl);
            }
            // If decl is null, the target might be a stdlib type with no user-declared
            // field list — fall through without a diagnostic. Once stdlib types carry
            // real field info, this arm can also validate.
        }
        return UnknownType.Instance;
    }

    private void ReportUnknownField(FieldAccessExpr fa, string recordName, RecordDecl decl)
    {
        var d = new Diagnostic(
            DiagnosticSeverity.Error,
            "OV0302",
            $"record `{recordName}` has no field `{fa.FieldName}`",
            fa.Span);

        var suggestion = FindFieldSuggestion(fa.FieldName, decl.Fields);
        if (suggestion is not null)
        {
            d = d.WithHelp($"did you mean `{suggestion}`?");
        }
        else if (decl.Fields.Length > 0)
        {
            var names = string.Join(", ", decl.Fields.Select(f => $"`{f.Name}`"));
            d = d.WithHelp($"`{recordName}` has fields: {names}");
        }
        _diagnostics.Add(d);
    }

    private static string? FindFieldSuggestion(string target, ImmutableArray<RecordField> fields)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        var budget = Math.Max(1, target.Length / 3);
        foreach (var field in fields)
        {
            var d = NameResolver.Levenshtein(target, field.Name);
            if (d <= budget && d < bestDistance)
            {
                best = field.Name;
                bestDistance = d;
            }
        }
        return best;
    }

    private TypeRef InferCall(CallExpr c)
    {
        var calleeType = AnnotateExpression(c.Callee);
        foreach (var arg in c.Arguments)
        {
            AnnotateExpression(arg.Value);
        }

        if (calleeType is FunctionTypeRef ft)
        {
            if (!_pipeSplicedCalls.Contains(c.Span))
            {
                CheckCallArity(c, ft);
                CheckCallArgumentTypes(c, ft);
            }
            return ft.Return;
        }
        return UnknownType.Instance;
    }

    private void CheckCallArity(CallExpr c, FunctionTypeRef ft)
    {
        if (c.Arguments.Length == ft.Parameters.Length) return;
        var plural = ft.Parameters.Length == 1 ? "" : "s";
        ReportError("OV0306",
            $"call expects {ft.Parameters.Length} argument{plural}, got {c.Arguments.Length}",
            c.Span);
    }

    private void CheckCallArgumentTypes(CallExpr c, FunctionTypeRef ft)
    {
        var count = Math.Min(c.Arguments.Length, ft.Parameters.Length);
        for (var i = 0; i < count; i++)
        {
            var expected = UnfoldAlias(ft.Parameters[i]);
            var actual = UnfoldAlias(
                _expressionTypes.TryGetValue(c.Arguments[i].Value.Span, out var t)
                    ? t
                    : UnknownType.Instance);

            if (!IsConcrete(expected) || !IsConcrete(actual)) continue;
            if (TypesEqual(expected, actual)) continue;

            ReportErrorWithHelp("OV0300",
                $"argument {i + 1}: expected `{ft.Parameters[i].Display}`, got `{actual.Display}`",
                c.Arguments[i].Value.Span,
                "argument type does not match the declared parameter type");
        }

        // Refinement-predicate check runs regardless of the above type-equality
        // result — a literal of the base type is type-compatible but may still
        // violate the refinement predicate.
        for (var i = 0; i < count; i++)
        {
            CheckRefinementAtBoundary(c.Arguments[i].Value, ft.Parameters[i],
                boundaryDescription: $"argument {i + 1}");
        }
    }

    private TypeRef InferPropagate(PropagateExpr pr)
    {
        var operandType = AnnotateExpression(pr.Operand);
        // `expr?` on Result<T, E> yields T. Match the stdlib type shape heuristically.
        if (operandType is NamedTypeRef { Name: "Result", TypeArguments: { Length: 2 } args })
        {
            return args[0];
        }
        // Option<T>?  yields T (same pattern).
        if (operandType is NamedTypeRef { Name: "Option", TypeArguments: { Length: 1 } oargs })
        {
            return oargs[0];
        }
        return UnknownType.Instance;
    }

    private TypeRef InferBinary(BinaryExpr be)
    {
        // Register pipe RHS calls before descending so the call's arity check knows to
        // skip. `x |> f(a, b)` desugars to `f(x, a, b)`; checking `f(a, b)` against f's
        // signature without the splice would emit a false OV0306.
        if (be.Op is BinaryOp.PipeCompose or BinaryOp.PipePropagate
            && be.Right is CallExpr pipeCall)
        {
            _pipeSplicedCalls.Add(pipeCall.Span);
        }

        var left = AnnotateExpression(be.Left);
        var right = AnnotateExpression(be.Right);
        return be.Op switch
        {
            BinaryOp.Equal or BinaryOp.NotEqual
                or BinaryOp.Less or BinaryOp.LessEqual
                or BinaryOp.Greater or BinaryOp.GreaterEqual
                or BinaryOp.LogicalAnd or BinaryOp.LogicalOr => PrimitiveType.Bool,

            BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply
                or BinaryOp.Divide or BinaryOp.Modulo =>
                // Promote: prefer Float if either side is Float, else Int, else Unknown.
                (left is PrimitiveType { Name: "Float" }
                    || right is PrimitiveType { Name: "Float" })
                    ? PrimitiveType.Float
                    : (left is PrimitiveType { Name: "Int" }
                       && right is PrimitiveType { Name: "Int" })
                        ? PrimitiveType.Int
                        : UnknownType.Instance,

            // Pipe is desugared: x |> f(a, b) has the call's return type. `|>?`
            // additionally unwraps a Result<T, E> result to T, since the Err is
            // propagated to the enclosing function.
            BinaryOp.PipeCompose =>
                right is FunctionTypeRef ft ? ft.Return :
                be.Right is CallExpr call
                    && _expressionTypes.TryGetValue(call.Callee.Span, out var t)
                    && t is FunctionTypeRef ft2 ? ft2.Return
                    : UnknownType.Instance,

            BinaryOp.PipePropagate =>
                UnwrapResultOrKeep(
                    right is FunctionTypeRef pft ? pft.Return :
                    be.Right is CallExpr pcall
                        && _expressionTypes.TryGetValue(pcall.Callee.Span, out var pt)
                        && pt is FunctionTypeRef pft2 ? pft2.Return
                        : UnknownType.Instance),

            _ => UnknownType.Instance,
        };
    }

    /// <summary>Unwrap a <c>Result&lt;T, E&gt;</c> to <c>T</c>; identity otherwise.
    /// Used by <c>|&gt;?</c>'s type inference: the pipe reveals the Ok-type to the
    /// downstream operand, with Err propagated to the enclosing function.</summary>
    private static TypeRef UnwrapResultOrKeep(TypeRef t)
        => t is NamedTypeRef { Name: "Result", TypeArguments: { Length: 2 } args }
            ? args[0]
            : t;

    private TypeRef InferUnary(UnaryExpr ue)
    {
        var operand = AnnotateExpression(ue.Operand);
        return ue.Op switch
        {
            UnaryOp.LogicalNot => PrimitiveType.Bool,
            UnaryOp.Negate => operand, // -(Int)=Int, -(Float)=Float, otherwise whatever we had
            _ => UnknownType.Instance,
        };
    }

    private TypeRef InferIf(IfExpr ie)
    {
        var condType = AnnotateExpression(ie.Condition);
        CheckConditionIsBool(ie.Condition.Span, condType, "`if`");

        var thenType = AnnotateExpression(ie.Then);
        if (ie.Else is { } elseBlock)
        {
            var elseType = AnnotateExpression(elseBlock);
            if (IsConcrete(thenType) && IsConcrete(elseType) && !TypesEqual(thenType, elseType))
            {
                ReportErrorWithHelp("OV0303",
                    $"`if` arms have different types: `{thenType.Display}` and `{elseType.Display}`",
                    ie.Span,
                    "both arms must produce the same type, or one must be coerced to match");
            }
            return JoinTypes(thenType, elseType);
        }
        // Else is absent; per §4 the then block must evaluate to `()`.
        if (IsConcrete(thenType) && !TypesEqual(thenType, PrimitiveType.Unit))
        {
            ReportErrorWithHelp("OV0303",
                $"`if` without `else` requires its body to produce `()`, got `{thenType.Display}`",
                ie.Then.Span,
                "add an `else` arm that yields the same type, or make the body evaluate to `()`");
        }
        return PrimitiveType.Unit;
    }

    private TypeRef InferMatch(MatchExpr me)
    {
        var scrutineeType = AnnotateExpression(me.Scrutinee);
        TypeRef? joined = null;
        SourceSpan? firstArmSpan = null;

        foreach (var arm in me.Arms)
        {
            BindPatternSymbols(arm.Pattern, scrutineeType, SymbolKind.PatternBinding);
            var armType = AnnotateExpression(arm.Body);

            if (joined is null)
            {
                joined = armType;
                firstArmSpan = arm.Body.Span;
            }
            else if (IsConcrete(joined) && IsConcrete(armType) && !TypesEqual(joined, armType))
            {
                var d = new Diagnostic(
                    DiagnosticSeverity.Error,
                    "OV0303",
                    $"match arm has type `{armType.Display}` "
                        + $"but earlier arm produced `{joined.Display}`",
                    arm.Body.Span);
                if (firstArmSpan is { } fs)
                {
                    d = d.WithNoteAt(fs, "earlier arm established this type");
                }
                d = d.WithHelp("every arm must yield the same type, or wrap mismatched arms to coerce");
                _diagnostics.Add(d);
                // Keep `joined` as the first type so downstream reports are consistent.
            }
            else
            {
                joined = JoinTypes(joined, armType);
            }
        }

        CheckMatchExhaustiveness(me, scrutineeType);
        return joined ?? UnknownType.Instance;
    }

    /// <summary>
    /// DESIGN.md §8: exhaustive pattern matching is "the single most valuable check
    /// in the system." When a match scrutinee is an enum-shaped type — user-declared
    /// or a stdlib enum (<c>Option</c>, <c>Result</c>) — verify every variant appears
    /// in some arm (or the match has a catch-all). Missing variants fire OV0308 with
    /// the list of uncovered names so agents adding a variant get every existing
    /// match pinpointed as a todo.
    ///
    /// v0 limitations: only single-enum scrutinees (tuples of enums would be a
    /// cartesian-product walk, deferred). Arity on variant patterns is not checked
    /// here — <c>Some(x, y)</c> still counts as covering <c>Some</c> even though Some
    /// takes one arg.
    /// </summary>
    private void CheckMatchExhaustiveness(MatchExpr me, TypeRef scrutineeType)
    {
        if (scrutineeType is not NamedTypeRef nt) return;

        var allVariants = GetEnumVariants(nt.Name);
        if (allVariants is null) return;

        var covered = new HashSet<string>(StringComparer.Ordinal);
        var hasCatchAll = false;

        foreach (var arm in me.Arms)
        {
            var variant = TryGetVariantName(arm.Pattern, nt.Name, allVariants);
            if (variant is not null)
            {
                covered.Add(variant);
                continue;
            }
            if (IsCatchAllPattern(arm.Pattern, allVariants))
            {
                hasCatchAll = true;
            }
        }

        if (hasCatchAll) return;

        var missing = allVariants.Except(covered).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (missing.Count == 0) return;

        var list = string.Join(", ", missing.Select(v => $"`{nt.Name}.{v}`"));
        var plural = missing.Count == 1 ? "variant" : "variants";
        _diagnostics.Add(
            new Diagnostic(
                DiagnosticSeverity.Error,
                "OV0308",
                $"non-exhaustive match on `{nt.Name}`: missing {plural} {list}",
                me.Span)
                .WithHelp(
                    "add an arm for each missing variant, or add a wildcard arm "
                    + "`_ => ...` to cover the remainder"));
    }

    /// <summary>
    /// Unified variant lookup. User-declared enums come from the module's AST;
    /// stdlib enums (<c>Result</c>, <c>Option</c>) come from
    /// <see cref="Stdlib.EnumVariants"/>. Returns null when <paramref name="enumName"/>
    /// is not an enum-shaped type.
    /// </summary>
    private HashSet<string>? GetEnumVariants(string enumName)
    {
        var userEnum = _module.Declarations
            .OfType<EnumDecl>()
            .FirstOrDefault(e => e.Name == enumName);
        if (userEnum is not null)
        {
            return new HashSet<string>(
                userEnum.Variants.Select(v => v.Name),
                StringComparer.Ordinal);
        }
        if (Stdlib.EnumVariants.TryGetValue(enumName, out var stdlibVariants))
        {
            return new HashSet<string>(stdlibVariants, StringComparer.Ordinal);
        }
        return null;
    }

    /// <summary>
    /// Catch-all patterns match any scrutinee value: <c>_</c>, a bare identifier
    /// that doesn't match a variant of this enum (plain binding), or a tuple of
    /// catch-alls. A bare identifier that DOES match a variant name (e.g. <c>None</c>
    /// on an Option scrutinee) is a variant reference, not a catch-all.
    /// </summary>
    private static bool IsCatchAllPattern(Pattern p, HashSet<string> variants) => p switch
    {
        WildcardPattern => true,
        IdentifierPattern ip => !variants.Contains(ip.Name),
        TuplePattern tp => tp.Elements.All(e => IsCatchAllPattern(e, variants)),
        _ => false,
    };

    /// <summary>
    /// Extract the variant name if the pattern references one of <paramref name="variants"/>.
    /// Accepts both qualified paths (<c>Enum.Variant</c> for user enums) and bare
    /// names (<c>Some</c>, <c>None</c>, <c>Ok</c>, <c>Err</c> for stdlib enums). The
    /// qualified form is matched only against the specific enum name; the bare form
    /// matches if the identifier is any variant of the scrutinee's enum.
    /// </summary>
    private static string? TryGetVariantName(Pattern p, string enumName, HashSet<string> variants)
    {
        switch (p)
        {
            // Two-segment qualified paths: Enum.Variant / Enum.Variant(...) / Enum.Variant { ... }
            case PathPattern pp when pp.Path.Length == 2
                && pp.Path[0] == enumName && variants.Contains(pp.Path[1]):
                return pp.Path[1];
            case ConstructorPattern cp when cp.Path.Length == 2
                && cp.Path[0] == enumName && variants.Contains(cp.Path[1]):
                return cp.Path[1];
            case RecordPattern rp when rp.Path.Length == 2
                && rp.Path[0] == enumName && variants.Contains(rp.Path[1]):
                return rp.Path[1];

            // Single-segment bare variant references: Some(x) / None / Ok(x) / Err(e).
            case IdentifierPattern ip when variants.Contains(ip.Name):
                return ip.Name;
            case PathPattern pp when pp.Path.Length == 1 && variants.Contains(pp.Path[0]):
                return pp.Path[0];
            case ConstructorPattern cp when cp.Path.Length == 1 && variants.Contains(cp.Path[0]):
                return cp.Path[0];
            case RecordPattern rp when rp.Path.Length == 1 && variants.Contains(rp.Path[0]):
                return rp.Path[0];

            default:
                return null;
        }
    }

    private TypeRef InferWhile(WhileExpr we)
    {
        var condType = AnnotateExpression(we.Condition);
        CheckConditionIsBool(we.Condition.Span, condType, "`while`");
        _loopDepth++;
        try { AnnotateExpression(we.Body); }
        finally { _loopDepth--; }
        return PrimitiveType.Unit;
    }

    private TypeRef InferForEach(ForEachExpr fe)
    {
        var iterType = AnnotateExpression(fe.Iterable);
        TypeRef elemType = UnknownType.Instance;
        if (iterType is NamedTypeRef { Name: "List", TypeArguments: { Length: 1 } elemArgs })
        {
            elemType = elemArgs[0];
        }
        else if (IsConcrete(iterType))
        {
            ReportErrorWithHelp("OV0313",
                $"`for each` requires a `List<T>`, got `{iterType.Display}`",
                fe.Iterable.Span,
                "`for each x in xs { ... }` iterates a `List<T>`; convert other collection shapes first");
        }
        BindPatternSymbols(fe.Binder, elemType, SymbolKind.PatternBinding);
        _loopDepth++;
        try { AnnotateExpression(fe.Body); }
        finally { _loopDepth--; }
        return PrimitiveType.Unit;
    }

    private TypeRef InferLoop(LoopExpr lp)
    {
        _loopDepth++;
        try { AnnotateExpression(lp.Body); }
        finally { _loopDepth--; }
        return PrimitiveType.Unit;
    }

    private void CheckConditionIsBool(SourceSpan span, TypeRef actual, string form)
    {
        if (!IsConcrete(actual)) return;
        if (TypesEqual(actual, PrimitiveType.Bool)) return;
        ReportErrorWithHelp("OV0304",
            $"{form} condition must be `Bool`, got `{actual.Display}`",
            span,
            $"wrap the condition in a comparison or boolean-returning call — {form} requires a boolean value to branch on");
    }

    private TypeRef InferBlock(BlockExpr b)
    {
        foreach (var stmt in b.Statements)
        {
            switch (stmt)
            {
                case LetStmt ls:
                    var initType = AnnotateExpression(ls.Initializer);
                    var bindingType = ls.Type is { } annotated ? LowerType(annotated) : initType;
                    BindPatternSymbols(ls.Target, bindingType, SymbolKind.LetBinding);
                    if (ls.Type is not null)
                    {
                        CheckRefinementAtBoundary(ls.Initializer, bindingType,
                            boundaryDescription: "let binding");
                    }
                    break;
                case AssignmentStmt asn:
                    AnnotateExpression(asn.Value);
                    break;
                case ExpressionStmt es:
                    var stmtType = AnnotateExpression(es.Expression);
                    CheckIgnoredResult(es.Expression, stmtType);
                    break;
                case BreakStmt brk:
                    CheckLoopControl("break", brk.Span);
                    break;
                case ContinueStmt cont:
                    CheckLoopControl("continue", cont.Span);
                    break;
            }
        }
        return b.TrailingExpression is { } tail
            ? AnnotateExpression(tail)
            : PrimitiveType.Unit;
    }

    /// <summary>Reject <c>break</c>/<c>continue</c> outside a loop body.</summary>
    private void CheckLoopControl(string keyword, SourceSpan span)
    {
        if (_loopDepth > 0) return;
        ReportErrorWithHelp("OV0312",
            $"`{keyword}` outside of a loop body",
            span,
            $"`{keyword}` is only valid inside `while`, `for each`, or `loop`");
    }

    /// <summary>
    /// DESIGN.md §11: a <c>Result&lt;T, E&gt;</c> that's ignored in statement position
    /// is a compile error. The `?` operator, `let _ = expr`, and pattern-matching on
    /// the result are the three ways to handle one; an unhandled Result hides a latent
    /// error branch. This check fires on every <see cref="ExpressionStmt"/> whose
    /// expression carries a <c>Result</c> type.
    /// </summary>
    private void CheckIgnoredResult(Expression expr, TypeRef exprType)
    {
        if (exprType is not NamedTypeRef { Name: "Result" }) return;

        _diagnostics.Add(
            new Diagnostic(
                DiagnosticSeverity.Error,
                "OV0307",
                $"value of type `{exprType.Display}` is ignored",
                expr.Span)
                .WithHelp(
                    "use `?` to propagate the error, `let _ = expr` to discard "
                    + "explicitly, or `match` to handle both the Ok and Err cases"));
    }

    /// <summary>
    /// Walk a pattern that binds names (let target, match-arm pattern) and register
    /// each binding's symbol with a best-effort type pulled from the scrutinee/initializer.
    /// TuplePattern destructures parallel-element-wise when <paramref name="source"/> is
    /// itself a <see cref="TupleTypeRef"/>; otherwise bindings get <see cref="UnknownType"/>.
    /// </summary>
    private void BindPatternSymbols(Pattern pattern, TypeRef source, SymbolKind kind)
    {
        switch (pattern)
        {
            case IdentifierPattern ip:
                _symbolTypes[new Symbol(kind, ip.Name, ip.Span)] = source;
                break;

            case TuplePattern tp:
                for (var i = 0; i < tp.Elements.Length; i++)
                {
                    var elemType = source is TupleTypeRef tt && i < tt.Elements.Length
                        ? tt.Elements[i]
                        : UnknownType.Instance;
                    BindPatternSymbols(tp.Elements[i], elemType, kind);
                }
                break;

            case ConstructorPattern cp:
                foreach (var arg in cp.Arguments)
                {
                    BindPatternSymbols(arg, UnknownType.Instance, kind);
                }
                break;

            case RecordPattern rp:
                foreach (var fp in rp.Fields)
                {
                    BindPatternSymbols(fp.Subpattern, UnknownType.Instance, kind);
                }
                break;

            // WildcardPattern and PathPattern bind nothing.
        }
    }

    private TypeRef InferTuple(TupleExpr te)
    {
        var elems = te.Elements.Select(AnnotateExpression).ToImmutableArray();
        return new TupleTypeRef(elems);
    }

    private TypeRef InferRecordLiteral(RecordLiteralExpr rl)
    {
        AnnotateExpression(rl.TypeTarget);
        foreach (var fi in rl.Fields)
        {
            AnnotateExpression(fi.Value);
        }

        // Refinement check on each field against the record's declared field type.
        if (NamedTypeFromTarget(rl.TypeTarget) is NamedTypeRef typeTarget)
        {
            var recordDecl = _module.Declarations
                .OfType<RecordDecl>()
                .FirstOrDefault(r => r.Name == typeTarget.Name);
            if (recordDecl is not null)
            {
                foreach (var fi in rl.Fields)
                {
                    var field = recordDecl.Fields.FirstOrDefault(f => f.Name == fi.Name);
                    if (field is null) continue;
                    var fieldType = LowerType(field.Type);
                    CheckRefinementAtBoundary(fi.Value, fieldType,
                        boundaryDescription: $"field `{fi.Name}`");
                }
            }
        }

        // The record literal's type is determined by the type-target identifier chain.
        // For `Point { ... }` the type is `Point`; for `Tree.Node { ... }` the type is
        // the enum base `Tree`. Walk the chain to pick the head name, which is the
        // outermost type.
        return NamedTypeFromTarget(rl.TypeTarget) ?? UnknownType.Instance;
    }

    private static TypeRef? NamedTypeFromTarget(Expression target) => target switch
    {
        IdentifierExpr id => new NamedTypeRef(id.Name),
        FieldAccessExpr fa => NamedTypeFromTarget(fa.Target),
        _ => null,
    };

    private TypeRef InferWith(WithExpr w)
    {
        var targetType = AnnotateExpression(w.Target);
        foreach (var upd in w.Updates)
        {
            AnnotateExpression(upd.Value);
        }
        return targetType;
    }

    private TypeRef InferParallel(ParallelExpr pe)
    {
        var taskTypes = pe.Tasks.Select(AnnotateExpression).ToImmutableArray();
        return new TupleTypeRef(taskTypes);
    }

    private TypeRef InferRace(RaceExpr re)
    {
        TypeRef? shared = null;
        foreach (var t in re.Tasks)
        {
            var taskType = AnnotateExpression(t);
            shared = shared is null ? taskType : JoinTypes(shared, taskType);
        }
        return shared ?? UnknownType.Instance;
    }

    // ------------------------------------------------ type join (loose)

    /// <summary>
    /// Loose join: if the two types are structurally equal, return one. Otherwise return
    /// <see cref="UnknownType"/> — the type checker's subtype and unification rules
    /// land later, not here.
    /// </summary>
    private static TypeRef JoinTypes(TypeRef a, TypeRef b)
    {
        if (a is UnknownType) return b;
        if (b is UnknownType) return a;
        return TypesEqual(a, b) ? a : UnknownType.Instance;
    }

    // ------------------------------------------------ type comparison

    /// <summary>
    /// Structural equality for type refs. Required because <see cref="ImmutableArray{T}"/>
    /// members on records don't participate in the compiler-generated <c>==</c>; a
    /// pure <c>record.Equals</c> call would compare by reference and report false for
    /// equally-shaped <see cref="NamedTypeRef"/>s built from different ImmutableArrays.
    /// </summary>
    private static bool TypesEqual(TypeRef a, TypeRef b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.GetType() != b.GetType()) return false;
        return (a, b) switch
        {
            (UnknownType, UnknownType) => true,
            (PrimitiveType pa, PrimitiveType pb) => pa.Name == pb.Name,
            (NamedTypeRef na, NamedTypeRef nb) =>
                na.Name == nb.Name && AllEqual(na.TypeArguments, nb.TypeArguments),
            (TupleTypeRef ta, TupleTypeRef tb) =>
                AllEqual(ta.Elements, tb.Elements),
            (FunctionTypeRef fa, FunctionTypeRef fb) =>
                AllEqual(fa.Parameters, fb.Parameters)
                && TypesEqual(fa.Return, fb.Return)
                && fa.Effects.SequenceEqual(fb.Effects, StringComparer.Ordinal),
            (TypeVarRef va, TypeVarRef vb) => va.Name == vb.Name,
            _ => false,
        };
    }

    private static bool AllEqual(ImmutableArray<TypeRef> a, ImmutableArray<TypeRef> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!TypesEqual(a[i], b[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// True when a type contains no free <see cref="TypeVarRef"/>s and is not itself
    /// <see cref="UnknownType"/>. Type-error diagnostics only fire when BOTH the
    /// expected and actual types are concrete — unification across type vars belongs
    /// to a later pass, and flagging `Ok(x)`'s <c>Result&lt;T, E&gt;</c> against
    /// <c>Result&lt;Unit, IoError&gt;</c> would be a pure false positive without it.
    /// </summary>
    private static bool IsConcrete(TypeRef t)
    {
        if (t is UnknownType) return false;
        return !ContainsTypeVar(t);
    }

    private static bool ContainsTypeVar(TypeRef t) => t switch
    {
        TypeVarRef => true,
        NamedTypeRef n => n.TypeArguments.Any(ContainsTypeVar),
        TupleTypeRef tt => tt.Elements.Any(ContainsTypeVar),
        FunctionTypeRef ft => ft.Parameters.Any(ContainsTypeVar) || ContainsTypeVar(ft.Return),
        _ => false,
    };

    // ------------------------------------------------ effect-row check

    /// <summary>
    /// v1 core effects declared concretely in Overt source (DESIGN.md §7). Anything
    /// else appearing in an effect row is treated as an effect-row type variable
    /// (e.g. <c>E</c> / <c>F</c>) and skipped by the checker until unification lands.
    /// <c>fails</c> is implicit (implied by any <c>Result</c> return) and never
    /// checked explicitly.
    /// </summary>
    private static readonly HashSet<string> ConcreteEffects = new(StringComparer.Ordinal)
    {
        "io", "async", "inference",
    };

    private static bool IsConcreteEffect(string name) => ConcreteEffects.Contains(name);

    /// <summary>
    /// Compute the set of effects a function body transitively requires and diagnose
    /// any concrete effect not covered by the declared effect row. Conservative: we do
    /// NOT solve effect-row type variables at call sites, so effects that reach the
    /// body only via a variable (<c>fn f(g: fn() !{E} -> ())</c> calling <c>g()</c>)
    /// are invisible to this pass. Proper unification closes that gap; this pass
    /// catches the easy, high-signal cases — <c>fn pure() { println("hi") }</c> would
    /// fire OV0310.
    /// </summary>
    private void CheckEffectRow(FunctionDecl fn)
    {
        var declared = fn.Effects is { } row
            ? new HashSet<string>(row.Effects, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var body = new HashSet<string>(StringComparer.Ordinal);
        CollectBodyEffects(fn.Body, body);

        foreach (var effect in body)
        {
            if (!IsConcreteEffect(effect)) continue;
            if (declared.Contains(effect)) continue;

            var declaredForDisplay = fn.Effects is null
                ? "empty"
                : $"`!{{{string.Join(", ", fn.Effects.Effects)}}}`";
            var suggested = fn.Effects is null
                ? $"!{{{effect}}}"
                : $"!{{{string.Join(", ", fn.Effects.Effects.Append(effect))}}}";
            var reportSpan = fn.Effects?.Span ?? new SourceSpan(fn.Span.Start, fn.Span.Start);
            ReportErrorWithHelp("OV0310",
                $"function `{fn.Name}` performs effect `{effect}` but its effect row is {declaredForDisplay}",
                reportSpan,
                $"add `{effect}` to the signature: `{suggested}`");
        }
    }

    private void CollectBodyEffects(Expression expr, HashSet<string> acc)
    {
        switch (expr)
        {
            case CallExpr c:
                if (TryGetCalleeFunctionType(c.Callee, out var ft))
                {
                    foreach (var eff in ft.Effects) acc.Add(eff);
                }
                CollectBodyEffects(c.Callee, acc);
                foreach (var a in c.Arguments)
                {
                    CollectBodyEffects(a.Value, acc);
                    // Effect-row variable approximation: a function-typed argument's
                    // effect row flows into the caller's effect set. Solves the common
                    // higher-order case without a full unification engine — if a
                    // callee's effect row declares a variable `E` that would be
                    // solved from this argument, those effects manifest at this
                    // call site. Conservative: also fires when the callee stores
                    // rather than invokes the passed function, which in practice is
                    // dominated by callees that DO invoke.
                    var argType = _expressionTypes.TryGetValue(a.Value.Span, out var t)
                        ? t : UnknownType.Instance;
                    if (argType is FunctionTypeRef argFn)
                    {
                        foreach (var eff in argFn.Effects) acc.Add(eff);
                    }
                }
                break;

            case ParallelExpr pe:
                acc.Add("async");
                foreach (var t in pe.Tasks) CollectBodyEffects(t, acc);
                break;

            case RaceExpr re:
                acc.Add("async");
                foreach (var t in re.Tasks) CollectBodyEffects(t, acc);
                break;

            case BlockExpr b:
                foreach (var stmt in b.Statements)
                {
                    switch (stmt)
                    {
                        case LetStmt ls: CollectBodyEffects(ls.Initializer, acc); break;
                        case AssignmentStmt asn: CollectBodyEffects(asn.Value, acc); break;
                        case ExpressionStmt es: CollectBodyEffects(es.Expression, acc); break;
                    }
                }
                if (b.TrailingExpression is { } tail) CollectBodyEffects(tail, acc);
                break;

            case IfExpr ie:
                CollectBodyEffects(ie.Condition, acc);
                CollectBodyEffects(ie.Then, acc);
                if (ie.Else is { } el) CollectBodyEffects(el, acc);
                break;

            case MatchExpr me:
                CollectBodyEffects(me.Scrutinee, acc);
                foreach (var arm in me.Arms) CollectBodyEffects(arm.Body, acc);
                break;

            case WhileExpr we:
                CollectBodyEffects(we.Condition, acc);
                CollectBodyEffects(we.Body, acc);
                break;

            case ForEachExpr fe:
                CollectBodyEffects(fe.Iterable, acc);
                CollectBodyEffects(fe.Body, acc);
                break;

            case LoopExpr lp:
                CollectBodyEffects(lp.Body, acc);
                break;

            case BinaryExpr be:
                CollectBodyEffects(be.Left, acc);
                CollectBodyEffects(be.Right, acc);
                break;

            case UnaryExpr ue:
                CollectBodyEffects(ue.Operand, acc);
                break;

            case PropagateExpr pr:
                CollectBodyEffects(pr.Operand, acc);
                break;

            case FieldAccessExpr fa:
                CollectBodyEffects(fa.Target, acc);
                break;

            case TupleExpr te:
                foreach (var elem in te.Elements) CollectBodyEffects(elem, acc);
                break;

            case WithExpr w:
                CollectBodyEffects(w.Target, acc);
                foreach (var u in w.Updates) CollectBodyEffects(u.Value, acc);
                break;

            case RecordLiteralExpr rl:
                CollectBodyEffects(rl.TypeTarget, acc);
                foreach (var f in rl.Fields) CollectBodyEffects(f.Value, acc);
                break;

            case InterpolatedStringExpr isx:
                foreach (var part in isx.Parts)
                {
                    if (part is StringInterpolationPart ip)
                    {
                        CollectBodyEffects(ip.Expression, acc);
                    }
                }
                break;

            case UnsafeExpr ux:
                CollectBodyEffects(ux.Body, acc);
                break;

            case TraceExpr tx:
                CollectBodyEffects(tx.Body, acc);
                break;

            // Leaf primary expressions contribute no effects.
        }
    }

    private bool TryGetCalleeFunctionType(Expression callee, out FunctionTypeRef ft)
    {
        // Bare identifier callees — look up through the resolver → symbol-type map.
        if (callee is IdentifierExpr id
            && _resolution.Resolutions.TryGetValue(id.Span, out var sym)
            && _symbolTypes.TryGetValue(sym, out var type)
            && type is FunctionTypeRef f)
        {
            ft = f;
            return true;
        }
        // Module-qualified callees (`List.empty`, `Trace.subscribe`) — the resolver
        // recorded the qualified symbol on the FieldAccessExpr span.
        if (callee is FieldAccessExpr fa
            && _resolution.Resolutions.TryGetValue(fa.Span, out var qsym)
            && _symbolTypes.TryGetValue(qsym, out var qtype)
            && qtype is FunctionTypeRef qf)
        {
            ft = qf;
            return true;
        }
        ft = null!;
        return false;
    }

    // ---------------------------------------- refinement predicate check

    /// <summary>
    /// At any point where a value flows into a position typed as a refined alias
    /// (call arg, let binding, record field), check whether the value — if a literal
    /// — satisfies the alias's <c>where</c> predicate. Violations that can be decided
    /// statically fire OV0311. Undecidable predicates or non-literal values fall
    /// through silently; DESIGN.md §8 says those become runtime assertions at the
    /// boundary (emission work, not this pass).
    /// </summary>
    private void CheckRefinementAtBoundary(
        Expression value,
        TypeRef expectedType,
        string boundaryDescription)
    {
        if (GetRefinementAlias(expectedType) is not { } alias) return;
        if (alias.Predicate is null) return;

        var selfValue = RefinementEvaluator.TryExtractLiteral(value);
        if (selfValue is null) return;

        var result = RefinementEvaluator.Evaluate(alias.Predicate, selfValue);
        if (result != false) return; // null = undecidable, true = satisfied

        var predicateText = FormatPredicate(alias.Predicate);
        var d = new Diagnostic(
            DiagnosticSeverity.Error,
            "OV0311",
            $"{boundaryDescription}: value does not satisfy refinement predicate on `{alias.Name}`",
            value.Span)
            .WithHelp($"values of type `{alias.Name}` must satisfy: {predicateText}")
            .WithNoteAt(alias.Span, $"refinement `{alias.Name}` is declared here");
        _diagnostics.Add(d);
    }

    /// <summary>
    /// Locate the <see cref="TypeAliasDecl"/> whose <paramref name="type"/> references
    /// it, if that alias carries a refinement predicate. Returns null for plain types,
    /// non-alias references, or aliases without predicates.
    /// </summary>
    private TypeAliasDecl? GetRefinementAlias(TypeRef type)
    {
        if (type is not NamedTypeRef nt) return null;
        return _module.Declarations
            .OfType<TypeAliasDecl>()
            .FirstOrDefault(t => t.Name == nt.Name && t.Predicate is not null);
    }

    /// <summary>
    /// Pretty-print a predicate expression for inclusion in a diagnostic message.
    /// Recognizes the common range / logical shapes; falls back to a placeholder
    /// for anything more exotic.
    /// </summary>
    private static string FormatPredicate(Expression e) => e switch
    {
        BinaryExpr be => $"{FormatPredicate(be.Left)} {FormatBinaryOp(be.Op)} {FormatPredicate(be.Right)}",
        UnaryExpr { Op: UnaryOp.Negate } ue => $"-{FormatPredicate(ue.Operand)}",
        UnaryExpr { Op: UnaryOp.LogicalNot } ue => $"!{FormatPredicate(ue.Operand)}",
        IdentifierExpr id => id.Name,
        IntegerLiteralExpr i => i.Lexeme,
        FloatLiteralExpr f => f.Lexeme,
        BooleanLiteralExpr b => b.Value ? "true" : "false",
        StringLiteralExpr s => s.Value,
        _ => "...",
    };

    private static string FormatBinaryOp(BinaryOp op) => op switch
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

    // ----------------------------------------------- diagnostic reporting

    private void ReportError(string code, string message, SourceSpan span) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span));

    private void ReportErrorWithHelp(string code, string message, SourceSpan span, string help)
        => _diagnostics.Add(
            new Diagnostic(DiagnosticSeverity.Error, code, message, span).WithHelp(help));
}

public sealed record TypeCheckResult(
    ModuleDecl Module,
    ImmutableDictionary<Symbol, TypeRef> SymbolTypes,
    ImmutableDictionary<SourceSpan, TypeRef> ExpressionTypes,
    ImmutableArray<Diagnostic> Diagnostics);
