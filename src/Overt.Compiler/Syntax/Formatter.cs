using System.Collections.Immutable;
using System.Text;

namespace Overt.Compiler.Syntax;

/// <summary>
/// Canonical source formatter for Overt. Takes a parsed <see cref="ModuleDecl"/>
/// plus the original token stream (comments preserved) and produces a single
/// canonical form: four-space indent, trailing commas on multi-line lists, one
/// statement per line, match arms one per line, pipes one per line after the
/// first, named arguments for multi-arg calls. One canonical form, no config.
///
/// Comment preservation: line comments survive a round-trip. They are emitted
/// at their source position — as leading comments on the declaration /
/// statement / expression that follows, or as trailing comments after the
/// token they share a line with.
///
/// Not covered by v1: block comments (lexer doesn't emit them), wrapping at a
/// target line width (fixed layout rules only), formatter-driven trailing
/// comma insertion on single-line collections. These are additive extensions.
/// </summary>
public static class Formatter
{
    private const string Indent = "    ";

    public static string Format(ModuleDecl module, ImmutableArray<Token> tokens)
    {
        var ctx = new FormatContext(new StringBuilder(), new CommentReader(tokens));
        FormatModule(module, ctx);
        return ctx.Result;
    }

    private sealed class FormatContext
    {
        private readonly StringBuilder _sb;
        public CommentReader Comments { get; }
        public int Depth { get; set; }

        public FormatContext(StringBuilder sb, CommentReader comments)
        {
            _sb = sb;
            Comments = comments;
        }

        public string Result => _sb.ToString().TrimEnd() + "\n";

        public void Write(string s) => _sb.Append(s);
        public void Line(string s = "")
        {
            WriteIndent();
            _sb.Append(s);
            _sb.Append('\n');
        }
        public void Newline() => _sb.Append('\n');
        public void WriteIndent()
        {
            for (var i = 0; i < Depth; i++) _sb.Append(Indent);
        }

        public bool EndsWithNewline
        {
            get
            {
                if (_sb.Length == 0) return true;
                return _sb[^1] == '\n';
            }
        }

        public bool EndsWithBlankLine
        {
            get
            {
                var len = _sb.Length;
                if (len < 2) return false;
                return _sb[len - 1] == '\n' && _sb[len - 2] == '\n';
            }
        }
    }

    /// <summary>Tracks which comments have been emitted. Comments are attached
    /// to the AST node whose span begins on or after the comment's line —
    /// "leading comments" flush out before their target. A comment that ends
    /// on the same line as the previously-emitted token becomes a trailing
    /// comment on that line.</summary>
    private sealed class CommentReader
    {
        private readonly ImmutableArray<Token> _tokens;
        private int _index;

        public CommentReader(ImmutableArray<Token> tokens)
        {
            _tokens = tokens;
        }

        /// <summary>Emit any queued comments whose source line precedes <paramref name="upToLine"/>.
        /// Used right before emitting a construct at the given source line so the
        /// comment pack that leads up to it flushes first.</summary>
        public void FlushLeading(FormatContext ctx, int upToLine)
        {
            while (_index < _tokens.Length)
            {
                var t = _tokens[_index];
                if (t.Kind != TokenKind.LineComment)
                {
                    _index++;
                    continue;
                }
                if (t.Span.Start.Line >= upToLine) break;

                // Separate blocks with a blank line so blank-line groupings the
                // author had around a comment survive at least approximately.
                if (!ctx.EndsWithNewline) ctx.Newline();
                ctx.Line(t.Lexeme);
                _index++;
            }
        }

        /// <summary>Emit any remaining comments at end of file.</summary>
        public void FlushTrailing(FormatContext ctx)
        {
            while (_index < _tokens.Length)
            {
                var t = _tokens[_index++];
                if (t.Kind != TokenKind.LineComment) continue;
                if (!ctx.EndsWithNewline) ctx.Newline();
                ctx.Line(t.Lexeme);
            }
        }
    }

    // ------------------------------------------------------------- module

    private static void FormatModule(ModuleDecl module, FormatContext ctx)
    {
        ctx.Comments.FlushLeading(ctx, module.Span.Start.Line);
        ctx.Line($"module {module.Name}");

        foreach (var decl in module.Declarations)
        {
            ctx.Newline();
            ctx.Comments.FlushLeading(ctx, decl.Span.Start.Line);
            FormatDecl(decl, ctx);
        }
        ctx.Comments.FlushTrailing(ctx);
    }

    private static void FormatDecl(Declaration decl, FormatContext ctx)
    {
        switch (decl)
        {
            case RecordDecl rd: FormatRecord(rd, ctx); break;
            case EnumDecl ed: FormatEnum(ed, ctx); break;
            case FunctionDecl fn: FormatFunction(fn, ctx); break;
            case TypeAliasDecl ta: FormatTypeAlias(ta, ctx); break;
            case ExternDecl ex: FormatExtern(ex, ctx); break;
            case ExternTypeDecl xt: FormatExternType(xt, ctx); break;
            case ExternUseDecl xu: FormatExternUse(xu, ctx); break;
            case UseDecl u: FormatUse(u, ctx); break;
            default:
                ctx.Line($"// TODO: unformatted decl {decl.GetType().Name}");
                break;
        }
    }

    // ------------------------------------------------------------- records

    private static void FormatRecord(RecordDecl rd, FormatContext ctx)
    {
        foreach (var ann in rd.Annotations) FormatAnnotation(ann, ctx);
        if (rd.Fields.Length == 0)
        {
            ctx.Line($"record {rd.Name} {{}}");
            return;
        }
        ctx.Line($"record {rd.Name} {{");
        ctx.Depth++;
        foreach (var f in rd.Fields)
        {
            ctx.WriteIndent();
            ctx.Write($"{f.Name}: ");
            FormatType(f.Type, ctx);
            ctx.Write(",");
            ctx.Newline();
        }
        ctx.Depth--;
        ctx.Line("}");
    }

    // --------------------------------------------------------------- enums

    private static void FormatEnum(EnumDecl ed, FormatContext ctx)
    {
        foreach (var ann in ed.Annotations) FormatAnnotation(ann, ctx);
        if (ed.Variants.Length == 0)
        {
            ctx.Line($"enum {ed.Name} {{}}");
            return;
        }
        ctx.Line($"enum {ed.Name} {{");
        ctx.Depth++;
        foreach (var v in ed.Variants)
        {
            ctx.WriteIndent();
            ctx.Write(v.Name);
            if (v.Fields.Length > 0)
            {
                ctx.Write(" { ");
                for (var i = 0; i < v.Fields.Length; i++)
                {
                    if (i > 0) ctx.Write(", ");
                    ctx.Write($"{v.Fields[i].Name}: ");
                    FormatType(v.Fields[i].Type, ctx);
                }
                ctx.Write(" }");
            }
            ctx.Write(",");
            ctx.Newline();
        }
        ctx.Depth--;
        ctx.Line("}");
    }

    // ---------------------------------------------------------- use

    private static void FormatUse(UseDecl u, FormatContext ctx)
    {
        ctx.WriteIndent();
        ctx.Write($"use {u.ModuleName}");
        if (u.Alias is { } alias)
        {
            ctx.Write($" as {alias}");
        }
        else
        {
            ctx.Write(".{");
            for (var i = 0; i < u.ImportedSymbols.Length; i++)
            {
                if (i > 0) ctx.Write(", ");
                ctx.Write(u.ImportedSymbols[i]);
            }
            ctx.Write("}");
        }
        ctx.Newline();
    }

    // ---------------------------------------------------------- annotations

    private static void FormatAnnotation(Annotation ann, FormatContext ctx)
    {
        ctx.WriteIndent();
        ctx.Write("@");
        ctx.Write(ann.Name);
        if (ann.Arguments.Length > 0)
        {
            ctx.Write("(");
            ctx.Write(string.Join(", ", ann.Arguments));
            ctx.Write(")");
        }
        ctx.Newline();
    }

    // ---------------------------------------------------------- type alias

    private static void FormatTypeAlias(TypeAliasDecl ta, FormatContext ctx)
    {
        ctx.WriteIndent();
        ctx.Write($"type {ta.Name}");
        if (ta.TypeParameters.Length > 0)
        {
            ctx.Write($"<{string.Join(", ", ta.TypeParameters)}>");
        }
        ctx.Write(" = ");
        FormatType(ta.Target, ctx);
        if (ta.Predicate is { } pred)
        {
            ctx.Write(" where ");
            FormatExpr(pred, ctx);
        }
        ctx.Newline();
    }

    // -------------------------------------------------------------- extern

    private static void FormatExternType(ExternTypeDecl xt, FormatContext ctx)
    {
        ctx.Line($"extern \"{xt.Platform}\" type {xt.Name} binds \"{xt.BindsTarget}\"");
    }

    private static void FormatExternUse(ExternUseDecl xu, FormatContext ctx)
    {
        var aliasSuffix = xu.Alias is null ? "" : $" as {xu.Alias}";
        ctx.Line($"extern \"{xu.Platform}\" use \"{xu.Target}\"{aliasSuffix}");
    }

    private static void FormatExtern(ExternDecl ex, FormatContext ctx)
    {
        ctx.WriteIndent();
        if (ex.IsUnsafe) ctx.Write("unsafe ");
        var kindKw = ex.Kind switch
        {
            ExternKind.Instance => " instance",
            ExternKind.Constructor => " ctor",
            _ => "",
        };
        ctx.Write($"extern \"{ex.Platform}\"{kindKw} fn {ex.Name}(");
        for (var i = 0; i < ex.Parameters.Length; i++)
        {
            if (i > 0) ctx.Write(", ");
            ctx.Write($"{ex.Parameters[i].Name}: ");
            FormatType(ex.Parameters[i].Type, ctx);
        }
        ctx.Write(")");
        if (ex.Effects is { } eff) FormatEffectRow(eff, ctx);
        if (ex.ReturnType is { } rt)
        {
            ctx.Write(" -> ");
            FormatType(rt, ctx);
        }
        ctx.Newline();
        // The grammar requires `binds "target"` (and optional `from "lib"`) on
        // their own line(s) after the signature.
        ctx.Depth++;
        ctx.Line($"binds \"{ex.BindsTarget}\"");
        if (ex.FromLibrary is { } lib)
        {
            ctx.Line($"from \"{lib}\"");
        }
        ctx.Depth--;
    }

    // ----------------------------------------------------------- functions

    private static void FormatFunction(FunctionDecl fn, FormatContext ctx)
    {
        ctx.WriteIndent();
        ctx.Write($"fn {fn.Name}");
        if (fn.TypeParameters.Length > 0)
        {
            ctx.Write($"<{string.Join(", ", fn.TypeParameters)}>");
        }
        ctx.Write("(");
        for (var i = 0; i < fn.Parameters.Length; i++)
        {
            if (i > 0) ctx.Write(", ");
            ctx.Write($"{fn.Parameters[i].Name}: ");
            FormatType(fn.Parameters[i].Type, ctx);
        }
        ctx.Write(")");
        if (fn.Effects is { } eff) FormatEffectRow(eff, ctx);
        if (fn.ReturnType is { } rt)
        {
            ctx.Write(" -> ");
            FormatType(rt, ctx);
        }
        ctx.Write(" ");
        FormatBlock(fn.Body, ctx);
        ctx.Newline();
    }

    private static void FormatEffectRow(EffectRow eff, FormatContext ctx)
    {
        if (eff.Effects.Length == 0)
        {
            ctx.Write(" !{}");
            return;
        }
        ctx.Write($" !{{{string.Join(", ", eff.Effects)}}}");
    }

    // ------------------------------------------------------------- types

    private static void FormatType(TypeExpr t, FormatContext ctx)
    {
        switch (t)
        {
            case UnitType: ctx.Write("()"); break;
            case NamedType nt:
                ctx.Write(nt.Name);
                if (nt.TypeArguments.Length > 0)
                {
                    ctx.Write("<");
                    for (var i = 0; i < nt.TypeArguments.Length; i++)
                    {
                        if (i > 0) ctx.Write(", ");
                        FormatType(nt.TypeArguments[i], ctx);
                    }
                    ctx.Write(">");
                }
                break;
            case FunctionType ft:
                ctx.Write("fn(");
                for (var i = 0; i < ft.Parameters.Length; i++)
                {
                    if (i > 0) ctx.Write(", ");
                    FormatType(ft.Parameters[i], ctx);
                }
                ctx.Write(")");
                if (ft.Effects is { } eff) FormatEffectRow(eff, ctx);
                ctx.Write(" -> ");
                FormatType(ft.ReturnType, ctx);
                break;
            default: ctx.Write("/* ? type */"); break;
        }
    }

    // ------------------------------------------------------- statements

    private static void FormatBlock(BlockExpr block, FormatContext ctx)
    {
        if (block.Statements.Length == 0 && block.TrailingExpression is null)
        {
            ctx.Write("{}");
            return;
        }
        ctx.Write("{");
        ctx.Newline();
        ctx.Depth++;
        foreach (var stmt in block.Statements)
        {
            ctx.Comments.FlushLeading(ctx, stmt.Span.Start.Line);
            FormatStmt(stmt, ctx);
        }
        if (block.TrailingExpression is { } tail)
        {
            ctx.Comments.FlushLeading(ctx, tail.Span.Start.Line);
            ctx.WriteIndent();
            FormatExpr(tail, ctx);
            ctx.Newline();
        }
        ctx.Depth--;
        ctx.WriteIndent();
        ctx.Write("}");
    }

    private static void FormatStmt(Statement stmt, FormatContext ctx)
    {
        switch (stmt)
        {
            case LetStmt ls:
                ctx.WriteIndent();
                ctx.Write("let ");
                if (ls.IsMutable) ctx.Write("mut ");
                FormatPattern(ls.Target, ctx);
                if (ls.Type is { } t)
                {
                    ctx.Write(": ");
                    FormatType(t, ctx);
                }
                ctx.Write(" = ");
                FormatExpr(ls.Initializer, ctx);
                ctx.Newline();
                break;
            case AssignmentStmt asn:
                ctx.WriteIndent();
                ctx.Write($"{asn.Name} = ");
                FormatExpr(asn.Value, ctx);
                ctx.Newline();
                break;
            case ExpressionStmt es:
                ctx.WriteIndent();
                FormatExpr(es.Expression, ctx);
                ctx.Newline();
                break;
            case BreakStmt:
                ctx.Line("break");
                break;
            case ContinueStmt:
                ctx.Line("continue");
                break;
        }
    }

    // --------------------------------------------------------- expressions

    private static void FormatExpr(Expression e, FormatContext ctx)
    {
        switch (e)
        {
            case IntegerLiteralExpr i: ctx.Write(i.Lexeme); break;
            case FloatLiteralExpr f: ctx.Write(f.Lexeme); break;
            case BooleanLiteralExpr b: ctx.Write(b.Value ? "true" : "false"); break;
            case StringLiteralExpr s:
                // StringLiteralExpr.Value retains the original quoted lexeme;
                // emit it verbatim rather than re-quoting.
                ctx.Write(s.Value);
                break;
            case UnitExpr: ctx.Write("()"); break;
            case IdentifierExpr id: ctx.Write(id.Name); break;
            case InterpolatedStringExpr isx: FormatInterpolatedString(isx, ctx); break;
            case FieldAccessExpr fa:
                FormatExpr(fa.Target, ctx);
                ctx.Write($".{fa.FieldName}");
                break;
            case CallExpr c: FormatCall(c, ctx); break;
            case PropagateExpr pr:
                FormatExpr(pr.Operand, ctx);
                ctx.Write("?");
                break;
            case BinaryExpr be: FormatBinary(be, ctx); break;
            case UnaryExpr ue:
                ctx.Write(ue.Op == UnaryOp.Negate ? "-" : "!");
                FormatExpr(ue.Operand, ctx);
                break;
            case IfExpr ie: FormatIf(ie, ctx); break;
            case MatchExpr me: FormatMatch(me, ctx); break;
            case WhileExpr we:
                ctx.Write("while ");
                FormatExpr(we.Condition, ctx);
                ctx.Write(" ");
                FormatBlock(we.Body, ctx);
                break;
            case ForEachExpr fe:
                ctx.Write("for each ");
                FormatPattern(fe.Binder, ctx);
                ctx.Write(" in ");
                FormatExpr(fe.Iterable, ctx);
                ctx.Write(" ");
                FormatBlock(fe.Body, ctx);
                break;
            case LoopExpr lp:
                ctx.Write("loop ");
                FormatBlock(lp.Body, ctx);
                break;
            case BlockExpr b: FormatBlock(b, ctx); break;
            case TupleExpr te:
                ctx.Write("(");
                for (var i = 0; i < te.Elements.Length; i++)
                {
                    if (i > 0) ctx.Write(", ");
                    FormatExpr(te.Elements[i], ctx);
                }
                ctx.Write(")");
                break;
            case RecordLiteralExpr rl: FormatRecordLiteral(rl, ctx); break;
            case WithExpr we:
                FormatExpr(we.Target, ctx);
                ctx.Write(" with { ");
                for (var i = 0; i < we.Updates.Length; i++)
                {
                    if (i > 0) ctx.Write(", ");
                    ctx.Write($"{we.Updates[i].Name} = ");
                    FormatExpr(we.Updates[i].Value, ctx);
                }
                ctx.Write(" }");
                break;
            case UnsafeExpr ux:
                ctx.Write("unsafe ");
                FormatBlock(ux.Body, ctx);
                break;
            case TraceExpr tx:
                ctx.Write("trace ");
                FormatBlock(tx.Body, ctx);
                break;
            case ParallelExpr pe:
                ctx.Write("parallel {");
                ctx.Newline();
                ctx.Depth++;
                foreach (var task in pe.Tasks)
                {
                    ctx.WriteIndent();
                    FormatExpr(task, ctx);
                    ctx.Write(",");
                    ctx.Newline();
                }
                ctx.Depth--;
                ctx.WriteIndent();
                ctx.Write("}");
                break;
            case RaceExpr re:
                ctx.Write("race {");
                ctx.Newline();
                ctx.Depth++;
                foreach (var task in re.Tasks)
                {
                    ctx.WriteIndent();
                    FormatExpr(task, ctx);
                    ctx.Write(",");
                    ctx.Newline();
                }
                ctx.Depth--;
                ctx.WriteIndent();
                ctx.Write("}");
                break;
            default:
                ctx.Write($"/* ? {e.GetType().Name} */");
                break;
        }
    }

    private static void FormatCall(CallExpr c, FormatContext ctx)
    {
        FormatExpr(c.Callee, ctx);
        ctx.Write("(");
        for (var i = 0; i < c.Arguments.Length; i++)
        {
            if (i > 0) ctx.Write(", ");
            if (c.Arguments[i].Name is { } name)
            {
                ctx.Write($"{name} = ");
            }
            FormatExpr(c.Arguments[i].Value, ctx);
        }
        ctx.Write(")");
    }

    private static void FormatRecordLiteral(RecordLiteralExpr rl, FormatContext ctx)
    {
        FormatExpr(rl.TypeTarget, ctx);
        if (rl.Fields.Length == 0)
        {
            ctx.Write(" {}");
            return;
        }
        ctx.Write(" { ");
        for (var i = 0; i < rl.Fields.Length; i++)
        {
            if (i > 0) ctx.Write(", ");
            ctx.Write($"{rl.Fields[i].Name} = ");
            FormatExpr(rl.Fields[i].Value, ctx);
        }
        ctx.Write(" }");
    }

    private static void FormatBinary(BinaryExpr be, FormatContext ctx)
    {
        var op = be.Op switch
        {
            BinaryOp.Add => "+",
            BinaryOp.Subtract => "-",
            BinaryOp.Multiply => "*",
            BinaryOp.Divide => "/",
            BinaryOp.Modulo => "%",
            BinaryOp.Equal => "==",
            BinaryOp.NotEqual => "!=",
            BinaryOp.Less => "<",
            BinaryOp.LessEqual => "<=",
            BinaryOp.Greater => ">",
            BinaryOp.GreaterEqual => ">=",
            BinaryOp.LogicalAnd => "&&",
            BinaryOp.LogicalOr => "||",
            BinaryOp.PipeCompose => "|>",
            BinaryOp.PipePropagate => "|>?",
            _ => "?",
        };
        FormatExpr(be.Left, ctx);
        ctx.Write($" {op} ");
        FormatExpr(be.Right, ctx);
    }

    private static void FormatIf(IfExpr ie, FormatContext ctx)
    {
        ctx.Write("if ");
        FormatExpr(ie.Condition, ctx);
        ctx.Write(" ");
        FormatBlock(ie.Then, ctx);
        if (ie.Else is { } elseBlock)
        {
            ctx.Write(" else ");
            FormatBlock(elseBlock, ctx);
        }
    }

    private static void FormatMatch(MatchExpr me, FormatContext ctx)
    {
        ctx.Write("match ");
        FormatExpr(me.Scrutinee, ctx);
        ctx.Write(" {");
        ctx.Newline();
        ctx.Depth++;
        foreach (var arm in me.Arms)
        {
            ctx.Comments.FlushLeading(ctx, arm.Span.Start.Line);
            ctx.WriteIndent();
            FormatPattern(arm.Pattern, ctx);
            ctx.Write(" => ");
            FormatExpr(arm.Body, ctx);
            ctx.Write(",");
            ctx.Newline();
        }
        ctx.Depth--;
        ctx.WriteIndent();
        ctx.Write("}");
    }

    private static void FormatInterpolatedString(InterpolatedStringExpr isx, FormatContext ctx)
    {
        // String parts carry the source quotes — the first text run starts with
        // `"` and the last ends with `"`. Emit parts verbatim; only wrap
        // interpolation segments with `${...}`.
        foreach (var part in isx.Parts)
        {
            switch (part)
            {
                case StringLiteralPart lp: ctx.Write(lp.Text); break;
                case StringInterpolationPart sip:
                    ctx.Write("${");
                    FormatExpr(sip.Expression, ctx);
                    ctx.Write("}");
                    break;
            }
        }
    }

    // ---------------------------------------------------------- patterns

    private static void FormatPattern(Pattern p, FormatContext ctx)
    {
        switch (p)
        {
            case WildcardPattern: ctx.Write("_"); break;
            case IdentifierPattern ip: ctx.Write(ip.Name); break;
            case PathPattern pp: ctx.Write(string.Join(".", pp.Path)); break;
            case ConstructorPattern cp:
                ctx.Write(string.Join(".", cp.Path));
                if (cp.Arguments.Length > 0)
                {
                    ctx.Write("(");
                    for (var i = 0; i < cp.Arguments.Length; i++)
                    {
                        if (i > 0) ctx.Write(", ");
                        FormatPattern(cp.Arguments[i], ctx);
                    }
                    ctx.Write(")");
                }
                break;
            case RecordPattern rp:
                ctx.Write(string.Join(".", rp.Path));
                ctx.Write(" { ");
                for (var i = 0; i < rp.Fields.Length; i++)
                {
                    if (i > 0) ctx.Write(", ");
                    ctx.Write($"{rp.Fields[i].Name} = ");
                    FormatPattern(rp.Fields[i].Subpattern, ctx);
                }
                ctx.Write(" }");
                break;
            case TuplePattern tp:
                ctx.Write("(");
                for (var i = 0; i < tp.Elements.Length; i++)
                {
                    if (i > 0) ctx.Write(", ");
                    FormatPattern(tp.Elements[i], ctx);
                }
                ctx.Write(")");
                break;
            case LiteralPattern lp: FormatExpr(lp.Value, ctx); break;
        }
    }
}
