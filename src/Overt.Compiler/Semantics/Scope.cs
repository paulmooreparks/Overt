namespace Overt.Compiler.Semantics;

/// <summary>
/// A lexical scope. DESIGN.md §3 forbids shadowing outright — a name bound in an
/// enclosing scope cannot be rebound in any inner scope. <see cref="FindConflict"/>
/// therefore walks ancestors, not just the current scope, before a <see cref="Define"/>
/// call is accepted.
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.Ordinal);

    public Scope? Parent { get; }

    /// <summary>
    /// Marks the prelude scope (stdlib synthetic symbols). The prelude is ambient:
    /// patterns and locals can reuse a stdlib name (e.g. a match arm named <c>None</c>,
    /// a local <c>ok</c>) without the <see cref="FindConflict"/> check flagging it.
    /// User-defined scopes do not mark this.
    /// </summary>
    public bool IsPrelude { get; }

    public Scope(Scope? parent = null, bool isPrelude = false)
    {
        Parent = parent;
        IsPrelude = isPrelude;
    }

    /// <summary>Look up a name through this scope and all ancestors.</summary>
    public Symbol? Lookup(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope._symbols.TryGetValue(name, out var s))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the symbol that would shadow or conflict with <paramref name="name"/>,
    /// anywhere in the non-prelude scope chain. Used to enforce DESIGN.md §3's
    /// no-shadowing rule — with prelude exempted so stdlib names don't block
    /// ordinary locals or pattern bindings.
    /// </summary>
    public Symbol? FindConflict(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.IsPrelude) continue;
            if (scope._symbols.TryGetValue(name, out var s))
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>Unconditionally record a binding in this scope.</summary>
    public void Define(Symbol symbol)
    {
        _symbols[symbol.Name] = symbol;
    }

    /// <summary>Names bound directly in this scope (no ancestors).</summary>
    public IEnumerable<string> Names => _symbols.Keys;
}
