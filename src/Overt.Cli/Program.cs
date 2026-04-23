using System.Collections.Immutable;
using System.Reflection;
using Overt.Backend.CSharp;
// BindGenerator from Overt.Backend.CSharp — the C#-specific binding tool.
// A future Go or C++ backend would ship its own binding generator, dispatched
// on the `--platform` (or equivalent) flag in the `bind` subcommand.
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Modules;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

// Roslyn types collide with Overt's `Diagnostic` and `SyntaxNode`; import them
// under aliased namespaces and reference via the aliases in RunProgram.
using RoslynCore = global::Microsoft.CodeAnalysis;
using RoslynCSharp = global::Microsoft.CodeAnalysis.CSharp;

// CLI contract — stable for external integrators (Compiler Explorer, CI, agent tools):
//
//   overt --emit=<stage> <file.ov>    emit compiler output for one pipeline stage
//   overt run <file.ov>               transpile, compile, and execute
//   overt --version                   single-line version string
//   overt --help | -h                 usage
//
// Emit stages: tokens, ast, csharp, go. Backends not yet wired produce a clear
// "not yet implemented" diagnostic and non-zero exit.
//
// Output:
//   - Compiler artifact on stdout, deterministic, no color, no timestamps.
//   - Diagnostics on stderr, line-prefixed: `path:line:col: severity: CODE: message`.
//   - Exit code 0 on clean compile, 1 on diagnostic errors, 2 on usage errors.
//
// See docs/tooling/godbolt.md for the integration criteria this CLI honors.

// Subcommand dispatch: `overt run file.ov` and `overt fmt file.ov` run ahead
// of the --emit flag parser. Everything else (including --help/--version)
// falls through to the flag parser.
if (args.Length >= 1 && args[0] == "run")
{
    return Cli.RunProgram(args.AsSpan(1).ToArray());
}
if (args.Length >= 1 && args[0] == "fmt")
{
    return Cli.FormatProgram(args.AsSpan(1).ToArray());
}
if (args.Length >= 1 && args[0] == "bind")
{
    return Cli.BindProgram(args.AsSpan(1).ToArray());
}

return Cli.Run(args);

static class Cli
{
    const string Version = "overt 0.1.0-dev";

    const string Usage =
        """
        usage: overt --emit=<stage> <file.ov>
               overt run <file.ov>
               overt fmt [--write] <file.ov>
               overt bind --type <FullName> [--module <name>] [--output <file>]

        commands:
          run              transpile, compile, and execute <file.ov>. Exits 0 on
                           Ok, 1 on compile errors or Err, 2 on usage errors.
          fmt              format <file.ov> to canonical form. Writes to stdout
                           unless --write is passed, which updates in place.
          bind             reflect on a .NET type and emit an Overt facade
                           (extern declarations) for its public static methods.
                           Writes to stdout unless --output is given.

        options:
          --emit=<stage>   required for emit mode. one of:
                           tokens, ast, resolved, typed, csharp, go
          --no-color       accepted for tool compatibility (output is never colored)
          --version        print version and exit
          --help, -h       print this message and exit

        diagnostics are printed to stderr in the form
            path:line:col: severity: CODE: message
        """;

    public static int Run(string[] args)
    {
        string? emit = null;
        string? inputFile = null;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--version":
                    Console.Out.WriteLine(Version);
                    return 0;
                case "--help":
                case "-h":
                    Console.Out.WriteLine(Usage);
                    return 0;
                case "--no-color":
                    continue;
                default:
                    if (arg.StartsWith("--emit=", StringComparison.Ordinal))
                    {
                        emit = arg["--emit=".Length..];
                        continue;
                    }
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"overt: unknown option '{arg}'");
                        return 2;
                    }
                    if (inputFile is not null)
                    {
                        Console.Error.WriteLine("overt: multiple input files not supported");
                        return 2;
                    }
                    inputFile = arg;
                    break;
            }
        }

        if (emit is null)
        {
            Console.Error.WriteLine("overt: --emit=<stage> is required");
            return 2;
        }
        if (inputFile is null)
        {
            Console.Error.WriteLine("overt: missing input file");
            return 2;
        }
        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"overt: file not found: {inputFile}");
            return 1;
        }

        var source = File.ReadAllText(inputFile);

        return emit switch
        {
            "tokens" => EmitTokens(source, inputFile),
            "ast" => EmitAst(source, inputFile),
            "resolved" => EmitResolved(source, inputFile),
            "typed" => EmitTyped(source, inputFile),
            "csharp" => EmitCSharp(source, inputFile),
            "go" => NotYetImplemented("go"),
            _ => UnknownStage(emit),
        };
    }

    static int EmitTyped(string source, string inputFile)
    {
        // Multi-module-aware: resolve the graph so imports type-check, then
        // emit the entry module's type annotations. Other modules' types are
        // available via the shared pipeline but aren't printed here — users
        // running `--emit=typed` want to inspect the file they named.
        var compiled = CompileGraph(inputFile);
        var entry = compiled.Modules[^1];
        var typed = compiled.TypeChecks[entry.Name];

        foreach (var (sym, type) in typed.SymbolTypes
            .OrderBy(kv => kv.Key.DeclarationSpan.Start.Line)
            .ThenBy(kv => kv.Key.DeclarationSpan.Start.Column))
        {
            Console.Out.WriteLine(
                $"{sym.DeclarationSpan.Start} decl {sym.Kind} {sym.Name} : {type.Display}");
        }
        foreach (var (span, type) in typed.ExpressionTypes
            .OrderBy(kv => kv.Key.Start.Line)
            .ThenBy(kv => kv.Key.Start.Column))
        {
            Console.Out.WriteLine($"{span.Start} expr : {type.Display}");
        }

        return WriteDiagnostics(inputFile, compiled.Diagnostics);
    }

    // `overt run <file.ov>` — transpile, compile the resulting C# in-memory
    // against Overt.Runtime, load the assembly, and invoke `Module.main()`.
    // Exit codes:
    //   0  — main returned Ok
    //   1  — Overt compile errors, Roslyn compile errors, or main returned Err
    //   2  — usage errors
    public static int RunProgram(string[] args)
    {
        string? inputFile = null;
        foreach (var arg in args)
        {
            if (arg is "--help" or "-h")
            {
                Console.Out.WriteLine("usage: overt run <file.ov>");
                return 0;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"overt run: unknown option '{arg}'");
                return 2;
            }
            if (inputFile is not null)
            {
                Console.Error.WriteLine("overt run: multiple input files not supported");
                return 2;
            }
            inputFile = arg;
        }
        if (inputFile is null)
        {
            Console.Error.WriteLine("overt run: missing input file");
            return 2;
        }
        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"overt run: file not found: {inputFile}");
            return 1;
        }

        // Resolve + type-check the module graph through the shared pipeline,
        // which applies stdlib auto-discovery (DiscoverSearchDirs).
        var compiled = CompileGraph(inputFile);
        if (compiled.Modules.Length == 0)
        {
            if (WriteDiagnostics(inputFile, compiled.Diagnostics) != 0) return 1;
            Console.Error.WriteLine("overt run: module graph is empty");
            return 1;
        }
        if (WriteDiagnostics(inputFile, compiled.Diagnostics) != 0) return 1;

        // Emit C# for every module in the graph. The entry module's
        // assembly name matches the input file so stack traces point there.
        var trees = new List<RoslynCore.SyntaxTree>();
        foreach (var module in compiled.Modules)
        {
            var csharp = CSharpEmitter.Emit(
                module.Ast,
                compiled.TypeChecks[module.Name],
                module.SourcePath);
            var tree = RoslynCSharp.CSharpSyntaxTree.ParseText(
                csharp, new RoslynCSharp.CSharpParseOptions(RoslynCSharp.LanguageVersion.Latest));
            trees.Add(tree);
        }
        var refs = ImmutableArray.CreateBuilder<RoslynCore.MetadataReference>();
        var runtimeAssembly = typeof(global::Overt.Runtime.Unit).Assembly;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            if (string.IsNullOrEmpty(asm.Location)) continue;
            refs.Add(RoslynCore.MetadataReference.CreateFromFile(asm.Location));
        }
        if (!string.IsNullOrEmpty(runtimeAssembly.Location)
            && !refs.Any(r => r.Display?.Contains("Overt.Runtime") == true))
        {
            refs.Add(RoslynCore.MetadataReference.CreateFromFile(runtimeAssembly.Location));
        }

        var compilation = RoslynCSharp.CSharpCompilation.Create(
            assemblyName: Path.GetFileNameWithoutExtension(inputFile),
            syntaxTrees: trees,
            references: refs.ToImmutable(),
            options: new RoslynCSharp.CSharpCompilationOptions(
                RoslynCore.OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: RoslynCore.NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == RoslynCore.DiagnosticSeverity.Error))
            {
                Console.Error.WriteLine($"overt run: generated C# failed to compile: {d.GetMessage()}");
            }
            return 1;
        }
        ms.Position = 0;
        var asmLoaded = Assembly.Load(ms.ToArray());

        // Locate Module.main and invoke. Every Overt program's entry point lowers
        // to `Overt.Generated.<ModuleName>.Module.main`; we don't constrain on
        // full name in case the user renames the module.
        var moduleType = asmLoaded.GetTypes().FirstOrDefault(t => t.Name == "Module");
        if (moduleType is null)
        {
            Console.Error.WriteLine("overt run: no `Module` type in emitted assembly");
            return 1;
        }
        var mainMethod = moduleType.GetMethod("main", BindingFlags.Public | BindingFlags.Static);
        if (mainMethod is null)
        {
            Console.Error.WriteLine("overt run: module has no `main` function");
            return 1;
        }
        if (mainMethod.GetParameters().Length != 0)
        {
            Console.Error.WriteLine("overt run: `main` must take no arguments");
            return 1;
        }

        object? result;
        try
        {
            result = mainMethod.Invoke(null, null);
        }
        catch (TargetInvocationException tie)
        {
            Console.Error.WriteLine($"overt run: unhandled exception: {tie.InnerException?.Message ?? tie.Message}");
            return 1;
        }

        // If main returns Result<_, _>, check IsOk. Ok → exit 0. Err → print + exit 1.
        if (result is null) return 0;
        var isOkProp = result.GetType().GetProperty("IsOk");
        if (isOkProp is null) return 0;

        if ((bool)isOkProp.GetValue(result)!)
        {
            return 0;
        }
        var errProp = result.GetType().GetProperty("Error");
        var err = errProp?.GetValue(result);
        Console.Error.WriteLine($"overt run: main returned Err: {err}");
        return 1;
    }

    // -------------------------------------------- compile-through-graph

    public sealed record CompiledGraph(
        ImmutableArray<ModuleGraph.LoadedModule> Modules,
        Dictionary<string, ResolutionResult> Resolutions,
        Dictionary<string, TypeCheckResult> TypeChecks,
        ImmutableArray<Diagnostic> Diagnostics);

    /// <summary>Shared compilation pipeline that both `run` and the stage-emit
    /// modes (`--emit=csharp` / `--emit=typed` / etc.) call into. Resolves the
    /// module graph, topologically resolves + type-checks each module, and
    /// returns everything needed to emit any stage of output.
    /// </summary>
    public static CompiledGraph CompileGraph(string entryFile)
    {
        var searchDirs = DiscoverSearchDirs();
        var graph = ModuleGraph.Resolve(entryFile, searchDirs);
        var allDiagnostics = graph.Diagnostics;

        var moduleResolutions = new Dictionary<string, ResolutionResult>(StringComparer.Ordinal);
        var moduleTypes = new Dictionary<string, TypeCheckResult>(StringComparer.Ordinal);
        var exportedSymbols = new Dictionary<string, ImmutableDictionary<string, Symbol>>(
            StringComparer.Ordinal);
        var symbolTypesByModule = new Dictionary<string, ImmutableDictionary<Symbol, TypeRef>>(
            StringComparer.Ordinal);

        foreach (var mod in graph.Modules)
        {
            var importable = exportedSymbols.ToImmutableDictionary(StringComparer.Ordinal);
            var resolved = NameResolver.Resolve(mod.Ast, importable);
            allDiagnostics = allDiagnostics.AddRange(resolved.Diagnostics);
            moduleResolutions[mod.Name] = resolved;

            var importedTypes = CollectImportedSymbolTypes(mod.Ast, exportedSymbols, symbolTypesByModule);
            var typed = TypeChecker.Check(mod.Ast, resolved, importedTypes);
            allDiagnostics = allDiagnostics.AddRange(typed.Diagnostics);
            moduleTypes[mod.Name] = typed;

            exportedSymbols[mod.Name] = CollectTopLevelExports(mod.Ast);
            symbolTypesByModule[mod.Name] = typed.SymbolTypes;
        }

        return new CompiledGraph(graph.Modules, moduleResolutions, moduleTypes, allDiagnostics);
    }

    // ------------------------------------------- search-path discovery

    /// <summary>
    /// Locate the search directories that <see cref="ModuleGraph.Resolve"/>
    /// will walk for <c>use</c> targets.
    ///
    /// The stdlib is a module tree rooted at <c>stdlib.*</c>; a file
    /// <c>stdlib/csharp/system/io/path.ov</c> declares <c>module stdlib.csharp.system.io.path</c>.
    /// Because the first segment of the import path is <c>stdlib</c>, the
    /// search dir must be the PARENT of the <c>stdlib</c> directory, not the
    /// directory itself — otherwise a lookup for <c>stdlib/system/...</c>
    /// would double up.
    ///
    /// Order:
    /// <list type="number">
    ///   <item>The entry file's own directory (applied by ModuleGraph itself;
    ///     doesn't need to appear here).</item>
    ///   <item><c>$OVERT_STDLIB</c> if set — an explicit override pointing at
    ///     the stdlib root (the directory containing <c>stdlib/</c>).</item>
    ///   <item>A directory walking up from the running executable, looking
    ///     for any ancestor that contains <c>stdlib/</c>. Handles both
    ///     installed (<c>bin/stdlib</c> next to <c>overt.exe</c>) and dev
    ///     (<c>repo/stdlib/</c>) layouts without further configuration.</item>
    /// </list>
    /// </summary>
    static ImmutableArray<string> DiscoverSearchDirs()
    {
        var dirs = ImmutableArray.CreateBuilder<string>();

        var envStdlib = Environment.GetEnvironmentVariable("OVERT_STDLIB");
        if (!string.IsNullOrWhiteSpace(envStdlib) && Directory.Exists(envStdlib))
        {
            // Accept either "path-to-stdlib" or "path-to-stdlib-parent" here.
            // If the supplied dir is itself named `stdlib`, use its parent;
            // otherwise assume the user already pointed at a parent.
            var full = Path.GetFullPath(envStdlib);
            var asParent = string.Equals(
                Path.GetFileName(full), "stdlib", StringComparison.Ordinal)
                ? Path.GetDirectoryName(full)
                : full;
            if (!string.IsNullOrEmpty(asParent) && Directory.Exists(asParent))
            {
                dirs.Add(asParent);
            }
        }

        var exeDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(exeDir))
        {
            var cursor = new DirectoryInfo(exeDir);
            for (var depth = 0; depth < 8 && cursor is not null; depth++)
            {
                if (Directory.Exists(Path.Combine(cursor.FullName, "stdlib")))
                {
                    var full = Path.GetFullPath(cursor.FullName);
                    if (!dirs.Contains(full)) dirs.Add(full);
                }
                cursor = cursor.Parent;
            }
        }

        return dirs.ToImmutable();
    }

    // ------------------------------------------- multi-module helpers (run)

    /// <summary>For a module <paramref name="module"/>, gather the typed
    /// symbol map for every symbol it imports via <c>use</c> from already-
    /// compiled modules. Downstream TypeChecker uses this so a call through
    /// an imported symbol sees a real signature, not UnknownType.</summary>
    static ImmutableDictionary<Symbol, TypeRef> CollectImportedSymbolTypes(
        ModuleDecl module,
        Dictionary<string, ImmutableDictionary<string, Symbol>> exportedSymbols,
        Dictionary<string, ImmutableDictionary<Symbol, TypeRef>> symbolTypesByModule)
    {
        var builder = ImmutableDictionary.CreateBuilder<Symbol, TypeRef>();
        foreach (var use in module.Declarations.OfType<UseDecl>())
        {
            if (!exportedSymbols.TryGetValue(use.ModuleName, out var exports)) continue;
            if (!symbolTypesByModule.TryGetValue(use.ModuleName, out var exportedTypes)) continue;
            foreach (var name in use.ImportedSymbols)
            {
                if (!exports.TryGetValue(name, out var sym)) continue;
                if (exportedTypes.TryGetValue(sym, out var type))
                {
                    builder[sym] = type;
                }
            }
        }
        return builder.ToImmutable();
    }

    /// <summary>A module's top-level exports — fns, externs, records, enums,
    /// type aliases. Indexed by simple name for <c>use</c> lookup.</summary>
    static ImmutableDictionary<string, Symbol> CollectTopLevelExports(ModuleDecl module)
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
            if (sym is not null) builder[sym.Name] = sym;
        }
        return builder.ToImmutable();
    }

    // `overt fmt [--write] <file.ov>` — format the file. Default writes to
    // stdout so `overt fmt foo.ov | diff foo.ov -` works; pass --write to
    // update the file in place. Exit codes:
    //   0  — clean format (or file already formatted with --write)
    //   1  — lex or parse errors (cannot format a malformed file)
    //   2  — usage errors
    public static int FormatProgram(string[] args)
    {
        string? inputFile = null;
        var write = false;
        foreach (var arg in args)
        {
            if (arg is "--help" or "-h")
            {
                Console.Out.WriteLine("usage: overt fmt [--write] <file.ov>");
                return 0;
            }
            if (arg == "--write" || arg == "-w") { write = true; continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"overt fmt: unknown option '{arg}'");
                return 2;
            }
            if (inputFile is not null)
            {
                Console.Error.WriteLine("overt fmt: multiple input files not supported");
                return 2;
            }
            inputFile = arg;
        }
        if (inputFile is null)
        {
            Console.Error.WriteLine("overt fmt: missing input file");
            return 2;
        }
        if (!File.Exists(inputFile))
        {
            Console.Error.WriteLine($"overt fmt: file not found: {inputFile}");
            return 1;
        }

        var source = File.ReadAllText(inputFile);
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);

        var diagnostics = lex.Diagnostics.AddRange(parse.Diagnostics);
        if (WriteDiagnostics(inputFile, diagnostics) != 0)
        {
            Console.Error.WriteLine("overt fmt: refusing to format a file with lex/parse errors");
            return 1;
        }

        var formatted = Formatter.Format(parse.Module, lex.Tokens);
        if (write)
        {
            File.WriteAllText(inputFile, formatted);
        }
        else
        {
            Console.Out.Write(formatted);
        }
        return 0;
    }

    // `overt bind --type <FullName> [--module <name>] [--output <file>]`
    //   — generate an Overt facade for a .NET type via reflection. Writes to
    //   stdout unless --output is given.
    public static int BindProgram(string[] args)
    {
        string? typeName = null;
        string? moduleName = null;
        string? outputPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help" or "-h":
                    Console.Out.WriteLine(
                        "usage: overt bind --type <FullName> [--module <name>] [--output <file>]");
                    return 0;
                case "--type":
                    if (++i >= args.Length) { Console.Error.WriteLine("overt bind: --type needs a value"); return 2; }
                    typeName = args[i];
                    break;
                case "--module":
                    if (++i >= args.Length) { Console.Error.WriteLine("overt bind: --module needs a value"); return 2; }
                    moduleName = args[i];
                    break;
                case "--output" or "-o":
                    if (++i >= args.Length) { Console.Error.WriteLine("overt bind: --output needs a value"); return 2; }
                    outputPath = args[i];
                    break;
                default:
                    Console.Error.WriteLine($"overt bind: unknown argument '{arg}'");
                    return 2;
            }
        }
        if (typeName is null)
        {
            Console.Error.WriteLine("overt bind: --type <FullName> is required");
            return 2;
        }

        // Resolve via the currently-loaded assemblies. For BCL types this is
        // fine; for custom assemblies the caller would need to pre-load them
        // (a future --assembly flag) — out of MVP scope.
        Type? targetType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            targetType = asm.GetType(typeName, throwOnError: false);
            if (targetType is not null) break;
        }
        if (targetType is null)
        {
            // Try to load via Type.GetType's assembly-qualified resolution.
            targetType = Type.GetType(typeName, throwOnError: false);
        }
        if (targetType is null)
        {
            Console.Error.WriteLine(
                $"overt bind: type '{typeName}' not found in loaded assemblies");
            return 1;
        }

        // Default module name: last segment of the type's full name, lower-cased.
        moduleName ??= (targetType.Name).ToLowerInvariant();

        var overtSource = BindGenerator.Generate(moduleName, targetType);
        if (outputPath is not null)
        {
            File.WriteAllText(outputPath, overtSource);
            Console.Error.WriteLine($"overt bind: wrote {outputPath}");
        }
        else
        {
            Console.Out.Write(overtSource);
        }
        return 0;
    }

    static int EmitCSharp(string source, string inputFile)
    {
        // Use the shared multi-module pipeline so that if this file has `use`
        // declarations, its imports are resolved and type-checked before
        // emitting. Only the entry module's C# is printed — the consumer of
        // `--emit=csharp` wants to inspect the main file, and downstream
        // modules live in parallel files anyway.
        var compiled = CompileGraph(inputFile);
        var entry = compiled.Modules[^1]; // topologically last = the entry file
        var typed = compiled.TypeChecks[entry.Name];

        var sourcePath = Path.GetFullPath(inputFile);
        var csharp = CSharpEmitter.Emit(entry.Ast, typed, sourcePath);
        Console.Out.Write(csharp);

        var combined = compiled.Diagnostics;
        return WriteDiagnostics(inputFile, combined);
    }

    static int EmitTokens(string source, string inputFile)
    {
        var result = Lexer.Lex(source);
        foreach (var token in result.Tokens)
        {
            Console.Out.WriteLine(token);
        }
        return WriteDiagnostics(inputFile, result.Diagnostics);
    }

    static int EmitAst(string source, string inputFile)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);

        AstPrinter.Print(parse.Module, Console.Out);

        var combined = lex.Diagnostics.AddRange(parse.Diagnostics);
        return WriteDiagnostics(inputFile, combined);
    }

    static int EmitResolved(string source, string inputFile)
    {
        // Multi-module-aware: the resolver gets its imports threaded through
        // so cross-module references resolve in the printout.
        var compiled = CompileGraph(inputFile);
        var entry = compiled.Modules[^1];
        var resolved = compiled.Resolutions[entry.Name];

        foreach (var (span, sym) in resolved.Resolutions
            .OrderBy(kv => kv.Key.Start.Line)
            .ThenBy(kv => kv.Key.Start.Column))
        {
            Console.Out.WriteLine($"{span.Start} -> {sym.Kind} {sym.Name} @ {sym.DeclarationSpan.Start}");
        }

        return WriteDiagnostics(inputFile, compiled.Diagnostics);
    }

    static int NotYetImplemented(string stage)
    {
        Console.Error.WriteLine($"overt: --emit={stage} is not yet implemented");
        return 1;
    }

    static int UnknownStage(string stage)
    {
        Console.Error.WriteLine(
            $"overt: unknown --emit stage '{stage}' (valid: tokens, ast, csharp, go)");
        return 2;
    }

    /// <summary>
    /// Maps OV diagnostic codes to the AGENTS.md section that explains the
    /// relevant rule. Looked up by <see cref="WriteDiagnostics"/> so every
    /// reported diagnostic is paired with a pointer into the grounding doc —
    /// an agent hitting the error learns the rule from the message itself.
    /// </summary>
    static readonly Dictionary<string, string> AgentsMdSection = new(StringComparer.Ordinal)
    {
        // Lex — general lexical rules don't map cleanly to one §; point at the spec.
        ["OV0001"] = "docs/grammar/lexical.md",
        ["OV0002"] = "docs/grammar/lexical.md",
        ["OV0003"] = "docs/grammar/lexical.md",
        ["OV0102"] = "docs/grammar/lexical.md",
        ["OV0103"] = "docs/grammar/lexical.md",

        // Parse
        ["OV0150"] = "AGENTS.md §2 (modules and declarations)",
        ["OV0151"] = "AGENTS.md §5 (effect rows)",
        ["OV0152"] = "AGENTS.md §3 (types)",
        ["OV0153"] = "AGENTS.md §3 (types)",
        ["OV0154"] = "AGENTS.md §10 (calls and pipes — named-arg rule)",
        ["OV0155"] = "AGENTS.md §6 (match patterns)",
        ["OV0156"] = "AGENTS.md §3 (types — interpolated strings)",
        ["OV0157"] = "AGENTS.md §2 (declarations)",
        ["OV0158"] = "AGENTS.md §6 (match patterns)",
        ["OV0159"] = "AGENTS.md §6 (match patterns)",
        ["OV0160"] = "AGENTS.md §2 (declarations — uniqueness)",
        ["OV0161"] = "AGENTS.md §7 (let mut)",
        ["OV0162"] = "AGENTS.md §6 (match arms)",

        // Resolve
        ["OV0200"] = "AGENTS.md §2 (declarations and scope)",
        ["OV0201"] = "AGENTS.md §7 (no shadowing)",

        // Type check
        ["OV0300"] = "AGENTS.md §10 (call argument types)",
        ["OV0301"] = "AGENTS.md §2 (function return type)",
        ["OV0302"] = "AGENTS.md §3 (record fields)",
        ["OV0303"] = "AGENTS.md §6 (if/match arm types)",
        ["OV0304"] = "AGENTS.md §6 (condition must be Bool)",
        ["OV0306"] = "AGENTS.md §10 (call arity)",
        ["OV0307"] = "AGENTS.md §9 (errors as values)",
        ["OV0308"] = "AGENTS.md §6 (exhaustive match)",
        ["OV0310"] = "AGENTS.md §5 (effect rows)",
        ["OV0311"] = "AGENTS.md §4 (refinement types)",
        ["OV0312"] = "AGENTS.md §8 (control flow — break/continue)",
        ["OV0313"] = "AGENTS.md §8 (control flow — for each)",
    };

    static int WriteDiagnostics(string path, ImmutableArray<Diagnostic> diagnostics)
    {
        var errors = 0;
        foreach (var d in diagnostics)
        {
            var severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            Console.Error.WriteLine(
                $"{path}:{d.Span.Start.Line}:{d.Span.Start.Column}: {severity}: {d.Code}: {d.Message}");
            foreach (var note in d.Notes)
            {
                var kind = note.Kind == DiagnosticNoteKind.Help ? "help" : "note";
                if (note.Span is { } ns)
                {
                    Console.Error.WriteLine(
                        $"{path}:{ns.Start.Line}:{ns.Start.Column}: {kind}: {note.Text}");
                }
                else
                {
                    Console.Error.WriteLine($"  {kind}: {note.Text}");
                }
            }
            // Tack on the AGENTS.md pointer for this code. Printed after any
            // call-site-emitted notes so the canonical fix (the `help:` line)
            // appears first and the doc reference second.
            if (AgentsMdSection.TryGetValue(d.Code, out var section))
            {
                Console.Error.WriteLine($"  note: see {section}");
            }
            if (d.Severity == DiagnosticSeverity.Error)
            {
                errors++;
            }
        }
        return errors == 0 ? 0 : 1;
    }
}

static class AstPrinter
{
    public static void Print(SyntaxNode node, TextWriter writer)
    {
        var w = new Indented(writer);
        Visit(node, w);
    }

    static void Visit(SyntaxNode node, Indented w)
    {
        switch (node)
        {
            case ModuleDecl m:
                w.Line($"Module {m.Name}");
                using (w.Indent())
                {
                    foreach (var d in m.Declarations) Visit(d, w);
                }
                break;

            case FunctionDecl f:
                var tparams = f.TypeParameters.Length > 0
                    ? $"<{string.Join(", ", f.TypeParameters)}>"
                    : "";
                w.Line($"Function {f.Name}{tparams}");
                using (w.Indent())
                {
                    if (f.Parameters.Length > 0)
                    {
                        w.Line("Parameters");
                        using (w.Indent())
                        {
                            foreach (var p in f.Parameters) Visit(p, w);
                        }
                    }
                    if (f.Effects is { } eff) Visit(eff, w);
                    if (f.ReturnType is { } rt)
                    {
                        w.Line("Return");
                        using (w.Indent())
                        {
                            Visit(rt, w);
                        }
                    }
                    w.Line("Body");
                    using (w.Indent())
                    {
                        Visit(f.Body, w);
                    }
                }
                break;

            case FunctionType ft:
                w.Line("FnType");
                using (w.Indent())
                {
                    if (ft.Parameters.Length > 0)
                    {
                        w.Line("Parameters");
                        using (w.Indent())
                        {
                            foreach (var p in ft.Parameters) Visit(p, w);
                        }
                    }
                    if (ft.Effects is { } fteff) Visit(fteff, w);
                    w.Line("Return");
                    using (w.Indent()) Visit(ft.ReturnType, w);
                }
                break;

            case TypeAliasDecl tad:
                var tadParams = tad.TypeParameters.Length > 0
                    ? $"<{string.Join(", ", tad.TypeParameters)}>"
                    : "";
                w.Line($"TypeAlias {tad.Name}{tadParams}");
                using (w.Indent())
                {
                    w.Line("Target");
                    using (w.Indent()) Visit(tad.Target, w);
                    if (tad.Predicate is { } pred)
                    {
                        w.Line("Where");
                        using (w.Indent()) Visit(pred, w);
                    }
                }
                break;

            case Parameter p:
                w.Line($"Parameter {p.Name}");
                using (w.Indent())
                {
                    Visit(p.Type, w);
                }
                break;

            case EffectRow er:
                w.Line($"Effects {{ {string.Join(", ", er.Effects)} }}");
                break;

            case NamedType nt:
                w.Line($"Named {nt.Name}");
                if (nt.TypeArguments.Length > 0)
                {
                    using (w.Indent())
                    {
                        foreach (var a in nt.TypeArguments) Visit(a, w);
                    }
                }
                break;

            case UnitType:
                w.Line("Unit");
                break;

            case BlockExpr b:
                w.Line("Block");
                using (w.Indent())
                {
                    foreach (var s in b.Statements) Visit(s, w);
                    if (b.TrailingExpression is { } t)
                    {
                        w.Line("Trailing");
                        using (w.Indent())
                        {
                            Visit(t, w);
                        }
                    }
                }
                break;

            case ExpressionStmt es:
                w.Line("Stmt");
                using (w.Indent())
                {
                    Visit(es.Expression, w);
                }
                break;

            case CallExpr c:
                w.Line("Call");
                using (w.Indent())
                {
                    w.Line("Callee");
                    using (w.Indent()) Visit(c.Callee, w);
                    foreach (var a in c.Arguments) Visit(a, w);
                }
                break;

            case Argument a:
                w.Line(a.Name is null ? "Arg" : $"Arg {a.Name}");
                using (w.Indent())
                {
                    Visit(a.Value, w);
                }
                break;

            case PropagateExpr pr:
                w.Line("Propagate");
                using (w.Indent())
                {
                    Visit(pr.Operand, w);
                }
                break;

            case IdentifierExpr id:
                w.Line($"Ident {id.Name}");
                break;

            case StringLiteralExpr s:
                w.Line($"String {s.Value}");
                break;

            case InterpolatedStringExpr isx:
                w.Line("InterpolatedString");
                using (w.Indent())
                {
                    foreach (var part in isx.Parts) Visit(part, w);
                }
                break;

            case StringLiteralPart lp:
                w.Line($"Literal {lp.Text}");
                break;

            case StringInterpolationPart sip:
                w.Line("Interp");
                using (w.Indent()) Visit(sip.Expression, w);
                break;

            case IntegerLiteralExpr i:
                w.Line($"Int {i.Lexeme}");
                break;

            case FloatLiteralExpr f:
                w.Line($"Float {f.Lexeme}");
                break;

            case BooleanLiteralExpr b2:
                w.Line($"Bool {(b2.Value ? "true" : "false")}");
                break;

            case UnitExpr:
                w.Line("UnitValue");
                break;

            case FieldAccessExpr fa:
                w.Line($"Field .{fa.FieldName}");
                using (w.Indent()) Visit(fa.Target, w);
                break;

            case BinaryExpr be:
                w.Line($"Binary {be.Op}");
                using (w.Indent())
                {
                    Visit(be.Left, w);
                    Visit(be.Right, w);
                }
                break;

            case UnaryExpr ue:
                w.Line($"Unary {ue.Op}");
                using (w.Indent()) Visit(ue.Operand, w);
                break;

            case IfExpr ie:
                w.Line("If");
                using (w.Indent())
                {
                    w.Line("Condition");
                    using (w.Indent()) Visit(ie.Condition, w);
                    w.Line("Then");
                    using (w.Indent()) Visit(ie.Then, w);
                    if (ie.Else is { } elseBlock)
                    {
                        w.Line("Else");
                        using (w.Indent()) Visit(elseBlock, w);
                    }
                }
                break;

            case LetStmt ls:
                w.Line($"Let{(ls.IsMutable ? " mut" : "")}");
                using (w.Indent())
                {
                    w.Line("Target");
                    using (w.Indent()) Visit(ls.Target, w);
                    if (ls.Type is { } t2)
                    {
                        w.Line("Type");
                        using (w.Indent()) Visit(t2, w);
                    }
                    w.Line("Init");
                    using (w.Indent()) Visit(ls.Initializer, w);
                }
                break;

            case AssignmentStmt asn:
                w.Line($"Assign {asn.Name}");
                using (w.Indent()) Visit(asn.Value, w);
                break;

            case RecordDecl rd:
                w.Line($"Record {rd.Name}");
                using (w.Indent())
                {
                    foreach (var a in rd.Annotations) Visit(a, w);
                    foreach (var fld in rd.Fields) Visit(fld, w);
                }
                break;

            case EnumDecl ed:
                w.Line($"Enum {ed.Name}");
                using (w.Indent())
                {
                    foreach (var a in ed.Annotations) Visit(a, w);
                    foreach (var v in ed.Variants) Visit(v, w);
                }
                break;

            case EnumVariant ev:
                w.Line($"Variant {ev.Name}");
                if (ev.Fields.Length > 0)
                {
                    using (w.Indent())
                    {
                        foreach (var fld in ev.Fields) Visit(fld, w);
                    }
                }
                break;

            case Annotation attr:
                var args = attr.Arguments.Length > 0 ? $"({string.Join(", ", attr.Arguments)})" : "";
                w.Line($"@{attr.Name}{args}");
                break;

            case RecordField rf:
                w.Line($"Field {rf.Name}");
                using (w.Indent()) Visit(rf.Type, w);
                break;

            case RecordLiteralExpr rl:
                w.Line("RecordLiteral");
                using (w.Indent())
                {
                    w.Line("Type");
                    using (w.Indent()) Visit(rl.TypeTarget, w);
                    foreach (var fi in rl.Fields) Visit(fi, w);
                }
                break;

            case FieldInit fi2:
                w.Line($"= {fi2.Name}");
                using (w.Indent()) Visit(fi2.Value, w);
                break;

            case WithExpr we:
                w.Line("With");
                using (w.Indent())
                {
                    w.Line("Target");
                    using (w.Indent()) Visit(we.Target, w);
                    foreach (var u in we.Updates) Visit(u, w);
                }
                break;

            case WhileExpr whe:
                w.Line("While");
                using (w.Indent())
                {
                    w.Line("Condition");
                    using (w.Indent()) Visit(whe.Condition, w);
                    w.Line("Body");
                    using (w.Indent()) Visit(whe.Body, w);
                }
                break;

            case ForEachExpr fxe:
                w.Line("ForEach");
                using (w.Indent())
                {
                    w.Line("Binder");
                    using (w.Indent()) Visit(fxe.Binder, w);
                    w.Line("Iterable");
                    using (w.Indent()) Visit(fxe.Iterable, w);
                    w.Line("Body");
                    using (w.Indent()) Visit(fxe.Body, w);
                }
                break;

            case LoopExpr lpe:
                w.Line("Loop");
                using (w.Indent()) Visit(lpe.Body, w);
                break;

            case BreakStmt:
                w.Line("Break");
                break;

            case ContinueStmt:
                w.Line("Continue");
                break;

            case TupleExpr te:
                w.Line($"Tuple[{te.Elements.Length}]");
                using (w.Indent())
                {
                    foreach (var e in te.Elements) Visit(e, w);
                }
                break;

            case MatchExpr me:
                w.Line("Match");
                using (w.Indent())
                {
                    w.Line("Scrutinee");
                    using (w.Indent()) Visit(me.Scrutinee, w);
                    foreach (var arm in me.Arms) Visit(arm, w);
                }
                break;

            case MatchArm ma:
                w.Line("Arm");
                using (w.Indent())
                {
                    w.Line("Pattern");
                    using (w.Indent()) Visit(ma.Pattern, w);
                    w.Line("Body");
                    using (w.Indent()) Visit(ma.Body, w);
                }
                break;

            case WildcardPattern:
                w.Line("_");
                break;

            case IdentifierPattern ip:
                w.Line($"Bind {ip.Name}");
                break;

            case PathPattern pp:
                w.Line($"Path {string.Join(".", pp.Path)}");
                break;

            case ConstructorPattern cp:
                w.Line($"Ctor {string.Join(".", cp.Path)}");
                using (w.Indent())
                {
                    foreach (var a in cp.Arguments) Visit(a, w);
                }
                break;

            case RecordPattern rp:
                w.Line($"RecordPat {string.Join(".", rp.Path)}");
                using (w.Indent())
                {
                    foreach (var fp in rp.Fields) Visit(fp, w);
                }
                break;

            case FieldPattern fp2:
                w.Line($"= {fp2.Name}");
                using (w.Indent()) Visit(fp2.Subpattern, w);
                break;

            case TuplePattern tp:
                w.Line($"TuplePat[{tp.Elements.Length}]");
                using (w.Indent())
                {
                    foreach (var e in tp.Elements) Visit(e, w);
                }
                break;

            case LiteralPattern lp:
                w.Line("LiteralPat");
                using (w.Indent()) Visit(lp.Value, w);
                break;

            case ParallelExpr pe:
                w.Line($"Parallel[{pe.Tasks.Length}]");
                using (w.Indent())
                {
                    foreach (var t in pe.Tasks) Visit(t, w);
                }
                break;

            case RaceExpr re:
                w.Line($"Race[{re.Tasks.Length}]");
                using (w.Indent())
                {
                    foreach (var t in re.Tasks) Visit(t, w);
                }
                break;

            case UnsafeExpr ue2:
                w.Line("Unsafe");
                using (w.Indent()) Visit(ue2.Body, w);
                break;

            case TraceExpr tre:
                w.Line("Trace");
                using (w.Indent()) Visit(tre.Body, w);
                break;

            case ExternDecl ext:
                var unsafePrefix = ext.IsUnsafe ? "unsafe " : "";
                w.Line($"{unsafePrefix}Extern \"{ext.Platform}\" {ext.Name}");
                using (w.Indent())
                {
                    if (ext.Parameters.Length > 0)
                    {
                        w.Line("Parameters");
                        using (w.Indent())
                        {
                            foreach (var p in ext.Parameters) Visit(p, w);
                        }
                    }
                    if (ext.Effects is { } eff) Visit(eff, w);
                    if (ext.ReturnType is { } rt)
                    {
                        w.Line("Return");
                        using (w.Indent()) Visit(rt, w);
                    }
                    w.Line($"binds \"{ext.BindsTarget}\"");
                    if (ext.FromLibrary is { } lib)
                    {
                        w.Line($"from \"{lib}\"");
                    }
                }
                break;

            default:
                w.Line($"? {node.GetType().Name}");
                break;
        }
    }

    sealed class Indented
    {
        readonly TextWriter _writer;
        int _depth;
        public Indented(TextWriter writer) { _writer = writer; }
        public void Line(string text) => _writer.WriteLine(new string(' ', _depth * 2) + text);
        public IDisposable Indent()
        {
            _depth++;
            return new Releaser(this);
        }
        sealed class Releaser(Indented parent) : IDisposable
        {
            public void Dispose() => parent._depth--;
        }
    }
}
