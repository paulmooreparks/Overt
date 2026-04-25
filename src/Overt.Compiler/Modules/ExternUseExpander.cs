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
    /// every <c>extern "platform" use "..."</c> declaration handled, plus
    /// any synthetic modules produced for aliased uses, plus diagnostics.
    ///
    /// Two paths through the expander, by alias presence:
    /// <list type="bullet">
    ///   <item>
    ///     <b>No alias</b>: the resolver-generated declarations are spliced
    ///     directly into the user's module at the position of the original
    ///     <c>extern use</c>. Method names land at top-level scope, mirroring
    ///     C#'s <c>using static</c>.
    ///   </item>
    ///   <item>
    ///     <b>With alias</b>: the resolver-generated source becomes a synthetic
    ///     <see cref="ModuleGraph.LoadedModule"/>, returned via
    ///     <see cref="Result.SyntheticModules"/> so the caller can add it to the
    ///     compilation graph. The original <c>extern use</c> is replaced with a
    ///     <see cref="UseDecl"/> that imports that synthetic module under the
    ///     given alias. Method names land under the alias namespace, mirroring
    ///     C#'s <c>using Alias = ...</c> form.
    ///   </item>
    /// </list>
    /// Failures (missing target, parse errors, etc.) become module-level
    /// diagnostics and the original use declaration is dropped from the
    /// expanded module.
    /// </summary>
    public static Result Expand(ModuleDecl module, Resolver resolver)
    {
        if (module.Declarations.IsDefaultOrEmpty)
        {
            return new Result(module, ImmutableArray<ModuleGraph.LoadedModule>.Empty, ImmutableArray<Diagnostic>.Empty);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var newDecls = ImmutableArray.CreateBuilder<Declaration>(module.Declarations.Length);
        var syntheticModules = ImmutableArray.CreateBuilder<ModuleGraph.LoadedModule>();

        foreach (var decl in module.Declarations)
        {
            if (decl is not ExternUseDecl use)
            {
                newDecls.Add(decl);
                continue;
            }

            var resolution = TryResolveModule(resolver, use, diagnostics);
            if (resolution is null)
            {
                // Failure already reported. Drop the use declaration so
                // downstream passes don't trip on it.
                continue;
            }

            if (use.Alias is null)
            {
                // No-alias path: splice the generated declarations into the
                // user's module at the position of the original `extern use`.
                foreach (var generated in resolution.Value.Module.Ast.Declarations)
                {
                    newDecls.Add(generated);
                }
            }
            else
            {
                // Aliased path: the generated declarations become a synthetic
                // module; replace the `extern use` with a `use ... as alias`
                // that imports it.
                syntheticModules.Add(resolution.Value.Module);
                newDecls.Add(new UseDecl(
                    ModulePath: ImmutableArray.Create(resolution.Value.Module.Name),
                    ImportedSymbols: ImmutableArray<string>.Empty,
                    Alias: use.Alias,
                    Span: use.Span));
            }
        }

        var expanded = module with { Declarations = newDecls.ToImmutable() };
        return new Result(expanded, syntheticModules.ToImmutable(), diagnostics.ToImmutable());
    }

    public sealed record Result(
        ModuleDecl Module,
        ImmutableArray<ModuleGraph.LoadedModule> SyntheticModules,
        ImmutableArray<Diagnostic> Diagnostics);

    private readonly record struct ResolvedModule(
        ModuleGraph.LoadedModule Module);

    private static ResolvedModule? TryResolveModule(
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

        // Build a synthetic LoadedModule from the parse result. The module
        // name comes from the resolver-supplied `module` declaration; the
        // source path is a marker indicating the module did not come from
        // disk (so error reporting and IDE tooling can recognize it).
        var syntheticPath = $"<extern:{use.Platform}:{use.Target}>";
        var loaded = new ModuleGraph.LoadedModule(
            Name: parse.Module.Name,
            SourcePath: syntheticPath,
            Source: source,
            Tokens: lex.Tokens,
            Ast: parse.Module);
        return new ResolvedModule(loaded);
    }
}
