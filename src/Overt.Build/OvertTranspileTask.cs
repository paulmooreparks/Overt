using System.Collections.Immutable;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Overt.Backend.CSharp;
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Modules;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

namespace Overt.Build;

/// <summary>
/// MSBuild task that transpiles <c>.ov</c> source files to C# and exposes
/// the generated paths back to the build so they can be fed to Csc as
/// additional <c>Compile</c> items.
///
/// The task lexes/parses every input source up front, then runs the same
/// orchestration the CLI uses for <c>overt run</c>: <see cref="ExternUseExpander"/>
/// rewrites every <c>extern "csharp" use "..."</c> declaration into either
/// inlined externs (no-alias form) or a synthetic sibling module that the
/// user's module imports under the alias. Each module — synthetic ones
/// included — is then resolved, type-checked with imported symbol types,
/// and emitted to its own <c>.g.cs</c> file. Csc picks up every produced
/// file via the <c>GeneratedFiles</c> output.
///
/// The single-file MSBuild contract from the original task is preserved:
/// each input <c>.ov</c> still produces one <c>&lt;input&gt;.g.cs</c>;
/// synthetic modules land at <c>&lt;target&gt;.synth.g.cs</c> next to it
/// (deterministic name so incremental builds are stable). Cross-file
/// <c>use</c> imports between user-authored <c>.ov</c> files are not yet
/// resolved here — the task assumes each <c>.ov</c> is independent for
/// non-extern purposes.
/// </summary>
public sealed class OvertTranspileTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();

    [Required]
    public string OutputDirectory { get; set; } = "";

    /// <summary>
    /// True when the consuming csproj is <c>OutputType=Exe</c>. When set,
    /// any user-authored <c>.ov</c> that exports a <c>main</c> with a
    /// supported signature gets a sibling <c>&lt;input&gt;.entry.g.cs</c>
    /// emitted: a tiny static class with <c>static int Main(string[])</c>
    /// that delegates to the user's Overt module. The build's `Compile`
    /// item group picks it up alongside the regular transpile output.
    ///
    /// Defaults to false so library projects (the common case) don't
    /// accidentally get spurious entry points if they happen to define
    /// a function called `main`.
    /// </summary>
    public bool EmitEntryPoint { get; set; } = false;

    /// <summary>
    /// `.ov` files from referenced projects that this project's source
    /// imports via <c>use</c>. The task lex/parse/expand-extern/typechecks
    /// each one to make its exports visible to the consumer's resolver,
    /// but does NOT emit C# — the referenced project's own build does
    /// that. The consumer's emit routes calls to imported symbols
    /// through the imported module's namespace, which gets linked at
    /// C# compile time via the <c>ProjectReference</c>'s assembly output.
    ///
    /// Wired in <c>Overt.Build.targets</c> by the consuming project
    /// listing <c>&lt;OvertImportSource Include="..\OtherProject\Foo.ov" /&gt;</c>.
    /// </summary>
    public ITaskItem[] ImportSourceFiles { get; set; } = Array.Empty<ITaskItem>();

    [Output]
    public ITaskItem[] GeneratedFiles { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        if (!Directory.Exists(OutputDirectory))
        {
            Directory.CreateDirectory(OutputDirectory);
        }

        var generated = new List<ITaskItem>(SourceFiles.Length);
        var anyErrors = false;

        // Pre-pass: process imported .ov files (from referenced projects).
        // Each is lex/parse/typecheck'd in isolation to harvest its
        // exports — those become starting state for the user's source so
        // `use ParksComputing.SemVer.{parse, ...}` resolves natively. We
        // never emit C# for these (the referenced project's own build
        // emits them); we only need their symbols visible here.
        var importedExportsByModule = new Dictionary<string, ImmutableDictionary<string, Symbol>>(
            StringComparer.Ordinal);
        var importedSymbolTypesByModule = new Dictionary<string, ImmutableDictionary<Symbol, TypeRef>>(
            StringComparer.Ordinal);
        // Dedupe imports by full path: auto-discovery from ProjectReference
        // and a manual <OvertImportSource> for the same file would otherwise
        // parse and register the module twice.
        var seenImportPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ImportSourceFiles)
        {
            var importPath = item.GetMetadata("FullPath");
            if (string.IsNullOrEmpty(importPath))
            {
                importPath = item.ItemSpec;
            }
            if (!seenImportPaths.Add(Path.GetFullPath(importPath)))
            {
                continue;
            }
            var importResult = CompileFile(importPath, importedExportsByModule, importedSymbolTypesByModule);
            foreach (var d in importResult.Diagnostics)
            {
                ReportDiagnostic(d, importPath);
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    anyErrors = true;
                }
            }
            if (importResult.HasErrors)
            {
                continue;
            }
            foreach (var mod in importResult.Modules)
            {
                if (mod.IsSynthetic)
                {
                    continue;
                }
                importedExportsByModule[mod.Name] = CollectTopLevelExports(mod.Ast);
                importedSymbolTypesByModule[mod.Name] = importResult.TypeChecks[mod.Name].SymbolTypes;
            }
        }

        if (anyErrors)
        {
            // Imported source has errors — abort before processing user
            // source against an inconsistent symbol set.
            GeneratedFiles = Array.Empty<ITaskItem>();
            return false;
        }

        // Each user input .ov is processed independently — synthetic
        // modules from `extern "csharp" use "..." as alias` get expanded
        // and type-checked alongside, with the imported-module exports
        // available as a starting point for the user's resolver.
        foreach (var item in SourceFiles)
        {
            var sourcePath = item.GetMetadata("FullPath");
            if (string.IsNullOrEmpty(sourcePath))
            {
                sourcePath = item.ItemSpec;
            }

            var fileResult = CompileFile(sourcePath, importedExportsByModule, importedSymbolTypesByModule);
            foreach (var d in fileResult.Diagnostics)
            {
                ReportDiagnostic(d, sourcePath);
                if (d.Severity == DiagnosticSeverity.Error)
                {
                    anyErrors = true;
                }
            }

            if (fileResult.HasErrors)
            {
                continue; // skip emission; errors are the user's fix
            }

            // Emit C# for every module produced — the user's plus any
            // synthetic siblings created by extern-use expansion. The
            // user module keeps its `<input>.g.cs` name; synthetic
            // modules use a deterministic suffixed name so MSBuild
            // incremental builds don't churn.
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            for (int i = 0; i < fileResult.Modules.Length; i++)
            {
                var mod = fileResult.Modules[i];
                var typed = fileResult.TypeChecks[mod.Name];
                var resolved = fileResult.Resolutions[mod.Name];
                var csharp = CSharpEmitter.Emit(mod.Ast, typed, resolved, sourcePath);

                var outName = mod.IsSynthetic
                    ? $"{baseName}.synth.{SafeName(mod.Name)}.g.cs"
                    : $"{baseName}.g.cs";
                var outputPath = Path.Combine(OutputDirectory, outName);
                File.WriteAllText(outputPath, csharp);
                generated.Add(new TaskItem(outputPath));

                // Synthesize an executable entry point if requested and
                // this module is the user's (not a synthetic extern-use
                // sibling) and exports a recognizable `main`.
                if (EmitEntryPoint && !mod.IsSynthetic
                    && TryBuildEntryPoint(mod.Ast, sourcePath) is { } entryCs)
                {
                    var entryPath = Path.Combine(OutputDirectory, $"{baseName}.entry.g.cs");
                    File.WriteAllText(entryPath, entryCs);
                    generated.Add(new TaskItem(entryPath));
                }
            }
        }

        GeneratedFiles = generated.ToArray();
        return !anyErrors;
    }

    /// <summary>
    /// Build the synthesized entry-point source for a module that
    /// exports a recognized <c>main</c>. Two shapes are supported:
    /// <list type="bullet">
    ///   <item><c>fn main() ... -&gt; Int</c></item>
    ///   <item><c>fn main(args: List&lt;String&gt;) ... -&gt; Int</c></item>
    /// </list>
    /// Effect rows are irrelevant — the call site doesn't observe them.
    /// Async/await isn't supported here yet; if main awaits, the emitted
    /// signature is <c>Task&lt;int&gt;</c> and Csc will fail to bind
    /// the int-returning Main below. Returns null when no qualifying
    /// `main` is present.
    /// </summary>
    private static string? TryBuildEntryPoint(ModuleDecl module, string sourcePath)
    {
        var mainFn = module.Declarations
            .OfType<FunctionDecl>()
            .FirstOrDefault(f => f.Name == "main" && IsValidMainSignature(f));
        if (mainFn is null)
        {
            return null;
        }

        var ns = ToEmittedNamespace(module.Name);
        var hasArgs = mainFn.Parameters.Length == 1;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"// Synthesized executable entry point for module `{module.Name}`.");
        sb.AppendLine($"// Source: {sourcePath}");
        sb.AppendLine("// DO NOT EDIT THIS FILE. Edits are overwritten on every build.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("internal static class OvertProgram");
        sb.AppendLine("{");
        sb.AppendLine("    private static int Main(string[] args)");
        sb.AppendLine("    {");
        if (hasArgs)
        {
            sb.AppendLine("        var argList = new global::Overt.Runtime.List<string>(");
            sb.AppendLine("            global::System.Collections.Immutable.ImmutableArray.Create(args));");
            sb.AppendLine($"        return global::{ns}.Module.main(argList);");
        }
        else
        {
            sb.AppendLine($"        return global::{ns}.Module.main();");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>True when <paramref name="fn"/>'s shape is one of the
    /// supported <c>main</c> entry forms. Rejects anything more exotic
    /// (refinement-typed args, multi-arg, non-Int return) early so the
    /// generated entry stays a one-liner.</summary>
    private static bool IsValidMainSignature(FunctionDecl fn)
    {
        if (fn.ReturnType is not NamedType { Name: "Int", TypeArguments.Length: 0 })
        {
            return false;
        }
        if (fn.Parameters.Length == 0)
        {
            return true;
        }
        if (fn.Parameters.Length == 1
            && fn.Parameters[0].Type is NamedType { Name: "List", TypeArguments.Length: 1 } listT
            && listT.TypeArguments[0] is NamedType { Name: "String", TypeArguments.Length: 0 })
        {
            return true;
        }
        return false;
    }

    /// <summary>Mirror of CSharpEmitter.ToEmittedNamespace — kept local
    /// to avoid widening the back end's public API for one caller. Dotted
    /// module names emit verbatim (the author chose a fully-qualified
    /// namespace); single-identifier names land under
    /// <c>Overt.Generated.</c>.</summary>
    private static string ToEmittedNamespace(string moduleName)
    {
        var pascalParts = moduleName.Split('.').Select(PascalCase);
        var joined = string.Join(".", pascalParts);
        return moduleName.Contains('.', StringComparison.Ordinal)
            ? joined
            : $"Overt.Generated.{joined}";
    }

    private static string PascalCase(string segment)
    {
        if (segment.Length == 0)
        {
            return segment;
        }
        if (char.IsUpper(segment[0]))
        {
            return segment;
        }
        return char.ToUpperInvariant(segment[0]) + segment[1..];
    }

    private readonly record struct CompiledFile(
        ImmutableArray<CompiledModule> Modules,
        Dictionary<string, TypeCheckResult> TypeChecks,
        Dictionary<string, ResolutionResult> Resolutions,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public bool HasErrors =>
            Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    }

    private readonly record struct CompiledModule(
        string Name,
        ModuleDecl Ast,
        bool IsSynthetic);

    /// <summary>Lex/parse the source, expand extern uses, then resolve+
    /// type-check the user module and every synthetic module produced
    /// for aliased imports. Returns enough state for the caller to emit
    /// every module's C# in one pass.
    /// <para>
    /// <paramref name="seedExports"/> and <paramref name="seedSymbolTypes"/>
    /// pre-populate the resolver/typer's cross-module state — used by
    /// the cross-project import path so a consumer's <c>use Foo.{bar}</c>
    /// resolves to the symbols of an already-processed library module.
    /// </para>
    /// </summary>
    private static CompiledFile CompileFile(
        string sourcePath,
        Dictionary<string, ImmutableDictionary<string, Symbol>>? seedExports = null,
        Dictionary<string, ImmutableDictionary<Symbol, TypeRef>>? seedSymbolTypes = null)
    {
        var source = File.ReadAllText(sourcePath);
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);

        var diagnostics = lex.Diagnostics.AddRange(parse.Diagnostics);

        // Expand extern uses. Aliased uses produce synthetic modules
        // that need their own resolve + typecheck pass before the user
        // module's typer can see their exports.
        var expansion = ExternUseExpander.Expand(parse.Module, CSharpExternUseResolver.Resolve);
        diagnostics = diagnostics.AddRange(expansion.Diagnostics);

        var modules = ImmutableArray.CreateBuilder<CompiledModule>();
        foreach (var synthetic in expansion.SyntheticModules)
        {
            modules.Add(new CompiledModule(synthetic.Name, synthetic.Ast, IsSynthetic: true));
        }
        modules.Add(new CompiledModule(expansion.Module.Name, expansion.Module, IsSynthetic: false));

        // Topological order is "synthetic before user" — synthetic
        // modules never import the user's module, only the reverse.
        var typeChecks = new Dictionary<string, TypeCheckResult>(StringComparer.Ordinal);
        var resolutions = new Dictionary<string, ResolutionResult>(StringComparer.Ordinal);
        var exportedSymbols = seedExports is null
            ? new Dictionary<string, ImmutableDictionary<string, Symbol>>(StringComparer.Ordinal)
            : new Dictionary<string, ImmutableDictionary<string, Symbol>>(seedExports, StringComparer.Ordinal);
        var symbolTypesByModule = seedSymbolTypes is null
            ? new Dictionary<string, ImmutableDictionary<Symbol, TypeRef>>(StringComparer.Ordinal)
            : new Dictionary<string, ImmutableDictionary<Symbol, TypeRef>>(seedSymbolTypes, StringComparer.Ordinal);

        foreach (var mod in modules)
        {
            var importable = exportedSymbols.ToImmutableDictionary(StringComparer.Ordinal);
            var resolved = NameResolver.Resolve(mod.Ast, importable);
            diagnostics = diagnostics.AddRange(resolved.Diagnostics);

            var importedTypes = CollectImportedSymbolTypes(
                mod.Ast, exportedSymbols, symbolTypesByModule);
            var typed = TypeChecker.Check(mod.Ast, resolved, importedTypes);
            diagnostics = diagnostics.AddRange(typed.Diagnostics);

            typeChecks[mod.Name] = typed;
            resolutions[mod.Name] = resolved;
            exportedSymbols[mod.Name] = CollectTopLevelExports(mod.Ast);
            symbolTypesByModule[mod.Name] = typed.SymbolTypes;
        }

        return new CompiledFile(modules.ToImmutable(), typeChecks, resolutions, diagnostics);
    }

    /// <summary>Mirrors the CLI's <c>CollectImportedSymbolTypes</c>: for
    /// each <c>use</c> declaration in <paramref name="module"/>, gather
    /// the typed symbols that should be visible to its TypeChecker.
    /// Aliased uses pull every export so dotted access through the alias
    /// types correctly; selective uses pull only the named symbols.
    /// </summary>
    private static ImmutableDictionary<Symbol, TypeRef> CollectImportedSymbolTypes(
        ModuleDecl module,
        Dictionary<string, ImmutableDictionary<string, Symbol>> exportedSymbols,
        Dictionary<string, ImmutableDictionary<Symbol, TypeRef>> symbolTypesByModule)
    {
        var builder = ImmutableDictionary.CreateBuilder<Symbol, TypeRef>();
        foreach (var use in module.Declarations.OfType<UseDecl>())
        {
            if (!exportedSymbols.TryGetValue(use.ModuleName, out var exports))
            {
                continue;
            }
            if (!symbolTypesByModule.TryGetValue(use.ModuleName, out var exportedTypes))
            {
                continue;
            }

            if (use.Alias is not null)
            {
                foreach (var sym in exports.Values)
                {
                    if (exportedTypes.TryGetValue(sym, out var type))
                    {
                        builder[sym] = type;
                    }
                }
            }
            else
            {
                foreach (var name in use.ImportedSymbols)
                {
                    if (!exports.TryGetValue(name, out var sym))
                    {
                        continue;
                    }
                    if (exportedTypes.TryGetValue(sym, out var type))
                    {
                        builder[sym] = type;
                    }
                }
            }
        }
        return builder.ToImmutable();
    }

    /// <summary>A module's top-level exports — fns, externs, records,
    /// enums, type aliases. Indexed by simple name for <c>use</c>
    /// lookup. Mirrors the CLI helper of the same name.</summary>
    private static ImmutableDictionary<string, Symbol> CollectTopLevelExports(ModuleDecl module)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, Symbol>(StringComparer.Ordinal);
        foreach (var decl in module.Declarations)
        {
            var sym = decl switch
            {
                FunctionDecl f => new Symbol(SymbolKind.Function, f.Name, f.Span, f),
                ExternDecl x => new Symbol(SymbolKind.Extern, x.Name, x.Span, x),
                RecordDecl r => new Symbol(SymbolKind.Record, r.Name, r.Span, r),
                EnumDecl e => new Symbol(SymbolKind.Enum, e.Name, e.Span, e),
                TypeAliasDecl t => new Symbol(SymbolKind.TypeAlias, t.Name, t.Span, t),
                ExternTypeDecl xt => new Symbol(SymbolKind.Record, xt.Name, xt.Span, xt),
                _ => (Symbol?)null,
            };
            if (sym is not null)
            {
                builder[sym.Name] = sym;
            }
        }
        return builder.ToImmutable();
    }

    /// <summary>Sanitize a module name for use as a filename component
    /// (synthetic module names contain dots and special chars).</summary>
    private static string SafeName(string moduleName)
    {
        var chars = moduleName.Select(c =>
            char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        return new string(chars);
    }

    private void ReportDiagnostic(Diagnostic d, string sourcePath)
    {
        var line = d.Span.Start.Line;
        var col = d.Span.Start.Column;
        var endLine = d.Span.End.Line;
        var endCol = d.Span.End.Column;

        if (d.Severity == DiagnosticSeverity.Error)
        {
            Log.LogError(
                subcategory: null,
                errorCode: d.Code,
                helpKeyword: null,
                file: sourcePath,
                lineNumber: line,
                columnNumber: col,
                endLineNumber: endLine,
                endColumnNumber: endCol,
                message: d.Message);
        }
        else
        {
            Log.LogWarning(
                subcategory: null,
                warningCode: d.Code,
                helpKeyword: null,
                file: sourcePath,
                lineNumber: line,
                columnNumber: col,
                endLineNumber: endLine,
                endColumnNumber: endCol,
                message: d.Message);
        }
    }
}
