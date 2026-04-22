namespace Overt.Compiler.Syntax;

public readonly record struct SourcePosition(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

public readonly record struct SourceSpan(SourcePosition Start, SourcePosition End)
{
    public override string ToString() => $"{Start}..{End}";
}
