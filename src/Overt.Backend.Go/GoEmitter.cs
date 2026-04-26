using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Overt.Compiler.Syntax;

namespace Overt.Backend.Go;

/// <summary>
/// Go back end. Lowers the supported subset of the Overt AST to Go source
/// so a transpiled module can be built with `go build` and run as a native
/// binary. The C# back end (<c>CSharpEmitter</c>) is the reference;
/// this emitter is intentionally several feature levels behind it.
///
/// Currently supported:
///   - module declaration → `package main`
///   - functions with parameters and Result / Option / record / primitive
///     return types; `?`-propagation lowers to early-return
///   - records (struct decl, literal, field access)
///   - enums (interface + struct-per-variant + sealing method)
///   - match on enum scrutinees in statement and return position,
///     with bare-variant and record-pattern arms
///   - if/else in statement and return position (else-if chains
///     fold to Go `if/else if/else`)
///   - let with explicit type, integer / boolean / string / unit
///     literals, the full set of binary and unary operators
///
/// Out of scope, queued for follow-ups: generics on user types,
/// expression-position match in non-return slots (let initializers,
/// fn args), constructor patterns (`Ok(x)`-style positional bindings),
/// the rest of the prelude, full effect-row propagation through
/// non-Result-Unit-IoError shapes.
/// </summary>
public static class GoEmitter
{
    /// <summary>Emit Go source for a single Overt module. Returns the full
    /// file content; the caller writes it to disk and arranges for `go
    /// build` / `go run` against the runtime under `runtime/go`.</summary>
    public static string Emit(ModuleDecl module)
    {
        var emitter = new EmitterInstance(module);
        return emitter.Emit();
    }

    /// <summary>
    /// Per-Emit() state: the StringBuilder, the enum-name → variants map
    /// (built before any expression is lowered so variant references like
    /// <c>Shape.Circle</c> can be distinguished from field access at emit
    /// time), and the current ?-temp counter (threaded across nested blocks).
    /// One instance per Emit() call; never shared across modules.
    /// </summary>
    private sealed class EmitterInstance
    {
        private readonly ModuleDecl _module;
        // The body StringBuilder accumulates declarations as we walk the
        // module. Imports are computed during emission (we only know
        // `fmt` is used after we encounter an InterpolatedStringExpr) and
        // prepended after the body is complete. Go is strict about
        // unused imports, so we can't speculatively import everything.
        private readonly StringBuilder _sb = new();
        private readonly Dictionary<string, ImmutableArray<EnumVariant>> _enums = new();
        private bool _usesFmt;
        private int _qCounter;

        public EmitterInstance(ModuleDecl module)
        {
            _module = module;
            foreach (var decl in module.Declarations)
            {
                if (decl is EnumDecl en)
                {
                    _enums[en.Name] = en.Variants;
                }
            }
        }

        public string Emit()
        {
            // Build the body first so the import set is known by the
            // time we render the file header.
            var hasUserMain = false;
            foreach (var decl in _module.Declarations)
            {
                switch (decl)
                {
                    case FunctionDecl fn:
                        EmitFunction(fn);
                        if (fn.Name == "main") hasUserMain = true;
                        _sb.AppendLine();
                        break;

                    case RecordDecl rec:
                        EmitRecord(rec);
                        _sb.AppendLine();
                        break;

                    case EnumDecl en:
                        EmitEnum(en);
                        _sb.AppendLine();
                        break;

                    default:
                        throw new NotSupportedException(
                            $"Go back end does not yet handle {decl.GetType().Name}.");
                }
            }

            if (hasUserMain)
            {
                _sb.AppendLine("func main() {");
                _sb.AppendLine("\tr := __overt_main()");
                _sb.AppendLine("\tif !r.IsOk {");
                _sb.AppendLine("\t\t_ = overt.Eprintln(r.Err.Error())");
                _sb.AppendLine("\t\tos.Exit(1)");
                _sb.AppendLine("\t}");
                _sb.AppendLine("}");
            }
            else
            {
                // Suppress "imported and not used" for `os` when no user main.
                _sb.AppendLine("var _ = os.Exit");
            }

            // Now assemble the final file: header + computed imports + body.
            var header = new StringBuilder();
            header.AppendLine("// Code generated by Overt.Backend.Go. DO NOT EDIT.");
            header.AppendLine($"// Transpiled from Overt module `{_module.Name}`.");
            header.AppendLine();
            header.AppendLine("package main");
            header.AppendLine();
            header.AppendLine("import (");
            if (_usesFmt) header.AppendLine("\t\"fmt\"");
            header.AppendLine("\t\"os\"");
            header.AppendLine("\t\"overt-runtime/overt\"");
            header.AppendLine(")");
            header.AppendLine();

            return header.ToString() + _sb.ToString();
        }

        // ------------------------------------------------------ declarations

        private void EmitFunction(FunctionDecl fn)
        {
            var goName = fn.Name == "main" ? "__overt_main" : fn.Name;

            _sb.Append($"func {goName}(");
            for (var i = 0; i < fn.Parameters.Length; i++)
            {
                if (i > 0) _sb.Append(", ");
                var p = fn.Parameters[i];
                _sb.Append(p.Name);
                _sb.Append(' ');
                _sb.Append(LowerType(p.Type));
            }
            _sb.Append(") ");
            EmitReturnType(fn.ReturnType);
            _sb.AppendLine(" {");
            var asReturn = fn.ReturnType is not null && fn.ReturnType is not UnitType;
            _qCounter = 0;
            EmitBlock(fn.Body, indent: 1, asReturn);
            _sb.AppendLine("}");
        }

        private void EmitRecord(RecordDecl rec)
        {
            _sb.AppendLine($"type {rec.Name} struct {{");
            foreach (var field in rec.Fields)
            {
                _sb.Append('\t');
                _sb.Append(field.Name);
                _sb.Append(' ');
                _sb.AppendLine(LowerType(field.Type));
            }
            _sb.AppendLine("}");
        }

        /// <summary>
        /// Lower an Overt enum to Go's idiomatic sum-type pattern: an
        /// interface with an unexported sealing method, plus one struct per
        /// variant that implements it. Bare variants emit as
        /// <c>type EnumName_Variant struct{}</c>; record-shaped variants
        /// carry their fields. The variant-as-constructor reference at use
        /// sites lowers to <c>EnumName_Variant{...}</c> via
        /// <see cref="EmitVariantConstructor"/>; the type-switch lowering
        /// in <see cref="EmitMatch"/> consumes the same per-variant struct
        /// type for case discrimination.
        /// </summary>
        private void EmitEnum(EnumDecl en)
        {
            var sealName = $"is{en.Name}";
            _sb.AppendLine($"type {en.Name} interface {{");
            _sb.AppendLine($"\t{sealName}()");
            _sb.AppendLine("}");
            _sb.AppendLine();

            foreach (var variant in en.Variants)
            {
                var structName = $"{en.Name}_{variant.Name}";
                if (variant.Fields.Length == 0)
                {
                    _sb.AppendLine($"type {structName} struct{{}}");
                }
                else
                {
                    _sb.AppendLine($"type {structName} struct {{");
                    foreach (var field in variant.Fields)
                    {
                        _sb.Append('\t');
                        _sb.Append(field.Name);
                        _sb.Append(' ');
                        _sb.AppendLine(LowerType(field.Type));
                    }
                    _sb.AppendLine("}");
                }
                _sb.AppendLine($"func ({structName}) {sealName}() {{}}");
            }
        }

        private void EmitReturnType(TypeExpr? type)
        {
            if (type is null || type is UnitType)
            {
                return;
            }
            _sb.Append(LowerType(type));
        }

        // ------------------------------------------------------ types

        private string LowerType(TypeExpr? type) => type switch
        {
            NamedType { Name: "Int" } => "int",
            NamedType { Name: "Int64" } => "int64",
            NamedType { Name: "Bool" } => "bool",
            NamedType { Name: "String" } => "string",
            NamedType { Name: "Float" } => "float64",
            UnitType => "overt.Unit",
            NamedType { Name: "Result" } nt when nt.TypeArguments.Length == 2
                => $"overt.Result[{LowerType(nt.TypeArguments[0])}, {LowerType(nt.TypeArguments[1])}]",
            NamedType { Name: "Option" } nt when nt.TypeArguments.Length == 1
                => $"overt.Option[{LowerType(nt.TypeArguments[0])}]",
            NamedType { Name: "List" } nt when nt.TypeArguments.Length == 1
                => $"overt.List[{LowerType(nt.TypeArguments[0])}]",
            NamedType { Name: "IoError" } => "overt.IoError",
            // User-declared record / enum types: NamedType with zero type
            // arguments. Refer to it by its source name (single-package layout).
            NamedType nt when nt.TypeArguments.Length == 0 => nt.Name,
            null => "overt.Unit",
            _ => throw new NotSupportedException(
                "Go back end does not yet handle type expression " + type.GetType().Name
                + (type is NamedType nm ? $" (Name = {nm.Name})" : "")),
        };

        // ------------------------------------------------------ blocks + statements

        private void EmitBlock(BlockExpr block, int indent, bool asReturn)
        {
            var pad = new string('\t', indent);
            foreach (var stmt in block.Statements)
            {
                _sb.Append(pad);
                EmitStatement(stmt, indent);
                _sb.AppendLine();
            }
            if (block.TrailingExpression is not null)
            {
                _sb.Append(pad);
                if (block.TrailingExpression is PropagateExpr trailingPe)
                {
                    EmitPropagate(trailingPe, indent);
                    _sb.AppendLine();
                    return;
                }
                if (asReturn && block.TrailingExpression is IfExpr trailingIf)
                {
                    EmitIfAsReturn(trailingIf, indent);
                    _sb.AppendLine();
                    return;
                }
                if (asReturn && block.TrailingExpression is MatchExpr trailingMatch)
                {
                    EmitMatch(trailingMatch, indent, asReturn: true);
                    _sb.AppendLine();
                    return;
                }
                if (asReturn)
                {
                    _sb.Append("return ");
                }
                EmitExpression(block.TrailingExpression);
                _sb.AppendLine();
            }
        }

        private void EmitStatement(Statement stmt, int indent)
        {
            switch (stmt)
            {
                case ExpressionStmt es:
                    if (es.Expression is PropagateExpr pe)
                    {
                        EmitPropagate(pe, indent);
                    }
                    else if (es.Expression is IfExpr ie)
                    {
                        EmitIfStatement(ie, indent);
                    }
                    else if (es.Expression is MatchExpr me)
                    {
                        EmitMatch(me, indent, asReturn: false);
                    }
                    else if (es.Expression is ForEachExpr fe)
                    {
                        EmitForEach(fe, indent);
                    }
                    else
                    {
                        EmitExpression(es.Expression);
                    }
                    break;

                case LetStmt ls:
                    EmitLet(ls);
                    break;

                case DiscardStmt ds:
                    _sb.Append("_ = ");
                    EmitExpression(ds.Value);
                    break;

                default:
                    throw new NotSupportedException(
                        $"Go back end does not yet handle {stmt.GetType().Name}.");
            }
        }

        private void EmitLet(LetStmt ls)
        {
            if (ls.Target is not IdentifierPattern ip)
            {
                throw new NotSupportedException(
                    "Go back end does not yet handle destructuring let; "
                    + $"got pattern {ls.Target.GetType().Name}.");
            }
            _sb.Append("var ");
            _sb.Append(ip.Name);
            if (ls.Type is not null)
            {
                _sb.Append(' ');
                _sb.Append(LowerType(ls.Type));
            }
            _sb.Append(" = ");
            EmitExpression(ls.Initializer);
        }

        /// <summary>
        /// Lower `for x in iter { body }` to Go's `for _, x := range
        /// iter.Items { body }`. The iterable must evaluate to an
        /// `overt.List[T]`; the runtime List wraps a Go slice as its
        /// `Items` field, which Go's `range` walks natively. The
        /// index is discarded with `_`. Body emits as a statement
        /// block (the for-each value is always Unit).
        /// </summary>
        private void EmitForEach(ForEachExpr fe, int indent)
        {
            if (fe.Binder is not IdentifierPattern ip)
            {
                throw new NotSupportedException(
                    "Go back end does not yet handle destructuring for-each binders; "
                    + $"got pattern {fe.Binder.GetType().Name}.");
            }
            var pad = new string('\t', indent);
            _sb.Append("for _, ");
            _sb.Append(ip.Name);
            _sb.Append(" := range (");
            EmitExpression(fe.Iterable);
            _sb.AppendLine(").Items {");
            EmitBlock(fe.Body, indent + 1, asReturn: false);
            _sb.Append(pad);
            _sb.Append('}');
        }

        private void EmitIfStatement(IfExpr ie, int indent)
        {
            var pad = new string('\t', indent);
            _sb.Append("if ");
            EmitExpression(ie.Condition);
            _sb.AppendLine(" {");
            EmitBlock(ie.Then, indent + 1, asReturn: false);
            _sb.Append(pad);
            if (ie.Else is BlockExpr elseBlock)
            {
                if (elseBlock.Statements.Length == 0
                    && elseBlock.TrailingExpression is IfExpr nested)
                {
                    _sb.Append("} else ");
                    EmitIfStatement(nested, indent);
                }
                else
                {
                    _sb.AppendLine("} else {");
                    EmitBlock(elseBlock, indent + 1, asReturn: false);
                    _sb.Append(pad);
                    _sb.Append('}');
                }
            }
            else
            {
                _sb.Append('}');
            }
        }

        private void EmitIfAsReturn(IfExpr ie, int indent)
        {
            var pad = new string('\t', indent);
            _sb.Append("if ");
            EmitExpression(ie.Condition);
            _sb.AppendLine(" {");
            EmitBlock(ie.Then, indent + 1, asReturn: true);
            _sb.Append(pad);
            if (ie.Else is BlockExpr elseBlock)
            {
                if (elseBlock.Statements.Length == 0
                    && elseBlock.TrailingExpression is IfExpr nested)
                {
                    _sb.Append("} else ");
                    EmitIfAsReturn(nested, indent);
                }
                else
                {
                    _sb.AppendLine("} else {");
                    EmitBlock(elseBlock, indent + 1, asReturn: true);
                    _sb.Append(pad);
                    _sb.Append('}');
                }
            }
            else
            {
                throw new NotSupportedException(
                    "if-without-else in return position is unreachable per the type checker");
            }
        }

        // ------------------------------------------------------ match

        /// <summary>
        /// Lower a match expression to Go's type-switch (when the scrutinee
        /// is enum-typed) or value-switch (other shapes — not yet wired).
        /// In return position, each arm body is recursively emitted as a
        /// return-shaped block; in statement position, each arm body is a
        /// statement block. A `default: panic(...)` guards the type switch
        /// so Go's flow analysis accepts the function as exhaustively
        /// returning, mirroring the type checker's exhaustiveness contract.
        /// </summary>
        private void EmitMatch(MatchExpr me, int indent, bool asReturn)
        {
            // Determine the scrutinee's enum (if any) by looking at the
            // first arm's pattern path. The type checker has already
            // verified all arms are coherent; we just need a name to
            // generate `Type_Variant` case labels.
            var enumName = TryResolveEnumName(me);
            if (enumName is null)
            {
                throw new NotSupportedException(
                    "Go back end currently only supports match on enum "
                    + "scrutinees with variant patterns; got something else.");
            }

            var pad = new string('\t', indent);
            var scrutVar = $"__m_{_qCounter++}";
            _sb.Append($"switch {scrutVar} := ");
            EmitExpression(me.Scrutinee);
            _sb.AppendLine(".(type) {");

            foreach (var arm in me.Arms)
            {
                var (variant, fieldBindings) = ResolvePatternVariant(arm.Pattern, enumName);
                _sb.Append(pad);
                _sb.AppendLine($"case {enumName}_{variant}:");

                // Either bind the destructured fields, or silence the
                // unused-variable warning if no bindings are taken.
                if (fieldBindings.Count == 0)
                {
                    _sb.Append(pad);
                    _sb.AppendLine($"\t_ = {scrutVar}");
                }
                else
                {
                    foreach (var (binder, fieldName) in fieldBindings)
                    {
                        _sb.Append(pad);
                        _sb.AppendLine($"\t{binder} := {scrutVar}.{fieldName}");
                    }
                }

                _sb.Append(pad);
                _sb.Append('\t');
                EmitArmBody(arm.Body, indent + 1, asReturn);
            }

            _sb.Append(pad);
            _sb.AppendLine("default:");
            _sb.Append(pad);
            _sb.AppendLine($"\tpanic(\"unreachable: type checker proved match exhaustive\")");
            _sb.Append(pad);
            _sb.Append('}');
        }

        private string? TryResolveEnumName(MatchExpr me)
        {
            foreach (var arm in me.Arms)
            {
                var path = arm.Pattern switch
                {
                    PathPattern p => p.Path,
                    RecordPattern r => r.Path,
                    ConstructorPattern c => c.Path,
                    _ => default,
                };
                if (!path.IsDefault && path.Length == 2 && _enums.ContainsKey(path[0]))
                {
                    return path[0];
                }
            }
            return null;
        }

        /// <summary>
        /// For a single arm pattern, return the matched variant name plus
        /// the per-field bindings the arm body needs in scope. The type
        /// checker has already validated that the path is well-formed and
        /// the fields exist.
        /// </summary>
        private static (string Variant, List<(string Binder, string Field)> Bindings)
            ResolvePatternVariant(Pattern pattern, string enumName)
        {
            var bindings = new List<(string, string)>();
            switch (pattern)
            {
                case PathPattern p when p.Path.Length == 2 && p.Path[0] == enumName:
                    return (p.Path[1], bindings);

                case RecordPattern r when r.Path.Length == 2 && r.Path[0] == enumName:
                    foreach (var fp in r.Fields)
                    {
                        if (fp.Subpattern is IdentifierPattern ip)
                        {
                            bindings.Add((ip.Name, fp.Name));
                        }
                        else if (fp.Subpattern is WildcardPattern)
                        {
                            // Skip — no binding requested.
                        }
                        else
                        {
                            throw new NotSupportedException(
                                "Go back end does not yet handle nested patterns in record fields; "
                                + $"got {fp.Subpattern.GetType().Name} in field {fp.Name}.");
                        }
                    }
                    return (r.Path[1], bindings);

                case ConstructorPattern c when c.Path.Length == 2 && c.Path[0] == enumName:
                    throw new NotSupportedException(
                        "Go back end does not yet handle positional variant patterns "
                        + $"like `{c.Path[0]}.{c.Path[1]}(...)`; use record-shape patterns "
                        + "with named fields for now.");

                default:
                    throw new NotSupportedException(
                        $"Go back end does not yet handle pattern shape {pattern.GetType().Name} "
                        + $"in match on `{enumName}`.");
            }
        }

        private void EmitArmBody(Expression body, int indent, bool asReturn)
        {
            if (body is BlockExpr block)
            {
                _sb.AppendLine();
                EmitBlock(block, indent, asReturn);
                return;
            }
            // Inline expression-shaped arm bodies (the common case for
            // simple matches like `Tree.Leaf => 0`): emit either
            // `return <expr>` or just `<expr>` depending on position.
            if (body is PropagateExpr pe)
            {
                EmitPropagate(pe, indent);
                _sb.AppendLine();
                return;
            }
            if (asReturn)
            {
                _sb.Append("return ");
            }
            EmitExpression(body);
            _sb.AppendLine();
        }

        // ------------------------------------------------------ propagate

        private void EmitPropagate(PropagateExpr pe, int indent)
        {
            var pad = new string('\t', indent);
            var name = $"__q_{_qCounter++}";
            _sb.Append($"{name} := ");
            EmitExpression(pe.Operand);
            _sb.AppendLine();
            _sb.Append(pad);
            _sb.AppendLine($"if !{name}.IsOk {{");
            _sb.Append(pad);
            _sb.AppendLine($"\treturn overt.Err[overt.Unit, overt.IoError]({name}.Err)");
            _sb.Append(pad);
            _sb.Append("}");
        }

        // ------------------------------------------------------ expressions

        private void EmitExpression(Expression expr)
        {
            switch (expr)
            {
                case StringLiteralExpr sl:
                    _sb.Append(sl.Value);
                    break;

                case InterpolatedStringExpr ie:
                    EmitInterpolatedString(ie);
                    break;

                case IntegerLiteralExpr il:
                    _sb.Append(il.Lexeme);
                    break;

                case BooleanLiteralExpr bl:
                    _sb.Append(bl.Value ? "true" : "false");
                    break;

                case UnitExpr:
                    _sb.Append("overt.UnitValue");
                    break;

                case BinaryExpr be:
                    _sb.Append('(');
                    EmitExpression(be.Left);
                    _sb.Append(' ');
                    _sb.Append(BinaryOpToGo(be.Op));
                    _sb.Append(' ');
                    EmitExpression(be.Right);
                    _sb.Append(')');
                    break;

                case UnaryExpr ue:
                    _sb.Append('(');
                    _sb.Append(UnaryOpToGo(ue.Op));
                    EmitExpression(ue.Operand);
                    _sb.Append(')');
                    break;

                case CallExpr call:
                    EmitCall(call);
                    break;

                case RecordLiteralExpr rl:
                    EmitRecordLiteral(rl);
                    break;

                case FieldAccessExpr fa:
                    // `EnumName.Variant` (bare-variant constructor) lowers
                    // to `EnumName_Variant{}`. Other field accesses are
                    // straight Go field reads.
                    if (fa.Target is IdentifierExpr typeId
                        && _enums.TryGetValue(typeId.Name, out var variants)
                        && IsVariantOf(variants, fa.FieldName))
                    {
                        _sb.Append($"{typeId.Name}_{fa.FieldName}{{}}");
                    }
                    else
                    {
                        EmitExpression(fa.Target);
                        _sb.Append('.');
                        _sb.Append(fa.FieldName);
                    }
                    break;

                case IdentifierExpr id:
                    _sb.Append(MapIdentifier(id.Name));
                    break;

                default:
                    throw new NotSupportedException(
                        $"Go back end does not yet handle expression {expr.GetType().Name}.");
            }
        }

        private static bool IsVariantOf(ImmutableArray<EnumVariant> variants, string name)
        {
            foreach (var v in variants)
            {
                if (v.Name == name) return true;
            }
            return false;
        }

        /// <summary>
        /// Lower an interpolated Overt string to a `fmt.Sprintf` call. Each
        /// literal-text part contributes its raw text (with `%` doubled to
        /// escape it), and each interpolated expression contributes a
        /// `%v` placeholder plus its emitted value as a vararg.
        /// `fmt.Sprintf` returns a plain `string`, matching Overt's
        /// String type for the interpolated-string expression.
        ///
        /// An InterpolatedStringExpr that happens to have only literal
        /// parts and no interpolations (rare but possible) still goes
        /// through this path and emits as `fmt.Sprintf("text")`. That's
        /// equivalent to the bare string but avoids special-casing.
        /// </summary>
        private void EmitInterpolatedString(InterpolatedStringExpr ie)
        {
            _usesFmt = true;
            var format = new StringBuilder("\"");
            var values = new List<Expression>();
            for (var i = 0; i < ie.Parts.Length; i++)
            {
                var part = ie.Parts[i];
                switch (part)
                {
                    case StringLiteralPart lit:
                        // The lexer's segmenting leaves the surrounding
                        // quote(s) on the head and tail parts of the AST
                        // (mirroring how StringLiteralExpr.Value carries
                        // them). Middle parts have no quotes. Strip the
                        // outer ones before re-encoding for Go's format
                        // string. cf. CSharpEmitter.StripQuotesForInterp.
                        var text = lit.Text;
                        if (i == 0 && text.StartsWith("\"")) text = text[1..];
                        if (i == ie.Parts.Length - 1 && text.EndsWith("\"")) text = text[..^1];
                        // Re-encode for Go: % must double-escape so it
                        // isn't interpreted as a verb; the C-style
                        // escapes (\n, \t, \\, \") are valid in Go string
                        // literals, so they pass through. The Text here
                        // is the still-encoded source form (`\n` is two
                        // characters, not a newline), so don't translate.
                        format.Append(text.Replace("%", "%%"));
                        break;

                    case StringInterpolationPart interp:
                        format.Append("%v");
                        values.Add(interp.Expression);
                        break;

                    default:
                        throw new NotSupportedException(
                            $"Go back end does not yet handle string part {part.GetType().Name}.");
                }
            }
            format.Append('"');

            _sb.Append("fmt.Sprintf(");
            _sb.Append(format);
            foreach (var v in values)
            {
                _sb.Append(", ");
                EmitExpression(v);
            }
            _sb.Append(')');
        }

        private void EmitRecordLiteral(RecordLiteralExpr rl)
        {
            // Two shapes:
            //   `Name { ... }`            single-segment record literal
            //   `EnumName.Variant { ... }`  enum variant constructor with fields
            string typeName;
            if (rl.TypeTarget is IdentifierExpr typeId)
            {
                typeName = typeId.Name;
            }
            else if (rl.TypeTarget is FieldAccessExpr fa
                && fa.Target is IdentifierExpr enumId
                && _enums.TryGetValue(enumId.Name, out var variants)
                && IsVariantOf(variants, fa.FieldName))
            {
                typeName = $"{enumId.Name}_{fa.FieldName}";
            }
            else
            {
                throw new NotSupportedException(
                    "Go back end does not yet handle record-literal type target "
                    + $"{rl.TypeTarget.GetType().Name}.");
            }
            _sb.Append(typeName);
            _sb.Append('{');
            for (var i = 0; i < rl.Fields.Length; i++)
            {
                if (i > 0) _sb.Append(", ");
                _sb.Append(rl.Fields[i].Name);
                _sb.Append(": ");
                EmitExpression(rl.Fields[i].Value);
            }
            _sb.Append('}');
        }

        private void EmitCall(CallExpr call)
        {
            // Special-case the constructors: `Ok(x)` and `Err(e)` need
            // explicit type parameters in Go since there's no contextual
            // type inference at the call site.
            if (call.Callee is IdentifierExpr { Name: "Ok" } && call.Arguments.Length == 1)
            {
                _sb.Append("overt.Ok[overt.Unit, overt.IoError](");
                EmitExpression(call.Arguments[0].Value);
                _sb.Append(')');
                return;
            }
            if (call.Callee is IdentifierExpr { Name: "Err" } && call.Arguments.Length == 1)
            {
                _sb.Append("overt.Err[overt.Unit, overt.IoError](");
                EmitExpression(call.Arguments[0].Value);
                _sb.Append(')');
                return;
            }

            // Module-qualified stdlib calls (`Int.range(0, 4)`,
            // `List.empty()`, `String.chars(s)`, etc.) route through a
            // flat Go function name in the runtime package. Distinct
            // from FieldAccess in expression position, where the same
            // shape might be a variant constructor (`Shape.Point`).
            // Variant constructors with no args are emitted via the
            // FieldAccessExpr path in EmitExpression, so we don't need
            // to distinguish them here — but a record-shaped variant
            // with args reaches us via RecordLiteralExpr, not CallExpr.
            // What we WILL see here: stdlib namespace calls.
            if (call.Callee is FieldAccessExpr { Target: IdentifierExpr nsId } facCallee)
            {
                if (MapNamespaceCall(nsId.Name, facCallee.FieldName) is { } mapped)
                {
                    _sb.Append(mapped);
                    _sb.Append('(');
                    for (var i = 0; i < call.Arguments.Length; i++)
                    {
                        if (i > 0) _sb.Append(", ");
                        EmitExpression(call.Arguments[i].Value);
                    }
                    _sb.Append(')');
                    return;
                }
            }

            EmitExpression(call.Callee);
            _sb.Append('(');
            for (var i = 0; i < call.Arguments.Length; i++)
            {
                if (i > 0) _sb.Append(", ");
                EmitExpression(call.Arguments[i].Value);
            }
            _sb.Append(')');
        }

        private static string BinaryOpToGo(BinaryOp op) => op switch
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
            _ => throw new NotSupportedException(
                "Go back end does not yet handle binary operator " + op
                + " (pipe operators desugar in a separate pass that's not wired here)."),
        };

        private static string UnaryOpToGo(UnaryOp op) => op switch
        {
            UnaryOp.Negate => "-",
            UnaryOp.LogicalNot => "!",
            _ => throw new NotSupportedException(
                "Go back end does not yet handle unary operator " + op),
        };

        /// <summary>
        /// Map an unqualified Overt identifier in expression position to its
        /// Go-runtime equivalent. Names not in this table emit verbatim,
        /// which is correct for user-declared fns and let-bindings.
        ///
        /// Go uses CamelCase for exported identifiers, so the Overt-side
        /// snake_case prelude names (`map`, `filter`, etc.) all gain a
        /// capitalized first letter at this boundary. Snake-case names
        /// with multiple segments (`unwrap_or` etc., when they show up
        /// at the bare-call level — not the case today) would split
        /// here too.
        /// </summary>
        private static string MapIdentifier(string name) => name switch
        {
            "println" => "overt.Println",
            "eprintln" => "overt.Eprintln",
            "map" => "overt.Map",
            "filter" => "overt.Filter",
            "fold" => "overt.Fold",
            "all" => "overt.All",
            "any" => "overt.Any",
            "size" => "overt.Size",
            "len" => "overt.Len",
            "length" => "overt.Length",
            _ => name,
        };

        /// <summary>
        /// Map an Overt module-qualified identifier (`Int.range`,
        /// `List.empty`, etc.) to its flat Go-runtime function name.
        /// Returns null when the path isn't a known stdlib namespace
        /// member, so the caller can decide whether to fall back to
        /// regular field access. Naming convention: camelize the
        /// member, drop the dot, prepend the namespace name, e.g.
        /// `String.starts_with` → `overt.StringStartsWith`.
        /// </summary>
        private static string? MapNamespaceCall(string namespaceName, string member)
        {
            // Allowlist gate: only known stdlib namespaces route here.
            // Unknown namespaces are most likely the user's own enum
            // (variant access) and shouldn't be camelized.
            if (namespaceName is not ("Int" or "List" or "String" or "Option" or "Result" or "Trace" or "CString"))
            {
                return null;
            }
            // Camelize: split on _, uppercase each segment, join.
            var parts = member.Split('_');
            var camel = new StringBuilder();
            foreach (var part in parts)
            {
                if (part.Length == 0) continue;
                camel.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1) camel.Append(part[1..]);
            }
            return $"overt.{namespaceName}{camel}";
        }
    }
}
