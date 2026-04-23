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
        // Seed the module scope with synthetic stdlib declarations (DESIGN.md §18).
        // Without this, every reference to `println` / `Ok` / `Result` / `List` etc.
        // would require an allow-list hack to avoid unknown-name diagnostics.
        // Prelude is marked ambient: patterns and locals may reuse stdlib names without
        // tripping no-shadowing.
        var preludeScope = new Scope(isPrelude: true);
        foreach (var stdlibSymbol in Stdlib.Symbols.Values)
        {
            preludeScope.Define(stdlibSymbol);
        }

        var moduleScope = new Scope(preludeScope);

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
                    // Primitive names (Int, Float, Bool, String) aren't in the prelude
                    // scope because they're part of the type grammar, not declared values.
                    if (!IsPrimitiveTypeName(nt.Name))
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

    private static bool IsPrimitiveTypeName(string name) =>
        name is "Int" or "Float" or "Bool" or "String";

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
                // Resolve the target first; field lookup against record types happens
                // later in the type checker.
                ResolveExpression(fa.Target, scope);
                // If the target is a bare identifier that names a stdlib namespace
                // (List, Trace, CString), try a module-qualified lookup `X.Y` in the
                // stdlib table. This lets references like `List.empty` / `Trace.subscribe`
                // carry real function types through to the type checker and effect walker.
                if (fa.Target is IdentifierExpr moduleIdent
                    && Stdlib.Symbols.TryGetValue(
                        $"{moduleIdent.Name}.{fa.FieldName}", out var qualified))
                {
                    _resolutions[fa.Span] = qualified;
                }
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
            ReportUnknownName(id.Name, id.Span, scope);
            return;
        }
        _resolutions[id.Span] = sym;
    }

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
            case LiteralPattern:
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
        var d = new Diagnostic(
                DiagnosticSeverity.Error,
                "OV0201",
                $"name `{incoming.Name}` cannot be rebound; Overt does not permit shadowing",
                incoming.DeclarationSpan)
            .WithNoteAt(existing.DeclarationSpan, $"`{incoming.Name}` was first bound here")
            .WithHelp("pick a different name, or remove one of the bindings");
        _diagnostics.Add(d);
    }

    private void Report(string code, string message, SourceSpan span)
    {
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, code, message, span));
    }

    private void ReportUnknownName(string name, SourceSpan span, Scope scope)
    {
        var d = new Diagnostic(
            DiagnosticSeverity.Error,
            "OV0200",
            $"unknown name `{name}`",
            span);
        var suggestion = FindSuggestion(name, scope);
        if (suggestion is not null)
        {
            d = d.WithHelp($"did you mean `{suggestion}`?");
        }
        _diagnostics.Add(d);
    }

    private static string? FindSuggestion(string target, Scope scope)
    {
        // Walk every in-scope name; return the closest match within a small Levenshtein
        // threshold. O(n * m) per miss, but n is the scope size and m is the name length —
        // both small enough to not matter at MVP compiler sizes.
        string? best = null;
        var bestDistance = int.MaxValue;
        var candidates = CollectNames(scope);
        foreach (var candidate in candidates)
        {
            var d = Levenshtein(target, candidate);
            var budget = Math.Max(1, target.Length / 3);
            if (d <= budget && d < bestDistance)
            {
                best = candidate;
                bestDistance = d;
            }
        }
        return best;
    }

    private static IEnumerable<string> CollectNames(Scope scope)
    {
        // Reflect the private _symbols dictionary indirectly by keeping a small helper
        // inside Scope would be cleaner; for now, walk through what we can reach.
        // Since Scope exposes only Lookup, we rely on the resolver's own tracking to
        // enumerate.
        for (var s = scope; s is not null; s = s.Parent)
        {
            foreach (var n in s.Names)
            {
                yield return n;
            }
        }
    }

    internal static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}

public sealed record ResolutionResult(
    ModuleDecl Module,
    ImmutableDictionary<SourceSpan, Symbol> Resolutions,
    ImmutableArray<Diagnostic> Diagnostics);
