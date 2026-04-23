using Overt.Compiler.Syntax;

namespace Overt.Compiler.Semantics;

public enum SymbolKind
{
    // Module-level
    Function,
    Record,
    Enum,
    TypeAlias,
    Extern,

    // Local — per-function-body
    Parameter,
    LetBinding,
    PatternBinding,

    // Generic / effect parameters — per-function-signature
    TypeParameter,

    // Cross-file import aliases — per `use module as alias`
    ModuleAlias,
}

/// <summary>
/// What a name refers to. A <see cref="Symbol"/> is always tied to a declaration site
/// (the <see cref="DeclarationSpan"/>); identifier-reference sites point *at* symbols
/// via the <see cref="ResolutionResult"/>.
/// </summary>
public sealed record Symbol(
    SymbolKind Kind,
    string Name,
    SourceSpan DeclarationSpan,
    SyntaxNode? Declaration = null);
