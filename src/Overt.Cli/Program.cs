using System.Collections.Immutable;
using Overt.Compiler.Diagnostics;
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
          --emit=<stage>   required. one of: tokens, ast, csharp, go
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
            "csharp" => NotYetImplemented("csharp"),
            "go" => NotYetImplemented("go"),
            _ => UnknownStage(emit),
        };
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
                w.Line($"Function {f.Name}");
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
                    w.Line("Else");
                    using (w.Indent()) Visit(ie.Else, w);
                }
                break;

            case LetStmt ls:
                w.Line($"Let{(ls.IsMutable ? " mut" : "")} {ls.Name}");
                using (w.Indent())
                {
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
