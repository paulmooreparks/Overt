using Overt.Compiler.Syntax;

namespace Overt.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceSpan Span)
{
    public override string ToString() => $"[{Severity} {Code}] {Message} @ {Span}";
}
