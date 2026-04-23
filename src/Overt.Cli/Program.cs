using System.Collections.Immutable;
using Overt.Backend.CSharp;
using Overt.Compiler.Diagnostics;
using Overt.Compiler.Semantics;
using Overt.Compiler.Syntax;

// CLI contract — stable for external integrators (Compiler Explorer, CI, agent tools):
//
//   overt --emit=<stage> <file.ov>    emit compiler output for one pipeline stage
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

return Cli.Run(args);

static class Cli
{
    const string Version = "overt 0.1.0-dev";

    const string Usage =
        """
        usage: overt --emit=<stage> <file.ov>

        options:
          --emit=<stage>   required. one of: tokens, ast, resolved, typed, csharp, go
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
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        var resolved = NameResolver.Resolve(parse.Module);
        var typed = TypeChecker.Check(parse.Module, resolved);

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

        var combined = lex.Diagnostics
            .AddRange(parse.Diagnostics)
            .AddRange(resolved.Diagnostics)
            .AddRange(typed.Diagnostics);
        return WriteDiagnostics(inputFile, combined);
    }

    static int EmitCSharp(string source, string inputFile)
    {
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        var resolved = NameResolver.Resolve(parse.Module);
        var typed = TypeChecker.Check(parse.Module, resolved);

        // Source path flows into #line directives so PDBs map runtime errors back to
        // the .ov file, not the generated .cs. The absolute path is resolved so PDB
        // entries survive the build being invoked from anywhere.
        var sourcePath = Path.GetFullPath(inputFile);
        var csharp = CSharpEmitter.Emit(parse.Module, typed, sourcePath);
        Console.Out.Write(csharp);

        var combined = lex.Diagnostics
            .AddRange(parse.Diagnostics)
            .AddRange(resolved.Diagnostics)
            .AddRange(typed.Diagnostics);
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
        var lex = Lexer.Lex(source);
        var parse = Parser.Parse(lex.Tokens);
        var resolved = NameResolver.Resolve(parse.Module);

        // Print a flat summary of resolutions: one line per resolved reference, in
        // source order. Deterministic and diff-friendly.
        foreach (var (span, sym) in resolved.Resolutions
            .OrderBy(kv => kv.Key.Start.Line)
            .ThenBy(kv => kv.Key.Start.Column))
        {
            Console.Out.WriteLine($"{span.Start} -> {sym.Kind} {sym.Name} @ {sym.DeclarationSpan.Start}");
        }

        var combined = lex.Diagnostics
            .AddRange(parse.Diagnostics)
            .AddRange(resolved.Diagnostics);
        return WriteDiagnostics(inputFile, combined);
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
