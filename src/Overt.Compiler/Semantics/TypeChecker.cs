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

    // Per-declaration bag of generic parameter names that TypeRefLowering consults to
    // distinguish NamedType("T") as a type variable vs. a named-type reference.
    private HashSet<string> _currentTypeParams = new();

    private TypeChecker(ModuleDecl module, ResolutionResult resolution)
    {
        _module = module;
        _resolution = resolution;

        // Seed with the stdlib's synthetic declarations so identifier references into
        // the prelude resolve to real types, not UnknownType.
        foreach (var (symbol, type) in Stdlib.Types)
        {
            _symbolTypes[symbol] = type;
        }
    }

    public static TypeCheckResult Check(ModuleDecl module, ResolutionResult resolution)
    {
        var checker = new TypeChecker(module, resolution);
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
        name is "Int" or "Float" or "Bool" or "String";

    private static PrimitiveType PrimitiveFor(string name) => name switch
    {
        "Int" => PrimitiveType.Int,
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
                AnnotateExpression(f.Body);
                break;
            case TypeAliasDecl t:
                if (t.Predicate is { } pred) AnnotateExpression(pred);
                break;
            // ExternDecl, RecordDecl, EnumDecl have no bodies to annotate.
        }
        _currentTypeParams = new HashSet<string>();
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
        var targetType = AnnotateExpression(fa.Target);
        // Resolve against the target's record declaration if we have one.
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
            }
        }
        return UnknownType.Instance;
    }

    private TypeRef InferCall(CallExpr c)
    {
        var calleeType = AnnotateExpression(c.Callee);
        foreach (var arg in c.Arguments)
        {
            AnnotateExpression(arg.Value);
        }
        return calleeType is FunctionTypeRef ft ? ft.Return : UnknownType.Instance;
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

            // Pipe is desugared: x |> f(a, b) has the call's return type.
            BinaryOp.PipeCompose or BinaryOp.PipePropagate =>
                right is FunctionTypeRef ft ? ft.Return :
                be.Right is CallExpr call
                    && _expressionTypes.TryGetValue(call.Callee.Span, out var t)
                    && t is FunctionTypeRef ft2 ? ft2.Return
                    : UnknownType.Instance,

            _ => UnknownType.Instance,
        };
    }

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
        AnnotateExpression(ie.Condition);
        var thenType = AnnotateExpression(ie.Then);
        if (ie.Else is { } elseBlock)
        {
            var elseType = AnnotateExpression(elseBlock);
            return JoinTypes(thenType, elseType);
        }
        return PrimitiveType.Unit;
    }

    private TypeRef InferMatch(MatchExpr me)
    {
        var scrutineeType = AnnotateExpression(me.Scrutinee);
        TypeRef? joined = null;
        foreach (var arm in me.Arms)
        {
            BindPatternSymbols(arm.Pattern, scrutineeType, SymbolKind.PatternBinding);
            var armType = AnnotateExpression(arm.Body);
            joined = joined is null ? armType : JoinTypes(joined, armType);
        }
        return joined ?? UnknownType.Instance;
    }

    private TypeRef InferWhile(WhileExpr we)
    {
        AnnotateExpression(we.Condition);
        AnnotateExpression(we.Body);
        return PrimitiveType.Unit;
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
                    break;
                case AssignmentStmt asn:
                    AnnotateExpression(asn.Value);
                    break;
                case ExpressionStmt es:
                    AnnotateExpression(es.Expression);
                    break;
            }
        }
        return b.TrailingExpression is { } tail
            ? AnnotateExpression(tail)
            : PrimitiveType.Unit;
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
    /// Loose join: if the two types are equal, return one. Otherwise return
    /// <see cref="UnknownType"/> — the type checker's subtype and unification rules
    /// land later, not here.
    /// </summary>
    private static TypeRef JoinTypes(TypeRef a, TypeRef b)
    {
        if (a is UnknownType) return b;
        if (b is UnknownType) return a;
        return a == b ? a : UnknownType.Instance;
    }
}

public sealed record TypeCheckResult(
    ModuleDecl Module,
    ImmutableDictionary<Symbol, TypeRef> SymbolTypes,
    ImmutableDictionary<SourceSpan, TypeRef> ExpressionTypes,
    ImmutableArray<Diagnostic> Diagnostics);
