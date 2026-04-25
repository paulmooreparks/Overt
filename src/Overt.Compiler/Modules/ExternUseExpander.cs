using System.Collections.Immutable;
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Syntax;

namespace Overt.Compiler.Modules;

/// <summary>
/// Expands <see cref="ExternUseDecl"/> nodes in a parsed module by delegating
/// to a per-backend resolver. The resolver is supplied as a callback so the
/// compiler stays free of references to specific back-end projects: the C#
/// back end, when wired in by a host (CLI, MSBuild task, test harness),
/// passes a callback that invokes its own binding-generator. Future Go and
/// Rust back ends supply their own callbacks. The expander is the seam
/// between "Overt sees an extern use declaration" and "the target's metadata
/// is reflected and turned into Overt source."
///
/// Operationally: for each <c>ExternUseDecl</c> the expander calls the
/// resolver, parses the returned Overt source, and splices the resulting
/// declarations into the original module in place of the use directive.
/// Failures (missing target, parse errors in the generated source, no
/// resolver registered for the platform) become module-level diagnostics
/// and the original use declaration is dropped from the expanded module
/// so downstream passes don't see a half-resolved declaration.
///
/// The expander does <b>not</b> walk dotted-name lookups, validate target
/// shapes, or emit anything itself. It owns one concern: turning extern
/// use declarations into the equivalent extern-fn / extern-type / etc.
/// declarations the rest of the pipeline already knows how to handle.
/// </summary>
public static class ExternUseExpander
{
    /// <summary>
    /// Resolver callback contract. Given the platform tag (e.g. <c>"csharp"</c>)
    /// and the target string from the use declaration (e.g. <c>"System.IO.File"</c>),
    /// returns the Overt source that represents the resolved binding, or
    /// <c>null</c> if the target cannot be resolved on this platform. A
    /// resolver returning <c>null</c> causes the expander to emit OV0170;
    /// a resolver that throws is treated identically.
    ///
    /// The returned source must be a complete Overt module, including the
    /// <c>module</c> header line. The expander parses it, plucks out the
    /// declarations, and discards the synthetic module name.
    /// </summary>
    public delegate string? Resolver(string platform, string target);

    /// <summary>
    /// Run the expander over a parsed module. Returns the same module with
    /// every <c>extern "platform" use "..."</c> declaration replaced by the
    /// declarations the resolver supplied for it, plus any diagnostics
    /// produced during resolution.
    /// </summary>
    public static Result Expand(ModuleDecl module, Resolver resolver)
    {
        if (module.Declarations.IsDefaultOrEmpty)
        {
            return new Result(module, ImmutableArray<Diagnostic>.Empty);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var newDecls = ImmutableArray.CreateBuilder<Declaration>(module.Declarations.Length);

        foreach (var decl in module.Declarations)
        {
            if (decl is not ExternUseDecl use)
            {
                newDecls.Add(decl);
                continue;
            }

            var resolved = TryResolve(resolver, use, diagnostics);
            if (resolved is null)
            {
                // Failure already reported via diagnostics. Drop the use
                // declaration so downstream passes (typer, emitter) don't
                // trip on a use directive they don't know how to handle.
                continue;
            }

            foreach (var generated in resolved)
            {
                newDecls.Add(generated);
            }
        }

        var expanded = module with { Declarations = newDecls.ToImmutable() };
        return new Result(expanded, diagnostics.ToImmutable());
    }

    public sealed record Result(
        ModuleDecl Module,
        ImmutableArray<Diagnostic> Diagnostics);

    private static ImmutableArray<Declaration>? TryResolve(
        Resolver resolver,
        ExternUseDecl use,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string? source;
        try
        {
            source = resolver(use.Platform, use.Target);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "OV0171",
                $"resolver for `extern \"{use.Platform}\" use \"{use.Target}\"` threw: {ex.Message}",
                use.Span,
                ImmutableArray<DiagnosticNote>.Empty));
            return null;
        }

        if (source is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                "OV0170",
                $"cannot resolve `extern \"{use.Platform}\" use \"{use.Target}\"`: target not found on this platform",
                use.Span,
                ImmutableArray<DiagnosticNote>.Empty));
            return null;
        }

        var lex = Lexer.Lex(source);
        if (lex.Diagnostics.Length > 0)
        {
            foreach (var d in lex.Diagnostics)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "OV0172",
                    $"resolver returned source with lex error for `extern \"{use.Platform}\" use \"{use.Target}\"`: {d.Message}",
                    use.Span,
                    ImmutableArray<DiagnosticNote>.Empty));
            }
            return null;
        }

        var parse = Parser.Parse(lex.Tokens);
        if (parse.Diagnostics.Length > 0)
        {
            foreach (var d in parse.Diagnostics)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "OV0173",
                    $"resolver returned source with parse error for `extern \"{use.Platform}\" use \"{use.Target}\"`: {d.Message}",
                    use.Span,
                    ImmutableArray<DiagnosticNote>.Empty));
            }
            return null;
        }

        // The generated source is a full module; we want only its
        // declarations. The synthetic module name is irrelevant to the
        // surrounding code.
        return parse.Module.Declarations;
    }
}
