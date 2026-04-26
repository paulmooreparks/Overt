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
        // Opaque host-type bindings: name → binds-string (the
        // verbatim Go-side type expression). LowerType consults
        // this when a NamedType doesn't match a built-in or stdlib
        // shape; the binds-string is what gets emitted at every use
        // site. See docs/ffi.md §2.
        private readonly Dictionary<string, string> _externTypes = new(StringComparer.Ordinal);
        // Names of `extern "go" instance fn` declarations so EmitCall
        // can route a method-call-shaped site (`c.method(...)`) to
        // the generated shim (`method(c, ...)`) instead of emitting
        // verbatim Go field access. See docs/ffi.md §5.
        private readonly HashSet<string> _instanceExterns = new(StringComparer.Ordinal);
        // WithExpr → emitted-temp-name map for the
        // hoist-then-substitute pass that handles WithExpr in
        // arbitrary expression positions (e.g. as a fn-call
        // argument). Pre-emitted before each return-position
        // expression line; cleared per line. EmitExpression's
        // WithExpr case checks here first.
        private readonly Dictionary<SourceSpan, string> _withHoists = new();
        // Refinement-type aliases (`type T = Base where ...`).
        // Non-generic ones get a Go `type T = Base` declaration so
        // the source name shows up in the emitted code; generic ones
        // (`type NonEmpty<T> = List<T>`) skip declaration and have
        // their Target substituted at every use site, since Go's
        // generic alias support varies across versions and the
        // substitution form is unambiguous. The `where` predicate
        // is dropped — refinement runtime checks aren't injected on
        // the Go target yet (see docs/ffi.md and the sweep comment
        // block).
        private readonly Dictionary<string, TypeAliasDecl> _typeAliases = new(StringComparer.Ordinal);
        // Set of Go-side import paths the emitted code needs. The
        // header is assembled after the body is emitted, so we know
        // exactly which packages to import. Initially seeded with the
        // runtime; `os` is added when there's a user main; `fmt` is
        // added when an interpolated string lowers; arbitrary
        // `extern "go"` bindings add their own package paths.
        private readonly SortedSet<string> _imports = new(StringComparer.Ordinal);
        private bool _usesFmt;
        private int _qCounter;
        // The enclosing fn's return type, threaded into ?-propagation so
        // the early-return matches the fn's Result<T, E> shape rather
        // than the previous hardcoded `[overt.Unit, overt.IoError]`.
        // null while emitting top-level decls / record fields / etc.
        private TypeExpr? _currentFnReturnType;

        public EmitterInstance(ModuleDecl module)
        {
            _module = module;
            foreach (var decl in module.Declarations)
            {
                if (decl is EnumDecl en)
                {
                    _enums[en.Name] = en.Variants;
                }
                else if (decl is ExternTypeDecl ext && ext.Platform == "go")
                {
                    _externTypes[ext.Name] = ext.BindsTarget;
                    var importPath = TryExtractGoImportPath(ext.BindsTarget);
                    if (importPath is not null)
                    {
                        _imports.Add(importPath);
                    }
                }
                else if (decl is ExternDecl efn
                    && efn.Platform == "go"
                    && efn.Kind == ExternKind.Instance)
                {
                    _instanceExterns.Add(efn.Name);
                }
                else if (decl is TypeAliasDecl ta)
                {
                    _typeAliases[ta.Name] = ta;
                }
            }
        }

        /// <summary>
        /// Substitute type arguments for type parameters in a TypeExpr
        /// tree. Used for generic refinement-alias use sites where we
        /// substitute the alias's Target rather than declaring a Go
        /// generic alias (Go-version-portability concern). Walks the
        /// TypeExpr; replaces any `NamedType("T")` whose name matches
        /// a parameter in <paramref name="parameters"/> with the
        /// corresponding entry from <paramref name="arguments"/>.
        /// </summary>
        private static TypeExpr SubstituteTypeArgs(
            TypeExpr type,
            ImmutableArray<string> parameters,
            ImmutableArray<TypeExpr> arguments)
        {
            switch (type)
            {
                case NamedType nt when nt.TypeArguments.Length == 0:
                    var idx = parameters.IndexOf(nt.Name);
                    if (idx >= 0) return arguments[idx];
                    return nt;
                case NamedType nt:
                    var subbed = nt.TypeArguments.Select(
                        ta => SubstituteTypeArgs(ta, parameters, arguments)).ToImmutableArray();
                    return new NamedType(nt.Name, subbed, nt.Span);
                case FunctionType ft:
                    var subbedParams = ft.Parameters.Select(
                        p => SubstituteTypeArgs(p, parameters, arguments)).ToImmutableArray();
                    var subbedRet = SubstituteTypeArgs(ft.ReturnType, parameters, arguments);
                    return new FunctionType(subbedParams, ft.Effects, subbedRet, ft.Span);
                default:
                    return type;
            }
        }

        /// <summary>
        /// Lower an Overt FunctionType to a Go `func(...) R` type
        /// expression. Used both for function-typed parameters in
        /// `extern "go" fn` declarations and for any other context
        /// where a function-typed value crosses the FFI boundary.
        /// Effect rows are erased at this point — Go has no concept
        /// of them; the type checker validates calling-context on
        /// the Overt side. A `()`-returning function emits with no
        /// return slot. See docs/ffi.md §4.
        /// </summary>
        private string LowerFunctionType(FunctionType ft)
        {
            var sb = new StringBuilder("func(");
            for (var i = 0; i < ft.Parameters.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(LowerType(ft.Parameters[i]));
            }
            sb.Append(')');
            if (ft.ReturnType is not UnitType)
            {
                sb.Append(' ');
                sb.Append(LowerType(ft.ReturnType));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Rewrite an opaque-type binds-string from import-path form
        /// (`*net/http.Request`, `github.com/gorilla/websocket.Conn`)
        /// to Go-source-use form (`*http.Request`,
        /// `websocket.Conn`). The package selector at the use site
        /// is the LAST segment of the import path, not the full
        /// path itself; Go's syntax doesn't allow `/` in type
        /// expressions.
        ///
        /// Algorithm: peel off any leading `*` indirection markers,
        /// split the remainder at the LAST `.` (which separates
        /// the import path from the in-package type name), then take
        /// the last `/`-segment of the path as the selector. Composite
        /// type expressions (`[]T`, `map[K]V`, `chan T`, `func(...)`)
        /// pass through verbatim — they don't follow the package-qualified
        /// shape this rewrite targets.
        /// </summary>
        private static string OpaqueTypeToGo(string binds)
        {
            var prefix = new StringBuilder();
            var s = binds;
            while (s.StartsWith("*", StringComparison.Ordinal))
            {
                prefix.Append('*');
                s = s[1..];
            }
            if (s.StartsWith("[", StringComparison.Ordinal)
                || s.StartsWith("map[", StringComparison.Ordinal)
                || s.StartsWith("chan ", StringComparison.Ordinal)
                || s.StartsWith("func", StringComparison.Ordinal))
            {
                return binds;
            }
            var lastDot = s.LastIndexOf('.');
            if (lastDot <= 0)
            {
                // Bare universe-block name (`int`, `string`, `error`,
                // etc.) — no rewrite needed.
                return binds;
            }
            var path = s[..lastDot];
            var typeName = s[(lastDot + 1)..];
            var lastSlash = path.LastIndexOf('/');
            var selector = lastSlash < 0 ? path : path[(lastSlash + 1)..];
            prefix.Append(selector);
            prefix.Append('.');
            prefix.Append(typeName);
            return prefix.ToString();
        }

        /// <summary>
        /// Extract the Go import path from an opaque-type binds-string
        /// like <c>"*net/http.Request"</c> → <c>"net/http"</c>. Returns
        /// null when the string is a built-in Go type expression
        /// (<c>"[]string"</c>, <c>"map[string]int"</c>, <c>"int"</c>)
        /// that needs no import.
        ///
        /// Algorithm: strip one leading <c>*</c>, then split at the
        /// LAST dot. The part before is the import path; the part
        /// after is the in-package type name. If there's no dot, the
        /// type is in the universe block (`int`, `string`, `error`)
        /// or a slice/map composite literal — nothing to import.
        /// </summary>
        private static string? TryExtractGoImportPath(string binds)
        {
            var s = binds;
            if (s.StartsWith("*", StringComparison.Ordinal))
            {
                s = s[1..];
            }
            // Reject composite type expressions (slices, maps, arrays).
            // Their element types may need imports too, but parsing
            // those out is brittle; for v1 we trust the user to declare
            // separate `extern "go" type` entries for the inner types
            // that need importing.
            if (s.StartsWith("[", StringComparison.Ordinal)
                || s.StartsWith("map[", StringComparison.Ordinal)
                || s.StartsWith("chan ", StringComparison.Ordinal)
                || s.StartsWith("func", StringComparison.Ordinal))
            {
                return null;
            }
            var lastDot = s.LastIndexOf('.');
            if (lastDot <= 0)
            {
                return null;
            }
            return s[..lastDot];
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

                    case ExternDecl ext:
                        EmitExtern(ext);
                        _sb.AppendLine();
                        break;

                    case ExternTypeDecl:
                        // Already collected into _externTypes during
                        // construction; the binds-string surfaces at
                        // every use site via LowerType. No body emit.
                        break;

                    case TypeAliasDecl ta:
                        EmitTypeAlias(ta);
                        // EmitTypeAlias may emit nothing (generic
                        // aliases substitute at use sites instead);
                        // only add the trailing blank line when it
                        // actually wrote something.
                        if (ta.TypeParameters.Length == 0)
                        {
                            _sb.AppendLine();
                        }
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
                // No user main: this Overt module is a library. Go's
                // `package main` still requires a `func main()` even
                // when the build is just type-checked, so we synthesize
                // an empty one. It also doubles as the unused-import
                // suppression for `os`. A future `overt-go` build tool
                // that knows its target is a library will instead emit
                // `package <camelized-module-name>` and skip both.
                _sb.AppendLine("func main() {");
                _sb.AppendLine("\t_ = os.Exit");
                _sb.AppendLine("}");
            }

            // Compose the final import set. `os` is always needed
            // (main wrapper or library stub uses os.Exit). The
            // runtime import is added only when the body actually
            // references it; a pure-record library that never uses
            // Result / Option / Unit / Ok / Err / runtime helpers
            // wouldn't compile with an unused-import error if we
            // imported it unconditionally.
            _imports.Add("os");
            var body = _sb.ToString();
            if (body.Contains("overt."))
            {
                _imports.Add("overt-runtime/overt");
            }
            if (_usesFmt) _imports.Add("fmt");

            // Now assemble the final file: header + computed imports + body.
            var header = new StringBuilder();
            header.AppendLine("// Code generated by Overt.Backend.Go. DO NOT EDIT.");
            header.AppendLine($"// Transpiled from Overt module `{_module.Name}`.");
            header.AppendLine();
            header.AppendLine("package main");
            header.AppendLine();
            header.AppendLine("import (");
            foreach (var path in _imports)
            {
                header.AppendLine($"\t\"{path}\"");
            }
            header.AppendLine(")");
            header.AppendLine();

            return header.ToString() + _sb.ToString();
        }

        // ------------------------------------------------------ declarations

        private void EmitFunction(FunctionDecl fn)
        {
            var goName = fn.Name == "main" ? "__overt_main" : fn.Name;

            _sb.Append($"func {goName}");
            // Type parameters: emit `[T any, U any, ...]` for any
            // type variables actually referenced in the signature.
            // Overt's <T, E, ...> mixes type variables and effect-
            // row variables in the same list; only the former
            // matter for Go's type-parameter syntax. We detect by
            // walking the parameter / return types and collecting
            // names that appear as a NamedType reference.
            var typeVars = CollectUsedTypeVariables(fn);
            if (typeVars.Count > 0)
            {
                _sb.Append('[');
                for (var i = 0; i < typeVars.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    _sb.Append(typeVars[i]);
                    _sb.Append(" any");
                }
                _sb.Append(']');
            }
            _sb.Append('(');
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
            _currentFnReturnType = fn.ReturnType;
            try
            {
                EmitBlock(fn.Body, indent: 1, asReturn);
            }
            finally
            {
                _currentFnReturnType = null;
            }
            _sb.AppendLine("}");
        }

        /// <summary>
        /// Lower an Overt <c>extern "go" fn</c> declaration to a Go
        /// shim that wraps the bound target. The shim is a real Go
        /// function with the Overt-side name and signature; its body
        /// calls the bound target with the parameters passed through.
        ///
        /// Binds-target encoding: the string is the Go-side use-site
        /// expression (e.g. <c>"fmt.Println"</c> or <c>"time.Now"</c>).
        /// The first dotted segment is the package selector; everything
        /// after is the member. The matching import path defaults to
        /// the package selector (correct for stdlib like <c>fmt</c>);
        /// non-stdlib targets supply the full path via <c>from
        /// "github.com/foo/bar"</c>, which then aliases as the same
        /// package selector when resolved.
        ///
        /// Externs targeting other platforms (`csharp`, `c`) emit a
        /// runtime-panic stub so `go build` still succeeds; calling
        /// one is a programmer error caught at run time. Mirrors the
        /// C# back end's <c>ExternPlatformNotImplemented</c> shape.
        /// </summary>
        private void EmitExtern(ExternDecl ext)
        {
            if (ext.Platform == "go")
            {
                EmitExternGoShim(ext);
                return;
            }

            // Non-Go extern: emit a panic stub so the program builds
            // but calling the fn surfaces a clear error at run time.
            // The Go target can't satisfy a `csharp` or `c` binding;
            // a multi-target build that needs the call has to wire
            // the right back end.
            _sb.Append($"func {ext.Name}(");
            for (var i = 0; i < ext.Parameters.Length; i++)
            {
                if (i > 0) _sb.Append(", ");
                var p = ext.Parameters[i];
                _sb.Append(p.Name);
                _sb.Append(' ');
                _sb.Append(LowerType(p.Type));
            }
            _sb.Append(") ");
            EmitReturnType(ext.ReturnType);
            _sb.AppendLine(" {");
            _sb.AppendLine($"\tpanic(\"extern \\\"{ext.Platform}\\\" not implemented on go target: {ext.Name}\")");
            _sb.AppendLine("}");
        }

        private void EmitExternGoShim(ExternDecl ext)
        {
            // Three binding shapes:
            //   - Static + `binds "pkg.Member"` (optional `from "<path>"`):
            //     out-of-package; emit `pkg.Member(args)`.
            //   - Static + `binds "Member"` (or `from ""`): the bound
            //     symbol lives in the same Go package as the emitted
            //     code. Emit `Member(args)` unqualified.
            //   - Instance + `binds "Method"`: the bound symbol is a
            //     method on the first parameter's host type. Emit
            //     `self.Method(rest)`. The receiver's package import
            //     comes from its extern "go" type declaration; nothing
            //     to add at this site.
            string? selector;
            string member;
            var isInstance = ext.Kind == ExternKind.Instance;
            if (isInstance)
            {
                if (ext.Parameters.Length == 0)
                {
                    throw new NotSupportedException(
                        "Go back end `extern \"go\" instance fn` requires a `self` "
                        + $"first parameter; `{ext.Name}` declares none.");
                }
                if (ext.BindsTarget.Contains('.'))
                {
                    throw new NotSupportedException(
                        "Go back end `extern \"go\" instance fn` binds-target must "
                        + "be a bare method name (e.g. `binds \"ReadMessage\"`); "
                        + $"got `{ext.BindsTarget}`.");
                }
                selector = null;
                member = ext.BindsTarget;
            }
            else if (ext.BindsTarget.Contains('.'))
            {
                (selector, member) = SplitBindsTarget(ext.BindsTarget);
                // Empty `from ""` means "no import needed because the
                // selector already names a symbol in the current
                // package's universe." Otherwise the import is the
                // selector for stdlib or the explicit `from` path.
                if (ext.FromLibrary != "")
                {
                    _imports.Add(ext.FromLibrary ?? selector);
                }
            }
            else
            {
                selector = null;
                member = ext.BindsTarget;
                if (!string.IsNullOrEmpty(ext.FromLibrary))
                {
                    _imports.Add(ext.FromLibrary);
                }
            }

            _sb.Append($"func {ext.Name}(");
            for (var i = 0; i < ext.Parameters.Length; i++)
            {
                if (i > 0) _sb.Append(", ");
                var p = ext.Parameters[i];
                _sb.Append(p.Name);
                _sb.Append(' ');
                _sb.Append(LowerType(p.Type));
            }
            _sb.Append(") ");
            EmitReturnType(ext.ReturnType);
            _sb.AppendLine(" {");

            // Body shape #1: Result<T, IoError> return → wrap the
            // Go-side `(T, error)` (or `error`) into Ok / Err. This
            // is the convention every Go stdlib fallible function
            // follows; Overt's Result<T, IoError> targets it directly.
            // See docs/ffi.md §6.
            if (TryGetResultIoErrorTypeArg(ext.ReturnType) is { } innerType)
            {
                EmitResultWrappedShimBody(ext, selector, member, innerType, isInstance);
                _sb.AppendLine("}");
                return;
            }

            // Body shape #2: Option<T> return → wrap the Go-side
            // (typically nillable) value into Some / None based on
            // a nil-check. The lowering is `if __r == nil { return
            // None } else { return Some(__r) }`. Valid for pointer,
            // interface, slice, map, and channel returns; invalid
            // for value-typed T (Go's nil isn't comparable to int,
            // string, etc.) — those surface as a clear go-build
            // error if the user mismatches the declaration.
            // See docs/ffi.md §6.
            if (TryGetOptionTypeArg(ext.ReturnType) is { } optionInner)
            {
                EmitOptionWrappedShimBody(ext, selector, member, optionInner, isInstance);
                _sb.AppendLine("}");
                return;
            }

            // Body shape #3: direct passthrough. For Unit-returning
            // shims the call is a statement; otherwise prefix with
            // `return`. Used when the Overt return is a primitive,
            // an opaque host type, or a non-Result composite.
            _sb.Append('\t');
            var hasReturnSlot = ext.ReturnType is not null && ext.ReturnType is not UnitType;
            if (hasReturnSlot) _sb.Append("return ");
            EmitShimCall(ext, selector, member, isInstance);
            _sb.AppendLine();
            _sb.AppendLine("}");
        }

        /// <summary>
        /// Emit the call expression that the shim forwards to. For
        /// static externs, this is `pkg.Member(arg1, ...)` or
        /// `Member(...)` for in-package binds. For instance externs,
        /// the first Overt parameter is the receiver; emit
        /// `self.Member(rest...)`.
        /// </summary>
        private void EmitShimCall(ExternDecl ext, string? selector, string member, bool isInstance)
        {
            if (isInstance)
            {
                _sb.Append(ext.Parameters[0].Name);
                _sb.Append('.');
                _sb.Append(member);
                _sb.Append('(');
                for (var i = 1; i < ext.Parameters.Length; i++)
                {
                    if (i > 1) _sb.Append(", ");
                    _sb.Append(ext.Parameters[i].Name);
                }
                _sb.Append(')');
                return;
            }
            if (selector is not null)
            {
                _sb.Append(selector);
                _sb.Append('.');
            }
            _sb.Append(member);
            _sb.Append('(');
            for (var i = 0; i < ext.Parameters.Length; i++)
            {
                if (i > 0) _sb.Append(", ");
                _sb.Append(ext.Parameters[i].Name);
            }
            _sb.Append(')');
        }

        /// <summary>
        /// If <paramref name="returnType"/> is `Result&lt;T, IoError&gt;`,
        /// return T. Used to detect the (T, error)-wrapping shim body
        /// shape. Returns null for any other return shape, including
        /// `Result&lt;T, E&gt;` where E isn't IoError (those need a
        /// manual shim).
        /// </summary>
        private static TypeExpr? TryGetResultIoErrorTypeArg(TypeExpr? returnType)
        {
            if (returnType is NamedType { Name: "Result" } nt
                && nt.TypeArguments.Length == 2
                && nt.TypeArguments[1] is NamedType { Name: "IoError" })
            {
                return nt.TypeArguments[0];
            }
            return null;
        }

        /// <summary>
        /// If <paramref name="returnType"/> is `Option&lt;T&gt;`, return
        /// T. Used to detect the nil-check-wrapping shim body shape.
        /// </summary>
        private static TypeExpr? TryGetOptionTypeArg(TypeExpr? returnType)
        {
            if (returnType is NamedType { Name: "Option" } nt
                && nt.TypeArguments.Length == 1)
            {
                return nt.TypeArguments[0];
            }
            return null;
        }

        /// <summary>
        /// Emit the shim body for an extern "go" fn whose Overt return
        /// type is `Option&lt;T&gt;`. The Go-side bound function is
        /// assumed to return a nillable T (typically `*T` for some
        /// struct, or an interface, slice, map, channel). The shim
        /// nil-checks the result and wraps into Some / None.
        /// </summary>
        private void EmitOptionWrappedShimBody(
            ExternDecl ext, string? selector, string member, TypeExpr innerType, bool isInstance)
        {
            var innerGoType = LowerType(innerType);
            _sb.Append("\t__r := ");
            EmitShimCall(ext, selector, member, isInstance);
            _sb.AppendLine();
            _sb.AppendLine("\tif __r == nil {");
            _sb.AppendLine($"\t\treturn overt.None[{innerGoType}]()");
            _sb.AppendLine("\t}");
            _sb.AppendLine($"\treturn overt.Some[{innerGoType}](__r)");
        }

        /// <summary>
        /// Emit the shim body for an extern "go" fn whose Overt return
        /// type is `Result&lt;T, IoError&gt;`. The Go-side bound
        /// function is assumed to return either `(T, error)` (when T
        /// isn't Unit) or `error` (when T is Unit). The shim does the
        /// err-check and wraps into Result on either branch. Same
        /// shape works for static and instance externs; the difference
        /// is only in how the underlying call is written.
        /// </summary>
        private void EmitResultWrappedShimBody(
            ExternDecl ext, string? selector, string member, TypeExpr innerType, bool isInstance)
        {
            var innerGoType = LowerType(innerType);
            var isUnit = innerType is UnitType;

            if (isUnit)
            {
                // `error`-only return shape:
                //   if err := <call>; err != nil {
                //       return overt.Err[overt.Unit, overt.IoError](
                //           overt.IoError{Narrative: err.Error()})
                //   }
                //   return overt.Ok[overt.Unit, overt.IoError](overt.UnitValue)
                _sb.Append("\tif err := ");
                EmitShimCall(ext, selector, member, isInstance);
                _sb.AppendLine("; err != nil {");
                _sb.AppendLine($"\t\treturn overt.Err[overt.Unit, overt.IoError](overt.IoError{{Narrative: err.Error()}})");
                _sb.AppendLine("\t}");
                _sb.AppendLine($"\treturn overt.Ok[overt.Unit, overt.IoError](overt.UnitValue)");
                return;
            }

            // `(T, error)` return shape:
            //   __r, err := <call>
            //   if err != nil {
            //       return overt.Err[T, overt.IoError](
            //           overt.IoError{Narrative: err.Error()})
            //   }
            //   return overt.Ok[T, overt.IoError](__r)
            _sb.Append("\t__r, err := ");
            EmitShimCall(ext, selector, member, isInstance);
            _sb.AppendLine();
            _sb.AppendLine("\tif err != nil {");
            _sb.AppendLine($"\t\treturn overt.Err[{innerGoType}, overt.IoError](overt.IoError{{Narrative: err.Error()}})");
            _sb.AppendLine("\t}");
            _sb.AppendLine($"\treturn overt.Ok[{innerGoType}, overt.IoError](__r)");
        }

        /// <summary>
        /// Split a binds target like <c>"fmt.Println"</c> or
        /// <c>"package.SubMember"</c> at the LAST dot. The selector
        /// is the package-as-used (Go's `pkg.Func` syntax); the
        /// member is the function or value name. Pure-name bindings
        /// (no dot) are not currently supported and surface as a
        /// helpful error rather than silent miscompilation.
        /// </summary>
        private static (string Selector, string Member) SplitBindsTarget(string target)
        {
            var lastDot = target.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == target.Length - 1)
            {
                throw new NotSupportedException(
                    "Go back end requires `binds \"package.Member\"`-shaped target; "
                    + $"got `{target}`. Use the `from \"<full/import/path>\"` clause for "
                    + "non-stdlib packages.");
            }
            return (target[..lastDot], target[(lastDot + 1)..]);
        }

        /// <summary>
        /// Walk an Overt fn's parameter and return types, collect the
        /// names from <see cref="FunctionDecl.TypeParameters"/> that
        /// actually appear as type references, and return them in
        /// declaration order. The remainder are effect-row variables
        /// (mentioned only inside `!{...}` rows, not as types) and
        /// don't get a Go type-parameter slot.
        /// </summary>
        private static List<string> CollectUsedTypeVariables(FunctionDecl fn)
        {
            if (fn.TypeParameters.Length == 0) return new List<string>();
            var declared = new HashSet<string>(fn.TypeParameters, StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in fn.Parameters)
            {
                WalkTypeExprForTypeVars(p.Type, declared, used);
            }
            WalkTypeExprForTypeVars(fn.ReturnType, declared, used);
            var ordered = new List<string>();
            foreach (var name in fn.TypeParameters)
            {
                if (used.Contains(name)) ordered.Add(name);
            }
            return ordered;
        }

        private static void WalkTypeExprForTypeVars(
            TypeExpr? type, HashSet<string> declared, HashSet<string> used)
        {
            switch (type)
            {
                case null:
                    return;
                case NamedType nt:
                    if (nt.TypeArguments.Length == 0 && declared.Contains(nt.Name))
                    {
                        used.Add(nt.Name);
                    }
                    foreach (var ta in nt.TypeArguments)
                    {
                        WalkTypeExprForTypeVars(ta, declared, used);
                    }
                    return;
                case FunctionType ft:
                    foreach (var pt in ft.Parameters)
                    {
                        WalkTypeExprForTypeVars(pt, declared, used);
                    }
                    WalkTypeExprForTypeVars(ft.ReturnType, declared, used);
                    return;
                default:
                    return;
            }
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
        /// Emit `type Name = <Base>` for non-generic refinement-type
        /// aliases. The `where` predicate is dropped — refinement
        /// runtime checks aren't injected on the Go target yet, so
        /// the alias is purely a name for the base type. Generic
        /// aliases skip emission entirely; LowerType substitutes
        /// their Target with type-arg substitution at every use
        /// site instead.
        /// </summary>
        private void EmitTypeAlias(TypeAliasDecl ta)
        {
            if (ta.TypeParameters.Length > 0)
            {
                // Substituted at use sites via LowerType. Nothing to
                // emit at the declaration.
                return;
            }
            _sb.Append($"type {ta.Name} = ");
            _sb.AppendLine(LowerType(ta.Target));
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
            NamedType { Name: "TraceEvent" } => "overt.TraceEvent",
            // Opaque host types: declared by `extern "go" type N binds
            // "..."`. The binds-string is the Go-side type expression
            // in import-path form (e.g. `*net/http.Request`); the
            // emitter rewrites it to use-site form (`*http.Request`)
            // where the package is referenced by its declared name,
            // not its import path. See docs/ffi.md §2 and OpaqueTypeToGo.
            NamedType nt when nt.TypeArguments.Length == 0
                && _externTypes.TryGetValue(nt.Name, out var binds) => OpaqueTypeToGo(binds),
            // Generic refinement alias: `NonEmpty<String>` substitutes
            // the alias's Target with String for every reference to
            // the alias's type parameter, then lowers the result.
            // This avoids relying on Go's generic-alias support which
            // varies across versions. See refinement.ov for the
            // canonical case.
            NamedType nt when nt.TypeArguments.Length > 0
                && _typeAliases.TryGetValue(nt.Name, out var galias)
                && galias.TypeParameters.Length == nt.TypeArguments.Length
                => LowerType(SubstituteTypeArgs(galias.Target, galias.TypeParameters, nt.TypeArguments)),
            // Non-generic refinement alias used as a name reference
            // (`Age`, `Percent`): keep the alias's source name, since
            // EmitTypeAlias will have declared `type Age = int` etc.
            // at the top of the module.
            NamedType nt when nt.TypeArguments.Length == 0
                && _typeAliases.TryGetValue(nt.Name, out var alias)
                && alias.TypeParameters.Length == 0
                => nt.Name,
            // Function types: lower to Go's `func(P1, P2, ...) R`. Effect
            // rows erase at the FFI boundary (Go has no concept of them);
            // a `()`-returning Overt fn becomes a Go fn with no return
            // slot. See docs/ffi.md §4.
            FunctionType ft => LowerFunctionType(ft),
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
                // Peel TraceExpr wrappers off the trailing expression.
                // With no Trace.subscribe consumer attached, a `trace
                // { ... }` block is zero-cost pass-through; the
                // emitter just emits the inner expression at the same
                // position. A trace body with leading statements is
                // not yet supported (would require flattening into
                // the outer block's statement list).
                var trailing = block.TrailingExpression;
                while (trailing is TraceExpr te)
                {
                    if (te.Body.Statements.Length > 0)
                    {
                        throw new NotSupportedException(
                            "Go back end does not yet handle trace blocks with "
                            + "leading statements; only single-trailing-expression "
                            + "trace bodies are supported.");
                    }
                    if (te.Body.TrailingExpression is null)
                    {
                        // `trace { }` with no body is a Unit-valued
                        // no-op. Erase it.
                        return;
                    }
                    trailing = te.Body.TrailingExpression;
                }
                _sb.Append(pad);
                if (trailing is PropagateExpr trailingPe)
                {
                    EmitPropagate(trailingPe, indent);
                    _sb.AppendLine();
                    return;
                }
                if (asReturn && trailing is IfExpr trailingIf)
                {
                    EmitIfAsReturn(trailingIf, indent);
                    _sb.AppendLine();
                    return;
                }
                // A trailing pipe chain that includes `|>?` somewhere
                // needs to hoist the propagated stage(s) to temps,
                // emit the early-return on Err, then emit the rest of
                // the chain as nested calls. This is the only place
                // `|>?` is currently handled; using it in non-trailing
                // expression positions throws via the bare BinaryExpr
                // path below.
                if (asReturn && IsPipeChainContainingPropagate(trailing, out var pipeChain))
                {
                    EmitPipeChainAsReturn(pipeChain, indent);
                    return;
                }
                if (asReturn && trailing is RaceExpr trailingRace)
                {
                    EmitRaceAsReturn(trailingRace, indent);
                    return;
                }
                if (trailing is MatchExpr trailingMatch)
                {
                    // Match in trailing position. asReturn flag passes
                    // through: return-position fn body recurses with
                    // each arm becoming a return; statement-position
                    // (Unit-returning fn body, or non-final block use)
                    // emits the match as a control-flow statement
                    // with arm bodies as plain statements.
                    EmitMatch(trailingMatch, indent, asReturn);
                    _sb.AppendLine();
                    return;
                }
                if (trailing is WhileExpr trailingWhile)
                {
                    EmitWhile(trailingWhile, indent);
                    _sb.AppendLine();
                    return;
                }
                if (trailing is LoopExpr trailingLoop)
                {
                    EmitLoop(trailingLoop, indent);
                    _sb.AppendLine();
                    return;
                }
                if (trailing is ForEachExpr trailingForEach)
                {
                    EmitForEach(trailingForEach, indent);
                    _sb.AppendLine();
                    return;
                }
                if (trailing is IfExpr trailingIfStmt && !asReturn)
                {
                    // If-as-trailing in a Unit-typed body. The earlier
                    // path only handled return-position (asReturn = true).
                    EmitIfStatement(trailingIfStmt, indent);
                    _sb.AppendLine();
                    return;
                }
                if (asReturn && trailing is WithExpr trailingWith)
                {
                    var tempName = EmitWithExprAsTemp(trailingWith, indent);
                    _sb.Append(new string('\t', indent));
                    _sb.Append("return ");
                    _sb.AppendLine(tempName);
                    return;
                }
                // Pre-hoist any embedded WithExpr in the trailing
                // expression. They can't be emitted inline (the
                // lowering is multi-statement); instead each one
                // becomes a temp on a preceding line, and
                // EmitExpression substitutes the temp's name at the
                // WithExpr's site. The pad we already wrote becomes
                // the lead-in for the FIRST hoist line; if no
                // hoists are needed, it's the lead-in for the line
                // we're about to emit.
                if (PreHoistEmbeddedWithExprs(trailing, indent, padAlreadyWritten: true))
                {
                    // Hoist consumed the pre-written pad; we need a
                    // fresh one for the actual line.
                    _sb.Append(pad);
                }
                if (asReturn)
                {
                    _sb.Append("return ");
                }
                EmitExpression(trailing);
                _sb.AppendLine();
                _withHoists.Clear();
            }
        }

        private void EmitStatement(Statement stmt, int indent)
        {
            switch (stmt)
            {
                case ExpressionStmt es:
                    // `match X { arm => body }?` rewrites to
                    // `match X { arm => body? }` — push the propagate
                    // into each arm. Same semantics; avoids needing a
                    // value-form match lowering (var temp; switch
                    // arms assign to temp; if !temp.IsOk return Err).
                    // Only valid when the match's value is unused
                    // (statement position), which is what the
                    // ExpressionStmt wrapper guarantees.
                    if (es.Expression is PropagateExpr { Operand: MatchExpr matchOp } pmatch)
                    {
                        var rewrittenArms = matchOp.Arms
                            .Select(arm => new MatchArm(
                                arm.Pattern,
                                new PropagateExpr(arm.Body, arm.Body.Span),
                                arm.Span))
                            .ToImmutableArray();
                        var rewrittenMatch = new MatchExpr(
                            matchOp.Scrutinee, rewrittenArms, matchOp.Span);
                        EmitMatch(rewrittenMatch, indent, asReturn: false);
                        break;
                    }
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
                    else if (es.Expression is WhileExpr we)
                    {
                        EmitWhile(we, indent);
                    }
                    else if (es.Expression is LoopExpr le)
                    {
                        EmitLoop(le, indent);
                    }
                    else
                    {
                        EmitExpression(es.Expression);
                    }
                    break;

                case BreakStmt:
                    _sb.Append("break");
                    break;

                case ContinueStmt:
                    _sb.Append("continue");
                    break;

                case LetStmt ls:
                    EmitLet(ls, indent);
                    break;

                case AssignmentStmt asn:
                    EmitAssignment(asn, indent);
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

        private void EmitLet(LetStmt ls, int indent)
        {
            // `let _ = expr` discards the value. Lowers to Go's `_ = expr`
            // form, which Go accepts at statement level for any
            // expression. Useful for opaque-host-type round-trips where
            // we want to evaluate the call but don't need the result.
            if (ls.Target is WildcardPattern)
            {
                _sb.Append("_ = ");
                EmitExpression(ls.Initializer);
                return;
            }
            // `let (a, b, ...) = parallel { e1, e2, ... }[?]` is the
            // canonical shape for parallel task groups. Lower to a
            // goroutine-per-task fan-out + WaitGroup join, then bind
            // each tuple slot to the corresponding result. The
            // optional `?` propagates the first Err.
            if (ls.Target is TuplePattern tp
                && TryUnwrapParallel(ls.Initializer, out var parallelExpr, out var hasPropagate)
                && tp.Elements.Length == parallelExpr.Tasks.Length)
            {
                EmitParallelLetTuple(tp, parallelExpr, hasPropagate, indent);
                return;
            }
            if (ls.Target is not IdentifierPattern ip)
            {
                throw new NotSupportedException(
                    "Go back end does not yet handle destructuring let; "
                    + $"got pattern {ls.Target.GetType().Name}.");
            }
            // `let x: T = expr?` lowers in two pieces: hoist the
            // ?-propagate (which writes the temp + the early-return
            // for the Err arm), then bind the let's name to the
            // temp's Value field. Without this special case the
            // PropagateExpr would fall through to EmitExpression,
            // which has no way to lower a statement-shaped hoist
            // into an expression slot.
            if (ls.Initializer is PropagateExpr pe)
            {
                EmitPropagateHoist(pe, indent, out var tempName);
                _sb.AppendLine();
                _sb.Append(new string('\t', indent));
                _sb.Append("var ");
                _sb.Append(ip.Name);
                if (ls.Type is not null)
                {
                    _sb.Append(' ');
                    _sb.Append(LowerType(ls.Type));
                }
                _sb.Append(" = ");
                _sb.Append(tempName);
                _sb.Append(".Value");
                return;
            }
            // `let x: T = target with { ... }` — multi-statement
            // hoist of the with-expression, then bind x to the
            // resulting temp.
            if (ls.Initializer is WithExpr we)
            {
                var withTemp = EmitWithExprAsTemp(we, indent);
                _sb.Append(new string('\t', indent));
                _sb.Append("var ");
                _sb.Append(ip.Name);
                if (ls.Type is not null)
                {
                    _sb.Append(' ');
                    _sb.Append(LowerType(ls.Type));
                }
                _sb.Append(" = ");
                _sb.Append(withTemp);
                return;
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

        /// <summary>
        /// Lower `name = expr` to Go's `name = expr`. The Overt-side
        /// resolver has already verified `name` was declared with
        /// `let mut`; the emitter just trusts that. WithExpr on the
        /// RHS is multi-statement-shape, so it pre-hoists into a
        /// temp before the assignment line.
        /// </summary>
        private void EmitAssignment(AssignmentStmt asn, int indent)
        {
            if (asn.Value is WithExpr we)
            {
                var temp = EmitWithExprAsTemp(we, indent);
                _sb.Append(asn.Name);
                _sb.Append(" = ");
                _sb.Append(temp);
                return;
            }
            _sb.Append(asn.Name);
            _sb.Append(" = ");
            EmitExpression(asn.Value);
        }

        /// <summary>
        /// Lower `while cond { body }` to Go's `for cond { body }`.
        /// Go has no `while` keyword; the bare-condition `for` is
        /// the same construct. Body emits as a statement block (the
        /// while-expression's value is always Unit).
        /// </summary>
        private void EmitWhile(WhileExpr we, int indent)
        {
            var pad = new string('\t', indent);
            _sb.Append("for ");
            EmitExpression(we.Condition);
            _sb.AppendLine(" {");
            EmitBlock(we.Body, indent + 1, asReturn: false);
            _sb.Append(pad);
            _sb.Append('}');
        }

        /// <summary>
        /// Lower `loop { body }` to Go's bare `for { body }`. Same
        /// construct as `while true`; the loop terminates only when
        /// the body executes a `break` or the surrounding fn returns.
        /// </summary>
        private void EmitLoop(LoopExpr le, int indent)
        {
            var pad = new string('\t', indent);
            _sb.AppendLine("for {");
            EmitBlock(le.Body, indent + 1, asReturn: false);
            _sb.Append(pad);
            _sb.Append('}');
        }

        /// <summary>
        /// Pre-emit hoists for any WithExpr embedded in <paramref name="e"/>
        /// (excluding `e` itself if it IS a WithExpr — that case is
        /// handled by the caller's own dispatch, e.g. as a let-init or
        /// trailing-return). Each hoist becomes a `__w_N := target;
        /// __w_N.f = v; ...` block at the current indent level.
        /// Records (span → temp name) in <see cref="_withHoists"/>
        /// so EmitExpression can substitute the temp name when it
        /// reaches the WithExpr's site.
        ///
        /// <paramref name="padAlreadyWritten"/> indicates whether the
        /// caller has written one pad on _sb (the standard
        /// statement-emit contract). Returns true if any hoist was
        /// emitted; in that case the caller's pre-written pad has
        /// been consumed and the caller needs a fresh pad for its
        /// continuation line. Returns false if no hoist was needed,
        /// in which case the pre-written pad is still in place.
        /// </summary>
        private bool PreHoistEmbeddedWithExprs(Expression e, int indent, bool padAlreadyWritten)
        {
            var found = new List<WithExpr>();
            CollectEmbeddedWithExprs(e, found);
            if (found.Count == 0) return false;
            var pad = new string('\t', indent);
            for (var i = 0; i < found.Count; i++)
            {
                if (i > 0 || !padAlreadyWritten)
                {
                    _sb.Append(pad);
                }
                var temp = EmitWithExprAsTemp(found[i], indent);
                _withHoists[found[i].Span] = temp;
            }
            return true;
        }

        /// <summary>
        /// Walk an expression tree, collect every WithExpr it contains
        /// (excluding the root if the root IS a WithExpr — those go
        /// through the caller's own dispatch). Doesn't recurse into a
        /// WithExpr's own children: <see cref="EmitWithExprAsTemp"/>
        /// handles nested-WithExpr-in-field-init internally.
        /// </summary>
        private static void CollectEmbeddedWithExprs(Expression e, List<WithExpr> found)
        {
            CollectInner(e, isRoot: true);

            void CollectInner(Expression node, bool isRoot)
            {
                switch (node)
                {
                    case WithExpr we:
                        if (!isRoot) found.Add(we);
                        // Don't recurse: EmitWithExprAsTemp handles nested.
                        return;
                    case CallExpr c:
                        CollectInner(c.Callee, isRoot: false);
                        foreach (var a in c.Arguments) CollectInner(a.Value, isRoot: false);
                        return;
                    case BinaryExpr be:
                        CollectInner(be.Left, isRoot: false);
                        CollectInner(be.Right, isRoot: false);
                        return;
                    case UnaryExpr ue:
                        CollectInner(ue.Operand, isRoot: false);
                        return;
                    case FieldAccessExpr fa:
                        CollectInner(fa.Target, isRoot: false);
                        return;
                    case PropagateExpr pe:
                        CollectInner(pe.Operand, isRoot: false);
                        return;
                    case RecordLiteralExpr rl:
                        foreach (var f in rl.Fields) CollectInner(f.Value, isRoot: false);
                        return;
                    case InterpolatedStringExpr ie:
                        foreach (var p in ie.Parts)
                        {
                            if (p is StringInterpolationPart ip)
                            {
                                CollectInner(ip.Expression, isRoot: false);
                            }
                        }
                        return;
                    // Leaves and language constructs whose bodies are
                    // their own statement scope (Block, If, Match,
                    // Loop, While, ForEach, etc.) don't contribute
                    // hoists at the enclosing line level — they have
                    // their own pre-hoist boundaries.
                    default:
                        return;
                }
            }
        }

        /// <summary>
        /// Lower `target with { f1 = v1, f2 = v2, ... }` to a
        /// temp-and-mutate sequence:
        ///
        ///   __w_N := target
        ///   __w_N.f1 = v1
        ///   __w_N.f2 = v2
        ///
        /// Returns the temp's name so the caller can use it as an
        /// expression in the slot the with-expression occupied.
        /// Nested WithExprs in field-init values are pre-hoisted
        /// recursively, so the outer's field assignments reference
        /// the inner temps by name.
        ///
        /// Calling contract (matches EmitPropagateHoist):
        ///   - caller writes one `pad` (`indent` tabs) on _sb before
        ///     invocation
        ///   - helper consumes that pad for its first emitted line
        ///   - helper writes its own pads for every subsequent line
        ///   - helper ends with cursor at column 0 (no trailing pad)
        ///   - caller is responsible for writing pad on the next line
        ///     before its continuation
        /// </summary>
        private string EmitWithExprAsTemp(WithExpr we, int indent)
        {
            var pad = new string('\t', indent);

            // Pre-hoist nested WithExprs first. The first one consumes
            // the caller-supplied pad; subsequent ones write their own.
            var hoisted = new Dictionary<string, string>(StringComparer.Ordinal);
            var consumedCallerPad = false;
            foreach (var fi in we.Updates)
            {
                if (fi.Value is WithExpr nested)
                {
                    if (consumedCallerPad)
                    {
                        _sb.Append(pad);
                    }
                    consumedCallerPad = true;
                    hoisted[fi.Name] = EmitWithExprAsTemp(nested, indent);
                }
            }

            // Outer with's first line. If we had any pre-hoists, we
            // need fresh pad here (since the helper recursion left us
            // at column 0). If we didn't, the caller-supplied pad is
            // still on _sb and we use it directly.
            if (consumedCallerPad)
            {
                _sb.Append(pad);
            }
            var tempName = $"__w_{_qCounter++}";
            _sb.Append(tempName);
            _sb.Append(" := ");
            EmitExpression(we.Target);
            _sb.AppendLine();
            foreach (var fi in we.Updates)
            {
                _sb.Append(pad);
                _sb.Append(tempName);
                _sb.Append('.');
                _sb.Append(fi.Name);
                _sb.Append(" = ");
                if (hoisted.TryGetValue(fi.Name, out var nestedTemp))
                {
                    _sb.AppendLine(nestedTemp);
                }
                else
                {
                    EmitExpression(fi.Value);
                    _sb.AppendLine();
                }
            }
            return tempName;
        }

        /// <summary>
        /// Try to unwrap an expression that is either a bare ParallelExpr
        /// or a PropagateExpr wrapping one. Returns the inner ParallelExpr
        /// and a flag indicating whether the `?` was present, or false
        /// when the expression doesn't fit either shape.
        /// </summary>
        private static bool TryUnwrapParallel(
            Expression e, out ParallelExpr parallel, out bool hasPropagate)
        {
            if (e is ParallelExpr pe)
            {
                parallel = pe;
                hasPropagate = false;
                return true;
            }
            if (e is PropagateExpr { Operand: ParallelExpr pep })
            {
                parallel = pep;
                hasPropagate = true;
                return true;
            }
            parallel = null!;
            hasPropagate = false;
            return false;
        }

        /// <summary>
        /// Lower `let (a, b, ...) = parallel { e1, e2, ... }[?]` as a
        /// SEQUENTIAL fan-out: each task runs in order, results bind
        /// to tuple slots, optional `?` propagates the first Err.
        ///
        /// The Overt-side semantics promise concurrent execution; the
        /// Go target satisfies the SHAPE (fan-out + join) but not
        /// the parallelism today. A real concurrent lowering would
        /// declare per-task Result temps with the type-checker's
        /// inferred types, spawn goroutines, and join with a
        /// WaitGroup. That requires plumbing TypeCheckResult into
        /// the GoEmitter and is gated on the language-arc
        /// concurrency design (docs/concurrency.md). For now, the
        /// sequential lowering compiles and produces semantically
        /// correct results — just slower than promised.
        /// </summary>
        private void EmitParallelLetTuple(
            TuplePattern tp, ParallelExpr parallel, bool hasPropagate, int indent)
        {
            var n = parallel.Tasks.Length;
            var pad = new string('\t', indent);
            var resultNames = new string[n];
            for (var i = 0; i < n; i++)
            {
                resultNames[i] = $"__par_{_qCounter++}";
                if (i > 0) _sb.Append(pad);
                _sb.Append(resultNames[i]);
                _sb.Append(" := ");
                EmitExpression(parallel.Tasks[i]);
                _sb.AppendLine();
            }
            if (hasPropagate)
            {
                for (var i = 0; i < n; i++)
                {
                    _sb.Append(pad);
                    _sb.AppendLine($"if !{resultNames[i]}.IsOk {{");
                    _sb.Append(pad);
                    _sb.Append("\treturn overt.Err[");
                    EmitCurrentReturnTypeArgs();
                    _sb.AppendLine($"]({resultNames[i]}.Err)");
                    _sb.Append(pad);
                    _sb.AppendLine("}");
                }
            }
            for (var i = 0; i < n; i++)
            {
                if (tp.Elements[i] is not IdentifierPattern slot)
                {
                    throw new NotSupportedException(
                        "Go back end's parallel-let-tuple binding requires "
                        + "identifier-pattern slots; nested patterns aren't supported yet.");
                }
                _sb.Append(pad);
                _sb.Append(slot.Name);
                _sb.Append(" := ");
                _sb.Append(resultNames[i]);
                if (hasPropagate)
                {
                    _sb.Append(".Value");
                }
                if (i < n - 1) _sb.AppendLine();
                // Last assignment: don't emit a trailing newline; the
                // caller's EmitBlock writes one after EmitStatement.
            }
        }

        /// <summary>
        /// Lower a trailing `race { e1, e2, ... }` (or
        /// `race { ... }?`) in return position to sequential
        /// "try each, return first Ok" form. Real first-success
        /// concurrency is gated on the same plumbing as
        /// EmitParallelLetTuple and lands with the language-arc
        /// concurrency design.
        /// </summary>
        private void EmitRaceAsReturn(RaceExpr race, int indent)
        {
            var pad = new string('\t', indent);
            var n = race.Tasks.Length;
            if (n == 0)
            {
                throw new NotSupportedException("Go back end requires race { ... } to have at least one task.");
            }
            var resultNames = new string[n];
            for (var i = 0; i < n; i++)
            {
                resultNames[i] = $"__race_{_qCounter++}";
                if (i > 0) _sb.Append(pad);
                _sb.Append(resultNames[i]);
                _sb.Append(" := ");
                EmitExpression(race.Tasks[i]);
                _sb.AppendLine();
                _sb.Append(pad);
                _sb.AppendLine($"if {resultNames[i]}.IsOk {{");
                _sb.Append(pad);
                _sb.AppendLine($"\treturn {resultNames[i]}");
                _sb.Append(pad);
                _sb.AppendLine("}");
            }
            // All failed: return the first Err.
            _sb.Append(pad);
            _sb.Append("return ");
            _sb.AppendLine(resultNames[0]);
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
            // Stdlib enums (Result, Option) are concrete struct types in
            // the Go runtime, not interfaces with sealed variants, so
            // they need an `if .IsOk { ... } else { ... }`-shape lowering
            // rather than a type switch. Detect that shape first.
            if (TryResolveStdlibShape(me) is { } stdlib)
            {
                EmitMatchStdlibShape(me, stdlib, indent, asReturn);
                return;
            }
            // Tuple-of-enums match: `match (a, b) { (EnumA.X, EnumB.Y)
            // => ..., _ => ... }`. Lowers to a nested type switch
            // (outer on a, inner on b). The trailing wildcard arm
            // becomes the default for every level.
            if (me.Scrutinee is TupleExpr tupleScrut
                && TryResolveTupleEnumNames(me, tupleScrut.Elements.Length) is { } tupleEnums)
            {
                EmitMatchTuple(me, tupleScrut, tupleEnums, indent, asReturn);
                return;
            }
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

        /// <summary>
        /// Detect whether a match's arms are stdlib-Result-shaped
        /// (`Ok(...)` / `Err(...)`) or stdlib-Option-shaped
        /// (`Some(...)` / `None`). Returns the shape name or null when
        /// the arms are some other kind. Detection is structural: we
        /// don't carry the type-checker's resolved scrutinee type
        /// here, so the heuristic looks at single-segment variant
        /// references on each arm. A user enum that happens to call
        /// its variants `Ok` / `Err` would collide with this; that's
        /// rare enough to defer until it bites.
        /// </summary>
        private static string? TryResolveStdlibShape(MatchExpr me)
        {
            var anyResult = false;
            var anyOption = false;
            foreach (var arm in me.Arms)
            {
                var (name, isSingle) = ExtractSingleSegmentName(arm.Pattern);
                if (!isSingle) continue;
                if (name is "Ok" or "Err") anyResult = true;
                if (name is "Some" or "None") anyOption = true;
            }
            if (anyResult && !anyOption) return "Result";
            if (anyOption && !anyResult) return "Option";
            return null;
        }

        private static (string? Name, bool IsSingle) ExtractSingleSegmentName(Pattern p)
        {
            return p switch
            {
                IdentifierPattern ip => (ip.Name, true),
                PathPattern pp when pp.Path.Length == 1 => (pp.Path[0], true),
                ConstructorPattern cp when cp.Path.Length == 1 => (cp.Path[0], true),
                RecordPattern rp when rp.Path.Length == 1 => (rp.Path[0], true),
                _ => (null, false),
            };
        }

        /// <summary>
        /// Emit a match on a stdlib Result or Option scrutinee as
        /// `__m := scrut; if __m.IsOk { ... } else { ... }` (or
        /// IsSome for Option). Each arm classified by its variant
        /// name; bindings extracted from the variant's data field
        /// (`__m.Value` for Ok / Some, `__m.Err` for Err). Wildcard
        /// bindings discard the value with `_`.
        /// </summary>
        private void EmitMatchStdlibShape(MatchExpr me, string shape, int indent, bool asReturn)
        {
            var pad = new string('\t', indent);
            var scrutVar = $"__m_{_qCounter++}";
            // Variant → (binderName?, isWildcard, body) for the two arms
            string? trueArmBinder = null;
            Expression? trueArmBody = null;
            string? falseArmBinder = null;
            Expression? falseArmBody = null;
            foreach (var arm in me.Arms)
            {
                var (name, _) = ExtractSingleSegmentName(arm.Pattern);
                var binder = ExtractFirstArgBinder(arm.Pattern);
                bool isTrueArm = name is "Ok" or "Some";
                if (isTrueArm)
                {
                    trueArmBinder = binder;
                    trueArmBody = arm.Body;
                }
                else
                {
                    falseArmBinder = binder;
                    falseArmBody = arm.Body;
                }
            }
            if (trueArmBody is null || falseArmBody is null)
            {
                throw new NotSupportedException(
                    $"Go back end {shape} match must cover both arms; "
                    + "wildcard arms aren't yet wired here.");
            }

            var probeField = shape == "Result" ? "IsOk" : "IsSome";
            var trueValueField = "Value";
            var falseValueField = shape == "Result" ? "Err" : null;

            _sb.Append($"{scrutVar} := ");
            EmitExpression(me.Scrutinee);
            _sb.AppendLine();
            _sb.Append(pad);
            _sb.Append($"if {scrutVar}.{probeField} {{");
            _sb.AppendLine();
            if (trueArmBinder is not null)
            {
                _sb.Append(pad);
                _sb.AppendLine($"\t{trueArmBinder} := {scrutVar}.{trueValueField}");
            }
            _sb.Append(pad);
            _sb.Append('\t');
            EmitArmBody(trueArmBody, indent + 1, asReturn);
            _sb.Append(pad);
            _sb.AppendLine("} else {");
            if (falseArmBinder is not null && falseValueField is not null)
            {
                _sb.Append(pad);
                _sb.AppendLine($"\t{falseArmBinder} := {scrutVar}.{falseValueField}");
            }
            else
            {
                // Wildcard arm or Option-None — silence the unused
                // variable warning so `go build` accepts the block.
                _sb.Append(pad);
                _sb.AppendLine($"\t_ = {scrutVar}");
            }
            _sb.Append(pad);
            _sb.Append('\t');
            EmitArmBody(falseArmBody, indent + 1, asReturn);
            _sb.Append(pad);
            _sb.Append('}');
        }

        /// <summary>Best-effort extraction of the first ConstructorPattern
        /// argument as a name (`Ok(x)` → "x"), or null when the pattern
        /// has no binder (`Err(_)`, `None`, bare `Ok`).</summary>
        private static string? ExtractFirstArgBinder(Pattern p)
        {
            if (p is ConstructorPattern cp && cp.Arguments.Length >= 1)
            {
                return cp.Arguments[0] switch
                {
                    IdentifierPattern ip => ip.Name,
                    _ => null,
                };
            }
            return null;
        }

        /// <summary>
        /// For a match on a tuple scrutinee, infer one enum name per
        /// position by scanning the arms' tuple-pattern paths. Returns
        /// the array of names if every position is an enum the module
        /// declares; null otherwise (in which case the standard
        /// single-enum lowering will fail with its own diagnostic).
        /// </summary>
        private string[]? TryResolveTupleEnumNames(MatchExpr me, int arity)
        {
            var names = new string?[arity];
            foreach (var arm in me.Arms)
            {
                if (arm.Pattern is not TuplePattern tp || tp.Elements.Length != arity) continue;
                for (var i = 0; i < arity; i++)
                {
                    if (names[i] is not null) continue;
                    var sub = tp.Elements[i];
                    var path = sub switch
                    {
                        PathPattern p => p.Path,
                        RecordPattern r => r.Path,
                        ConstructorPattern c => c.Path,
                        _ => default,
                    };
                    if (!path.IsDefault && path.Length == 2 && _enums.ContainsKey(path[0]))
                    {
                        names[i] = path[0];
                    }
                }
            }
            for (var i = 0; i < arity; i++)
            {
                if (names[i] is null) return null;
            }
            return names!;
        }

        /// <summary>
        /// Lower a `match (a, b) { (EnumA.X, EnumB.Y) => body, ... }`
        /// expression to nested Go type switches. The outer switch
        /// dispatches on the first scrutinee-element's variant; for
        /// each outer case, an inner switch dispatches on the
        /// remaining element. A trailing wildcard arm becomes the
        /// default for every inner switch and the outer.
        ///
        /// The lowering is strictly more verbose than a single-level
        /// switch, but Go has no tuple-pattern construct and the
        /// alternative (synthesized string keys) costs a string
        /// allocation per dispatch. The nested-switch shape compiles
        /// to roughly the same machine code Go generates for the
        /// human-written equivalent.
        /// </summary>
        private void EmitMatchTuple(
            MatchExpr me,
            TupleExpr tupleScrut,
            string[] enumNames,
            int indent,
            bool asReturn)
        {
            if (tupleScrut.Elements.Length != 2)
            {
                throw new NotSupportedException(
                    "Go back end currently only handles 2-tuple match scrutinees; "
                    + $"got arity {tupleScrut.Elements.Length}.");
            }
            // Bucket each arm by its (outer-variant, inner-variant)
            // pair. Wildcard is the catch-all at every level.
            var outerEnum = enumNames[0];
            var innerEnum = enumNames[1];
            // outerVariant → (innerVariant, body)[]
            var groups = new Dictionary<string, List<(string Inner, Expression Body)>>(StringComparer.Ordinal);
            Expression? wildcardBody = null;
            foreach (var arm in me.Arms)
            {
                if (arm.Pattern is WildcardPattern || arm.Pattern is IdentifierPattern)
                {
                    wildcardBody = arm.Body;
                    continue;
                }
                if (arm.Pattern is not TuplePattern tp || tp.Elements.Length != 2)
                {
                    throw new NotSupportedException(
                        "Go back end requires every non-wildcard arm of a tuple "
                        + "match to be a 2-tuple pattern; got "
                        + arm.Pattern.GetType().Name);
                }
                var outerVar = ExtractVariantOf(tp.Elements[0], outerEnum);
                var innerVar = ExtractVariantOf(tp.Elements[1], innerEnum);
                if (outerVar is null || innerVar is null)
                {
                    throw new NotSupportedException(
                        "Go back end tuple match requires per-element patterns to be "
                        + "qualified variant references like `EnumName.Variant`; "
                        + $"got {tp.Elements[0].GetType().Name} / {tp.Elements[1].GetType().Name}.");
                }
                if (!groups.TryGetValue(outerVar, out var list))
                {
                    list = new List<(string, Expression)>();
                    groups[outerVar] = list;
                }
                list.Add((innerVar, arm.Body));
            }

            var pad = new string('\t', indent);
            var outerVar2 = $"__mt_{_qCounter++}";
            var innerVar2 = $"__mt_{_qCounter++}";
            _sb.Append($"switch {outerVar2} := ");
            EmitExpression(tupleScrut.Elements[0]);
            _sb.AppendLine(".(type) {");
            foreach (var kvp in groups)
            {
                _sb.Append(pad);
                _sb.AppendLine($"case {outerEnum}_{kvp.Key}:");
                _sb.Append(pad);
                _sb.AppendLine($"\t_ = {outerVar2}");
                _sb.Append(pad);
                _sb.Append($"\tswitch {innerVar2} := ");
                EmitExpression(tupleScrut.Elements[1]);
                _sb.AppendLine(".(type) {");
                foreach (var (inner, body) in kvp.Value)
                {
                    _sb.Append(pad);
                    _sb.AppendLine($"\tcase {innerEnum}_{inner}:");
                    _sb.Append(pad);
                    _sb.AppendLine($"\t\t_ = {innerVar2}");
                    _sb.Append(pad);
                    _sb.Append("\t\t");
                    EmitArmBody(body, indent + 2, asReturn);
                }
                if (wildcardBody is not null)
                {
                    _sb.Append(pad);
                    _sb.AppendLine("\tdefault:");
                    _sb.Append(pad);
                    _sb.AppendLine($"\t\t_ = {innerVar2}");
                    _sb.Append(pad);
                    _sb.Append("\t\t");
                    EmitArmBody(wildcardBody, indent + 2, asReturn);
                }
                else
                {
                    _sb.Append(pad);
                    _sb.AppendLine("\tdefault:");
                    _sb.Append(pad);
                    _sb.AppendLine("\t\tpanic(\"unreachable: type checker proved tuple match exhaustive\")");
                }
                _sb.Append(pad);
                _sb.AppendLine("\t}");
            }
            if (wildcardBody is not null)
            {
                _sb.Append(pad);
                _sb.AppendLine("default:");
                _sb.Append(pad);
                _sb.AppendLine($"\t_ = {outerVar2}");
                _sb.Append(pad);
                _sb.Append('\t');
                EmitArmBody(wildcardBody, indent + 1, asReturn);
            }
            else
            {
                _sb.Append(pad);
                _sb.AppendLine("default:");
                _sb.Append(pad);
                _sb.AppendLine("\tpanic(\"unreachable: type checker proved tuple match exhaustive\")");
            }
            _sb.Append(pad);
            _sb.Append('}');
        }

        /// <summary>Extract the variant name from a single position of a
        /// tuple pattern, given the expected enum name. Returns null
        /// when the pattern shape isn't a qualified variant reference.</summary>
        private static string? ExtractVariantOf(Pattern p, string enumName)
        {
            ImmutableArray<string> path = p switch
            {
                PathPattern pp => pp.Path,
                RecordPattern rp => rp.Path,
                ConstructorPattern cp => cp.Path,
                _ => default,
            };
            if (!path.IsDefault && path.Length == 2 && path[0] == enumName)
            {
                return path[1];
            }
            return null;
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
            // A `()` arm body in statement position is a no-op. Without
            // this, the emitter would write `overt.UnitValue` as a
            // bare statement, which Go rejects (expressions aren't
            // valid statements). In return position, a Unit-typed
            // body still wants `return overt.UnitValue` so the fn
            // returns something; that case keeps the standard path.
            if (body is UnitExpr && !asReturn)
            {
                _sb.AppendLine();
                return;
            }
            // Pre-hoist embedded WithExprs the same way EmitBlock
            // does for trailing expressions. Caller has already
            // written one pad on _sb (the EmitMatch / EmitMatchTuple
            // paths Append('\t') before EmitArmBody); we consume it
            // for the first hoist line if any, then re-pad for the
            // actual return line.
            var pad = new string('\t', indent);
            if (PreHoistEmbeddedWithExprs(body, indent, padAlreadyWritten: true))
            {
                _sb.Append(pad);
            }
            if (asReturn)
            {
                _sb.Append("return ");
            }
            EmitExpression(body);
            _sb.AppendLine();
            _withHoists.Clear();
        }

        // ------------------------------------------------------ propagate

        private void EmitPropagate(PropagateExpr pe, int indent)
        {
            EmitPropagateHoist(pe, indent, out _);
        }

        /// <summary>
        /// Emit the `?`-propagation hoist (`__q_N := <op>; if
        /// !__q_N.IsOk { return ... }`) and report the temp's name
        /// via <paramref name="tempName"/> so callers (specifically
        /// <see cref="EmitLet"/>) can read `__q_N.Value` afterwards
        /// to bind the success branch's value to the let's target.
        /// The early-return type is derived from the enclosing fn's
        /// declared return type rather than hardcoded; that lets `?`
        /// work in fns returning any `Result&lt;T, E&gt;`, not just
        /// `Result&lt;(), IoError&gt;`.
        /// </summary>
        private void EmitPropagateHoist(PropagateExpr pe, int indent, out string tempName)
        {
            var pad = new string('\t', indent);
            tempName = $"__q_{_qCounter++}";
            _sb.Append($"{tempName} := ");
            EmitExpression(pe.Operand);
            _sb.AppendLine();
            _sb.Append(pad);
            _sb.AppendLine($"if !{tempName}.IsOk {{");
            _sb.Append(pad);
            _sb.Append("\treturn overt.Err[");
            EmitCurrentReturnTypeArgs();
            _sb.AppendLine($"]({tempName}.Err)");
            _sb.Append(pad);
            _sb.Append("}");
        }

        /// <summary>
        /// Emit `T, E` for the current fn's return type, where the fn
        /// returns `Result&lt;T, E&gt;`. Used by ?-propagation's
        /// early-return type-args. The type checker has already
        /// guaranteed `?` only appears inside a Result-returning fn,
        /// so a non-Result return here is a compiler bug.
        /// </summary>
        private void EmitCurrentReturnTypeArgs()
        {
            if (_currentFnReturnType is NamedType { Name: "Result" } nt
                && nt.TypeArguments.Length == 2)
            {
                _sb.Append(LowerType(nt.TypeArguments[0]));
                _sb.Append(", ");
                _sb.Append(LowerType(nt.TypeArguments[1]));
                return;
            }
            throw new InvalidOperationException(
                "?-propagation reached the emitter outside a Result-returning fn; "
                + "the type checker should have caught this.");
        }

        /// <summary>
        /// Emit the element type for a `List.empty()` call. Uses the
        /// enclosing fn's return type when it's `List&lt;T&gt;`-shaped;
        /// otherwise falls back to `any`, which compiles but loses
        /// type-precision and may surface as a usage error elsewhere.
        /// A future expected-type-threading pass would let let-init
        /// sites and call-arg positions also feed in the right T.
        /// </summary>
        private void EmitListElementTypeOrFallback()
        {
            if (_currentFnReturnType is NamedType { Name: "List" } nt
                && nt.TypeArguments.Length == 1)
            {
                _sb.Append(LowerType(nt.TypeArguments[0]));
                return;
            }
            // Common case: fn returns Result<List<T>, E> and the List.empty()
            // sits inside an Ok(...) wrap. The Ok-arg slot is List<T>, so
            // peel one layer through Result and reuse the same lookup.
            if (_currentFnReturnType is NamedType { Name: "Result" } rt
                && rt.TypeArguments.Length == 2
                && rt.TypeArguments[0] is NamedType { Name: "List" } innerList
                && innerList.TypeArguments.Length == 1)
            {
                _sb.Append(LowerType(innerList.TypeArguments[0]));
                return;
            }
            _sb.Append("any");
        }

        /// <summary>
        /// Emit `T, E` for an `Ok(...)` / `Err(...)` constructor call
        /// targeting the current fn's return type. Falls back to the
        /// `overt.Unit, overt.IoError` pair when the enclosing return
        /// isn't `Result&lt;T, E&gt;`-shaped (e.g. a call site outside
        /// any fn body, or a fn whose return is itself shaped through
        /// extern). The fallback isn't always semantically correct,
        /// but it matches the pre-target-typing behavior and keeps
        /// the existing curated tests green.
        /// </summary>
        private void EmitResultTypeArgsOrFallback()
        {
            if (_currentFnReturnType is NamedType { Name: "Result" } nt
                && nt.TypeArguments.Length == 2)
            {
                _sb.Append(LowerType(nt.TypeArguments[0]));
                _sb.Append(", ");
                _sb.Append(LowerType(nt.TypeArguments[1]));
                return;
            }
            _sb.Append("overt.Unit, overt.IoError");
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

                case FloatLiteralExpr fl:
                    // Go's float-literal syntax matches Overt's lexer
                    // output (decimal point, optional `e±N` exponent).
                    _sb.Append(fl.Lexeme);
                    break;

                case BooleanLiteralExpr bl:
                    _sb.Append(bl.Value ? "true" : "false");
                    break;

                case UnitExpr:
                    _sb.Append("overt.UnitValue");
                    break;

                case BinaryExpr be when be.Op == BinaryOp.PipeCompose:
                    // `x |> f(args)` rewrites to `f(x, args)`. If the
                    // RHS is a name (`|> Ok`), it becomes `Ok(x)`; if
                    // a call (`|> filter(p)`), `filter(x, p)`. Mirrors
                    // the C# back end's EmitPipe.
                    EmitPipeCompose(be);
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

                case WithExpr we when _withHoists.TryGetValue(we.Span, out var withTemp):
                    // Pre-hoisted by PreHoistEmbeddedWithExprs at the
                    // enclosing statement boundary; emit the temp's
                    // name in this slot.
                    _sb.Append(withTemp);
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
            // Constructors `Ok(x)` and `Err(e)` need explicit type
            // parameters because Go can't target-type generic calls
            // from the surrounding context. Use the enclosing fn's
            // declared return type (which the type checker has
            // already validated as Result<T, E>) to spell the
            // params. Falls back to the [Unit, IoError] hardcode for
            // calls outside any fn (rare; defensive).
            if (call.Callee is IdentifierExpr { Name: "Ok" } && call.Arguments.Length == 1)
            {
                _sb.Append("overt.Ok[");
                EmitResultTypeArgsOrFallback();
                _sb.Append("](");
                EmitExpression(call.Arguments[0].Value);
                _sb.Append(')');
                return;
            }
            if (call.Callee is IdentifierExpr { Name: "Err" } && call.Arguments.Length == 1)
            {
                _sb.Append("overt.Err[");
                EmitResultTypeArgsOrFallback();
                _sb.Append("](");
                EmitExpression(call.Arguments[0].Value);
                _sb.Append(')');
                return;
            }

            // Method-call syntax against an `extern "go" instance fn`:
            // `c.method(args)` parses as a FieldAccess callee but
            // should route to the shim `method(c, args)`, since
            // the shim is what holds the binds-target ↔ Go-method-
            // name mapping. Without this, the emitter would write
            // `c.method(...)` verbatim and Go would complain about
            // the case-mismatched method name.
            if (call.Callee is FieldAccessExpr fac
                && _instanceExterns.Contains(fac.FieldName))
            {
                _sb.Append(fac.FieldName);
                _sb.Append('(');
                EmitExpression(fac.Target);
                for (var i = 0; i < call.Arguments.Length; i++)
                {
                    _sb.Append(", ");
                    EmitExpression(call.Arguments[i].Value);
                }
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
                // `List.empty()` is generic with no value-typed arg, so
                // Go can't infer T. Thread the enclosing fn's return
                // type (when it's List<T>) into the explicit type-arg
                // slot. Without this, a fn shaped `-> List<Int>` whose
                // body returns `List.empty()` would fail Go's inference.
                if (nsId.Name == "List" && facCallee.FieldName == "empty"
                    && call.Arguments.Length == 0)
                {
                    _sb.Append("overt.ListEmpty[");
                    EmitListElementTypeOrFallback();
                    _sb.Append("]()");
                    return;
                }
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

        /// <summary>
        /// Detect whether <paramref name="e"/> is a left-leaning chain
        /// of pipe operators (mix of `|>` and `|>?`) that contains at
        /// least one `|>?`. The chain comes out reversed (innermost
        /// first) plus the original initial value at index 0.
        /// </summary>
        private static bool IsPipeChainContainingPropagate(
            Expression e, out List<(BinaryOp Op, Expression Right, Expression FullNode)> chain)
        {
            chain = new List<(BinaryOp, Expression, Expression)>();
            var hasPropagate = false;
            var node = e;
            while (node is BinaryExpr be
                && (be.Op == BinaryOp.PipeCompose || be.Op == BinaryOp.PipePropagate))
            {
                if (be.Op == BinaryOp.PipePropagate) hasPropagate = true;
                chain.Add((be.Op, be.Right, be));
                node = be.Left;
            }
            if (!hasPropagate || chain.Count == 0)
            {
                chain.Clear();
                return false;
            }
            chain.Reverse();
            // Stash the chain's initial value as a sentinel at index 0
            // by inserting a synthetic record. We just keep `node` for
            // the caller to use; chain.Reverse() above gave us
            // (op, rhs) in left-to-right order. The caller reads
            // `node` separately as the chain's seed value.
            // Repack: prepend a marker entry for the seed.
            chain.Insert(0, (default, node, node));
            return true;
        }

        /// <summary>
        /// Emit a trailing pipe chain (including at least one `|>?`)
        /// as a sequence of statements: hoist each `|>?` step into a
        /// Result temp, early-return on Err, then either thread the
        /// unwrapped value into the next step or emit the final
        /// `return <expr>` when the chain ends.
        ///
        /// Each step is one of:
        ///   - `|> rhs`:  current = rhs(current[, rhs.args])     (inline call)
        ///   - `|>? rhs`: temp = rhs(current[, ...]); if !temp.IsOk { return Err };
        ///                current = temp.Value
        ///
        /// The chain's first entry (index 0) is a sentinel carrying
        /// the seed value (the LHS of the leftmost pipe).
        /// </summary>
        private void EmitPipeChainAsReturn(
            List<(BinaryOp Op, Expression Right, Expression FullNode)> chain, int indent)
        {
            var pad = new string('\t', indent);
            // Capture the seed via a temp so subsequent steps can
            // reference it by name. Even when the seed is a single
            // identifier we hoist for uniformity.
            var seedName = $"__pp_{_qCounter++}";
            _sb.Append(seedName);
            _sb.Append(" := ");
            EmitExpression(chain[0].Right);
            _sb.AppendLine();
            var currentName = seedName;
            for (var i = 1; i < chain.Count; i++)
            {
                var (op, rhs, _) = chain[i];
                var stepName = $"__pp_{_qCounter++}";
                if (i == chain.Count - 1 && op == BinaryOp.PipeCompose)
                {
                    // Last step is an infallible pipe: emit as the
                    // function's `return <call>` directly, no temp.
                    _sb.Append(pad);
                    _sb.Append("return ");
                    EmitPipeStepCall(rhs, currentName);
                    _sb.AppendLine();
                    return;
                }
                _sb.Append(pad);
                _sb.Append(stepName);
                _sb.Append(" := ");
                EmitPipeStepCall(rhs, currentName);
                _sb.AppendLine();
                if (op == BinaryOp.PipePropagate)
                {
                    // Hoist + early-return on Err.
                    _sb.Append(pad);
                    _sb.AppendLine($"if !{stepName}.IsOk {{");
                    _sb.Append(pad);
                    _sb.Append("\treturn overt.Err[");
                    EmitCurrentReturnTypeArgs();
                    _sb.AppendLine($"]({stepName}.Err)");
                    _sb.Append(pad);
                    _sb.AppendLine("}");
                    // Subsequent steps see the unwrapped Value.
                    var unwrappedName = $"__pp_{_qCounter++}";
                    _sb.Append(pad);
                    _sb.Append(unwrappedName);
                    _sb.Append(" := ");
                    _sb.Append(stepName);
                    _sb.AppendLine(".Value");
                    currentName = unwrappedName;
                }
                else
                {
                    currentName = stepName;
                }
            }
            // Trailing `|>?` (rare but possible): the last step was a
            // hoist-and-unwrap; whatever currentName names is the
            // final value. Emit as the function return.
            _sb.Append(pad);
            _sb.Append("return ");
            _sb.AppendLine(currentName);
        }

        /// <summary>
        /// Emit a single pipe step as a Go call. The seed (the LHS
        /// of the pipe) is referenced by <paramref name="seedName"/>;
        /// the RHS is either a bare name (call as <c>f(seed)</c>) or
        /// a CallExpr whose argument list shifts by one position
        /// (call as <c>f(seed, args...)</c>).
        ///
        /// Ok / Err on the RHS get the constructor special-case: the
        /// emitted call needs explicit type parameters from the
        /// enclosing fn's return type, same as a bare `Ok(value)`
        /// site. We thread that through by emitting `overt.Ok[T, E](
        /// seed)` directly rather than via the EmitExpression path
        /// (which would emit a bare `Ok` identifier with no
        /// resolution).
        /// </summary>
        private void EmitPipeStepCall(Expression rhs, string seedName)
        {
            // Bare-name `Ok` / `Err` on RHS need explicit type args
            // since Go can't infer them from a single argument.
            if (rhs is IdentifierExpr { Name: "Ok" })
            {
                _sb.Append("overt.Ok[");
                EmitResultTypeArgsOrFallback();
                _sb.Append("](");
                _sb.Append(seedName);
                _sb.Append(')');
                return;
            }
            if (rhs is IdentifierExpr { Name: "Err" })
            {
                _sb.Append("overt.Err[");
                EmitResultTypeArgsOrFallback();
                _sb.Append("](");
                _sb.Append(seedName);
                _sb.Append(')');
                return;
            }
            if (rhs is CallExpr call)
            {
                EmitExpression(call.Callee);
                _sb.Append('(');
                _sb.Append(seedName);
                foreach (var arg in call.Arguments)
                {
                    _sb.Append(", ");
                    EmitExpression(arg.Value);
                }
                _sb.Append(')');
                return;
            }
            // Bare name on RHS — same shape as `seed |> f`.
            EmitExpression(rhs);
            _sb.Append('(');
            _sb.Append(seedName);
            _sb.Append(')');
        }

        /// <summary>
        /// Lower `x |> f` or `x |> f(args)` to `f(x)` / `f(x, args)`.
        /// The LHS becomes the FIRST argument of the call on the
        /// right; subsequent arguments shift one position. Mirrors
        /// the C# back end's EmitPipe rewrite. The Overt-side type
        /// checker has already verified the receiver-as-first-arg
        /// shape works.
        ///
        /// `|> Ok` (constructor-as-RHS) flows through EmitCall's
        /// existing `Ok` / `Err` special-case; the type-args come
        /// from the enclosing fn's return type, same as a bare
        /// `Ok(value)` site.
        /// </summary>
        private void EmitPipeCompose(BinaryExpr be)
        {
            // Ok / Err special-case: same as in EmitPipeStepCall.
            if (be.Right is IdentifierExpr { Name: "Ok" })
            {
                _sb.Append("overt.Ok[");
                EmitResultTypeArgsOrFallback();
                _sb.Append("](");
                EmitExpression(be.Left);
                _sb.Append(')');
                return;
            }
            if (be.Right is IdentifierExpr { Name: "Err" })
            {
                _sb.Append("overt.Err[");
                EmitResultTypeArgsOrFallback();
                _sb.Append("](");
                EmitExpression(be.Left);
                _sb.Append(')');
                return;
            }
            if (be.Right is CallExpr call)
            {
                // Splice LHS as first arg, original args after.
                EmitExpression(call.Callee);
                _sb.Append('(');
                EmitExpression(be.Left);
                foreach (var arg in call.Arguments)
                {
                    _sb.Append(", ");
                    EmitExpression(arg.Value);
                }
                _sb.Append(')');
                return;
            }
            // Bare-name RHS: emit as `f(x)`.
            EmitExpression(be.Right);
            _sb.Append('(');
            EmitExpression(be.Left);
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
            "par_map" => "overt.ParMap",
            "try_map" => "overt.TryMap",
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
