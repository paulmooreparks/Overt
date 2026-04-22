using System.Collections.Immutable;
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Syntax;

namespace Overt.Compiler.Semantics;

/// <summary>
/// First semantic pass. Walks the <see cref="ModuleDecl"/> AST, builds a symbol table
/// for top-level declarations, and resolves every <see cref="IdentifierExpr"/> reference
/// to a <see cref="Symbol"/>. Also resolves the head identifier of named type references.
///
/// What's NOT done here yet: module-qualified resolution (<c>List.empty</c>), field
/// access resolution (<c>user.name</c>), enum-variant resolution of dotted paths
/// (<c>Tree.Empty</c>). Those land with the type checker, which has the declared field
/// and variant tables in hand. For now, dotted paths resolve their head segment only
/// and later segments are left to semantic analysis.
/// </summary>
public sealed class NameResolver
{
    private readonly ModuleDecl _module;
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly Dictionary<SourceSpan, Symbol> _resolutions = new();

    private NameResolver(ModuleDecl module)
    {
        _module = module;
    }

    public static ResolutionResult Resolve(ModuleDecl module)
    {
        var resolver = new NameResolver(module);
        resolver.ResolveModule();
        return new ResolutionResult(
            module,
            resolver._resolutions.ToImmutableDictionary(),
            resolver._diagnostics.ToImmutableArray());
    }

    private void ResolveModule()
    {
        var moduleScope = new Scope();

        // Pass 1: collect all top-level declarations so that mutual recursion and
        // forward references work. Name resolution inside bodies happens in pass 2.
        foreach (var decl in _module.Declarations)
        {
            var symbol = TopLevelSymbolFor(decl);
            if (symbol is null)
            {
                continue;
            }
            var existing = moduleScope.FindConflict(symbol.Name);
            if (existing is not null)
            {
                ReportDuplicate(symbol, existing);
                continue;
            }
            moduleScope.Define(symbol);
        }

        // Pass 2: resolve references inside each declaration.
        foreach (var decl in _module.Declarations)
        {
            ResolveDeclaration(decl, moduleScope);
        }
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

    // ------------------------------------------------------ declarations

    private void ResolveDeclaration(Declaration decl, Scope moduleScope)
    {
        switch (decl)
        {
            case FunctionDecl f: ResolveFunctionDecl(f, moduleScope); break;
            case ExternDecl x: ResolveExternDecl(x, moduleScope); break;
            case RecordDecl r: ResolveRecordDecl(r, moduleScope); break;
            case EnumDecl e: ResolveEnumDecl(e, moduleScope); break;
            case TypeAliasDecl t: ResolveTypeAliasDecl(t, moduleScope); break;
        }
    }

    private void ResolveFunctionDecl(FunctionDecl fn, Scope moduleScope)
    {
        var sigScope = new Scope(moduleScope);

        foreach (var typeParam in fn.TypeParameters)
        {
            DefineOrReport(sigScope, new Symbol(
                SymbolKind.TypeParameter, typeParam, fn.Span));
        }

        foreach (var param in fn.Parameters)
        {
            ResolveType(param.Type, sigScope);
            DefineOrReport(sigScope, new Symbol(
                SymbolKind.Parameter, param.Name, param.Span, param));
        }

        if (fn.Effects is { } effects)
        {
            foreach (var effect in effects.Effects)
            {
                // Effect names resolve against the sig scope (for effect-row type
                // variables); builtin names `io`/`async`/`inference` are allowed even
                // without a declared type parameter, so missing is not an error here.
            }
        }

        if (fn.ReturnType is { } rt)
        {
            ResolveType(rt, sigScope);
        }

        ResolveExpression(fn.Body, sigScope);
    }

    private void ResolveExternDecl(ExternDecl ext, Scope moduleScope)
    {
        var sigScope = new Scope(moduleScope);
        foreach (var param in ext.Parameters)
        {
            ResolveType(param.Type, sigScope);
            DefineOrReport(sigScope, new Symbol(
                SymbolKind.Parameter, param.Name, param.Span, param));
        }
        if (ext.ReturnType is { } rt)
        {
            ResolveType(rt, sigScope);
        }
    }

    private void ResolveRecordDecl(RecordDecl rec, Scope moduleScope)
    {
        foreach (var field in rec.Fields)
        {
            ResolveType(field.Type, moduleScope);
        }
    }

    private void ResolveEnumDecl(EnumDecl e, Scope moduleScope)
    {
        foreach (var variant in e.Variants)
        {
            foreach (var field in variant.Fields)
            {
                ResolveType(field.Type, moduleScope);
            }
        }
    }

    private void ResolveTypeAliasDecl(TypeAliasDecl t, Scope moduleScope)
    {
        var aliasScope = new Scope(moduleScope);
        foreach (var typeParam in t.TypeParameters)
        {
            DefineOrReport(aliasScope, new Symbol(
                SymbolKind.TypeParameter, typeParam, t.Span));
        }
        ResolveType(t.Target, aliasScope);
        if (t.Predicate is { } pred)
        {
            // `self` is a synthetic binding in refinement predicates referring to
            // the being-refined value.
            var predScope = new Scope(aliasScope);
            predScope.Define(new Symbol(SymbolKind.PatternBinding, "self", t.Span));
            ResolveExpression(pred, predScope);
        }
    }

    // -------------------------------------------------- type expressions

    private void ResolveType(TypeExpr type, Scope scope)
    {
        switch (type)
        {
            case NamedType nt:
                // Single-segment reference only. Qualified paths (e.g. `Mod.T`) are not
                // modeled in the type AST yet — NamedType is always a single identifier.
                var symbol = scope.Lookup(nt.Name);
                if (symbol is null)
                {
                    // Built-in primitive types (Int, Float, Bool, String, Option, Result, List,
                    // etc.) aren't in scope yet because we have no stdlib. Leave these
                    // unresolved and defer to a later pass that knows the stdlib.
                    if (!IsLikelyStdlibType(nt.Name))
                    {
                        Report("OV0200",
                            $"unknown type `{nt.Name}`",
                            nt.Span);
                    }
                }
                else
                {
                    _resolutions[nt.Span] = symbol;
                }
                foreach (var arg in nt.TypeArguments)
                {
                    ResolveType(arg, scope);
                }
                break;

            case FunctionType ft:
                foreach (var p in ft.Parameters) ResolveType(p, scope);
                ResolveType(ft.ReturnType, scope);
                break;

            case UnitType:
                break;
        }
    }

    // Placeholder: until a stdlib-aware resolver exists, don't flag common stdlib type
    // names as unknown. This list is an obvious accretion point; we'll replace it once
    // stdlib declarations are available to the resolver.
    private static readonly HashSet<string> StdlibTypes = new(StringComparer.Ordinal)
    {
        "Int", "Float", "Bool", "String", "CString", "Ptr",
        "Option", "Result", "List", "Map", "Set",
        "IoError", "HttpError", "TraceEvent", "RaceAllFailed",
    };

    private static bool IsLikelyStdlibType(string name) => StdlibTypes.Contains(name);

    // -------------------------------------------------------- expressions

    private void ResolveExpression(Expression expr, Scope scope)
    {
        switch (expr)
        {
            case IdentifierExpr id:
                ResolveIdentifierReference(id, scope);
                break;

            case BlockExpr b:
                ResolveBlock(b, scope);
                break;

            case CallExpr c:
                ResolveExpression(c.Callee, scope);
                foreach (var arg in c.Arguments)
                {
                    ResolveExpression(arg.Value, scope);
                }
                break;

            case FieldAccessExpr fa:
                // Resolve the target; the field itself is not a name in any scope.
                ResolveExpression(fa.Target, scope);
                break;

            case PropagateExpr pr:
                ResolveExpression(pr.Operand, scope);
                break;

            case BinaryExpr be:
                ResolveExpression(be.Left, scope);
                ResolveExpression(be.Right, scope);
                break;

            case UnaryExpr ue:
                ResolveExpression(ue.Operand, scope);
                break;

            case IfExpr ie:
                ResolveExpression(ie.Condition, scope);
                ResolveExpression(ie.Then, scope);
                if (ie.Else is { } els) ResolveExpression(els, scope);
                break;

            case WhileExpr we:
                ResolveExpression(we.Condition, scope);
                ResolveExpression(we.Body, scope);
                break;

            case MatchExpr me:
                ResolveExpression(me.Scrutinee, scope);
                foreach (var arm in me.Arms)
                {
                    var armScope = new Scope(scope);
                    DefineFromPattern(arm.Pattern, armScope, SymbolKind.PatternBinding);
                    ResolveExpression(arm.Body, armScope);
                }
                break;

            case TupleExpr te:
                foreach (var e in te.Elements) ResolveExpression(e, scope);
                break;

            case ParallelExpr pe:
                foreach (var t in pe.Tasks) ResolveExpression(t, scope);
                break;

            case RaceExpr re:
                foreach (var t in re.Tasks) ResolveExpression(t, scope);
                break;

            case UnsafeExpr ux:
                ResolveExpression(ux.Body, scope);
                break;

            case TraceExpr tx:
                ResolveExpression(tx.Body, scope);
                break;

            case WithExpr w:
                ResolveExpression(w.Target, scope);
                foreach (var upd in w.Updates)
                {
                    ResolveExpression(upd.Value, scope);
                }
                break;

            case RecordLiteralExpr rl:
                ResolveExpression(rl.TypeTarget, scope);
                foreach (var fi in rl.Fields)
                {
                    ResolveExpression(fi.Value, scope);
                }
                break;

            case InterpolatedStringExpr isx:
                foreach (var part in isx.Parts)
                {
                    if (part is StringInterpolationPart ip)
                    {
                        ResolveExpression(ip.Expression, scope);
                    }
                }
                break;

            // Leaf primaries with no embedded references.
            case IntegerLiteralExpr:
            case FloatLiteralExpr:
            case BooleanLiteralExpr:
            case StringLiteralExpr:
            case UnitExpr:
                break;
        }
    }

    private void ResolveBlock(BlockExpr block, Scope outer)
    {
        var scope = new Scope(outer);
        foreach (var stmt in block.Statements)
        {
            switch (stmt)
            {
                case LetStmt ls:
                    if (ls.Type is { } ty) ResolveType(ty, scope);
                    ResolveExpression(ls.Initializer, scope);
                    // The pattern's bindings come into scope *after* the initializer so
                    // a let's own name can't refer to itself (same as Rust).
                    DefineFromPattern(ls.Target, scope, SymbolKind.LetBinding);
                    break;

                case AssignmentStmt asn:
                    var target = scope.Lookup(asn.Name);
                    if (target is null)
                    {
                        Report("OV0200", $"unknown name `{asn.Name}`",
                            new SourceSpan(asn.Span.Start, asn.Span.Start));
                    }
                    else
                    {
                        _resolutions[new SourceSpan(asn.Span.Start, asn.Span.Start)] = target;
                    }
                    ResolveExpression(asn.Value, scope);
                    break;

                case ExpressionStmt es:
                    ResolveExpression(es.Expression, scope);
                    break;
            }
        }
        if (block.TrailingExpression is { } tail)
        {
            ResolveExpression(tail, scope);
        }
    }

    private void ResolveIdentifierReference(IdentifierExpr id, Scope scope)
    {
        var sym = scope.Lookup(id.Name);
        if (sym is null)
        {
            // Stdlib constructors / modules (`Ok`, `Err`, `Some`, `None`, `List`, etc.)
            // are not bound until a stdlib resolver exists — tolerate them here, flag
            // everything else.
            if (!IsLikelyStdlibName(id.Name))
            {
                Report("OV0200", $"unknown name `{id.Name}`", id.Span);
            }
            return;
        }
        _resolutions[id.Span] = sym;
    }

    private static readonly HashSet<string> StdlibNames = new(StringComparer.Ordinal)
    {
        "Ok", "Err", "Some", "None",
        "List", "Map", "Set", "Option", "Result",
        "String", "Int", "Float", "Bool",
        "println", "print", "eprintln", "format",
        "par_map", "fold", "map", "filter", "sum_by", "collect",
        "size", "length", "len",
        "Trace", "CString",
    };

    private static bool IsLikelyStdlibName(string name) => StdlibNames.Contains(name);

    // ----------------------------------------------------------- patterns

    private void DefineFromPattern(Pattern pattern, Scope scope, SymbolKind kind)
    {
        switch (pattern)
        {
            case IdentifierPattern ip:
                // In pattern position, a lone identifier that matches an existing
                // in-scope zero-arg constructor (enum variant) is a reference, not a
                // fresh binding. We can't tell without knowing which is which — defer
                // to the type checker. For now, always bind.
                DefineOrReport(scope, new Symbol(kind, ip.Name, ip.Span));
                break;

            case WildcardPattern:
            case PathPattern:
                break;

            case ConstructorPattern cp:
                foreach (var arg in cp.Arguments) DefineFromPattern(arg, scope, kind);
                break;

            case RecordPattern rp:
                foreach (var fp in rp.Fields) DefineFromPattern(fp.Subpattern, scope, kind);
                break;

            case TuplePattern tp:
                foreach (var elem in tp.Elements) DefineFromPattern(elem, scope, kind);
                break;
        }
    }

    // --------------------------------------------------------- diagnostics

    private void DefineOrReport(Scope scope, Symbol symbol)
    {
        var existing = scope.FindConflict(symbol.Name);
        if (existing is not null)
        {
            ReportDuplicate(symbol, existing);
            return;
        }
        scope.Define(symbol);
    }

    private void ReportDuplicate(Symbol incoming, Symbol existing)
    {
        Report("OV0201",
            $"name `{incoming.Name}` is already bound at {existing.DeclarationSpan.Start}; "
                + "Overt does not permit shadowing",
            incoming.DeclarationSpan);
    }

    private void Report(string code, string message, SourceSpan span)
    {
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span));
    }
}

public sealed record ResolutionResult(
    ModuleDecl Module,
    ImmutableDictionary<SourceSpan, Symbol> Resolutions,
    ImmutableArray<Diagnostic> Diagnostics);
