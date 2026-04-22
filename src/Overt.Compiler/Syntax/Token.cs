namespace Overt.Compiler.Syntax;

public sealed record Token(TokenKind Kind, string Lexeme, SourceSpan Span)
{
    public override string ToString() => $"{Kind}({Lexeme}) @ {Span}";
}
