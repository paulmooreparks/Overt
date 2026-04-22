using System.Collections.Immutable;
using Overt.Compiler.Syntax;

namespace Overt.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A diagnostic: one actionable sentence plus a location, optionally followed by
/// <see cref="Notes"/> — each a "help:" or "note:" follow-up line giving context or a
/// suggested fix. Per DESIGN.md §4, debug-time is the most expensive token budget, so
/// diagnostics should explain *what to do*, not just *what went wrong*.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceSpan Span,
    ImmutableArray<DiagnosticNote> Notes = default)
{
    public ImmutableArray<DiagnosticNote> Notes { get; init; } =
        Notes.IsDefault ? ImmutableArray<DiagnosticNote>.Empty : Notes;

    public override string ToString() => $"[{Severity} {Code}] {Message} @ {Span}";

    /// <summary>Returns a copy with an added help note.</summary>
    public Diagnostic WithHelp(string text) =>
        this with { Notes = Notes.Add(new DiagnosticNote(DiagnosticNoteKind.Help, text, Span: null)) };

    /// <summary>Returns a copy with an added note that cross-references another span.</summary>
    public Diagnostic WithNoteAt(SourceSpan relatedSpan, string text) =>
        this with { Notes = Notes.Add(new DiagnosticNote(DiagnosticNoteKind.Note, text, relatedSpan)) };
}

public enum DiagnosticNoteKind
{
    Help,
    Note,
}

/// <summary>
/// A follow-up line attached to a <see cref="Diagnostic"/>. <c>help:</c> lines suggest
/// a fix or explain the expected form. <c>note:</c> lines with a <see cref="Span"/>
/// point at another location (e.g., an earlier declaration that conflicts).
/// </summary>
public sealed record DiagnosticNote(
    DiagnosticNoteKind Kind,
    string Text,
    SourceSpan? Span);
