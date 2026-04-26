# Language Server scoping

This document scopes a future Overt language server (LSP). It is *not*
an implementation; it is the architecture, phasing, and feature list
the implementation should track. Reading order: §1 establishes what's
already in `Overt.Compiler` that an LSP can stand on, §2 maps each LSP
feature to the compiler output it consumes, §3 phases the work into
shippable increments, §4 calls out the load-bearing decisions that
need to be made before the first commit.

## 1. What the compiler already exposes

The compiler's pipeline already produces, on every clean run, every
piece of state the LSP needs. None of the work below requires adding
new analyses; it requires plumbing existing outputs to a JSON-RPC
transport.

- **`ResolutionResult.Resolutions`** ([NameResolver.cs](../../src/Overt.Compiler/Semantics/NameResolver.cs)):
  `ImmutableDictionary<SourceSpan, Symbol>`. Maps every identifier
  reference in source to the symbol it resolves to. This is the entire
  data feed for **textDocument/definition** (jump from a use site to
  the declaration's span).

- **`ResolutionResult.AliasedModules` and `ImportedSymbols`**: the
  per-alias export tables. Powers cross-module navigation: clicking a
  qualified call like `env.processor_count()` jumps into the synthetic
  bulk-imported module.

- **`TypeCheckResult.ExpressionTypes`**: `ImmutableDictionary<SourceSpan, TypeRef>`.
  Every expression has an annotated type. This is the data feed for
  **textDocument/hover** (show the type under the cursor).

- **`TypeCheckResult.SymbolTypes`**: `ImmutableDictionary<Symbol, TypeRef>`.
  Symbol-keyed types, used to render signatures for hovers on
  declarations and for completion item details.

- **`TypeCheckResult.MethodCallResolutions`**: per-FieldAccess
  resolution for `receiver.method(args)` calls. Powers
  **definition-of-method-call** (an otherwise-ambiguous lookup
  through alias-extern-instance fns).

- **`Diagnostic`** records ([Diagnostics/Diagnostic.cs](../../src/Overt.Compiler/Diagnostics)):
  every error/warning carries a stable code (`OV0xxx`), severity,
  message, span, optional `help:` follow-up, and optional `note:`
  pointer. This is the data feed for **textDocument/publishDiagnostics**.
  The `help:` text is exactly what should appear as a code-action
  quick-fix description; the `note:` text becomes the
  "see AGENTS.md §N" link.

- **`Stdlib.Symbols` / `Stdlib.Types`**: prelude entries with
  declaration-spans set to a sentinel (line 0, column 0). LSP must
  detect the sentinel and substitute a "(prelude)" virtual location
  rather than offering a meaningless go-to-definition.

- **Module graph** (`Modules/`): the resolver-driven cross-file `use`
  resolution. The LSP needs to consume this for definitions that span
  files (e.g. a `use ParksComputing.SemVer.{ parse }` in the CLI that
  jumps into the library's `.ov`).

The pipeline is already split (Tier 1 Compiler / Tier 2 Backend), so
the LSP lives entirely in Tier 1: it never imports `Overt.Backend.*`.

## 2. LSP feature → compiler output mapping

| LSP method | Source of truth | Notes |
| --- | --- | --- |
| `textDocument/publishDiagnostics` | `Diagnostic[]` from each pipeline stage | Lex / parse / resolve / typecheck. Push on every recompile. |
| `textDocument/hover` | `ExpressionTypes[span]` + `SymbolTypes[symbol]` | Hover renders the `TypeRef` via `TypeRef.ToString()`-equivalent prose. Pair with the declaration's `@doc("...")` attribute when present. |
| `textDocument/definition` | `Resolutions[span]` → `Symbol.DeclarationSpan` | Sentinel spans (Stdlib) emit `null` rather than a bogus location. |
| `textDocument/references` | inverted `Resolutions` map | Build at compile-end: for each Symbol, list all spans whose resolution points at it. |
| `textDocument/documentSymbol` | walk `ModuleDecl.Declarations` | Enum, record, and fn declarations each emit one symbol; nested record fields and enum variants are children. |
| `textDocument/rename` | `Resolutions` + parser span info | Compute all reference spans, plus the declaration span; client applies the edit. Refuse if any reference is in a synthetic / generated location. |
| `textDocument/completion` | resolver scope at cursor + `Stdlib.Symbols` | First cut: only top-level identifiers and prelude. Member completion (`s.<>` showing instance externs) requires the type-checker's current type at the cursor. |
| `textDocument/codeAction` | diagnostics with `help:` text | The `help:` string is the action title; the fix is currently human-readable, not machine-applicable. Phase 4 work, not phase 1. |
| `textDocument/formatting` | `Formatter` in `Overt.Compiler` | Already exists; LSP just routes the buffer through it. |
| `textDocument/semanticTokens` | walk the module + ExpressionTypes | Optional. The TextMate grammar already gives 80% of the colorization; semantic tokens improve only ambiguous cases (uppercase identifier that's a type vs. a variant constructor). Phase 4. |

## 3. Phasing

Each phase is a shippable increment: install the extension, edit a
`.ov` file, see the new capability work. No phase should ever leave
the extension worse than it was at the start.

### Phase 1 — Diagnostics + formatter (the table-stakes phase)

The minimum that justifies the install. Transports:

- LSP server project: `Overt.LanguageServer` (new, Tier 1, references
  `Overt.Compiler` only). Hosts JSON-RPC over stdio using
  `OmniSharp.Extensions.LanguageServer` (recommended; mature, wide
  coverage, MIT-licensed).
- Capabilities advertised: `textDocumentSync.full`,
  `publishDiagnostics`, `documentFormatting`.
- Document store: `ConcurrentDictionary<DocumentUri, string>` updated
  on `didOpen` / `didChange`; trigger compile on a 250ms debounce
  after the last edit.
- Compile pipeline: lex → parse → resolve → typecheck (skip emit).
  Surface every diagnostic via `publishDiagnostics`. Map
  `SourceSpan` → LSP `Range` 1:1; both are line/column.
- Formatter: route the buffer through `Formatter.Format(source)`
  on `documentFormatting`; return a single full-document edit.

VS Code client: ~30 lines of TypeScript wrapping
`vscode-languageclient` and pointing at the dotnet-built server
binary. Lives in the existing `vscode-extension/` directory; the
extension activates on `onLanguage:overt` and spawns the server
on demand.

Exit criteria: open a `.ov` file with a syntax error, see the
squiggly + the OV-coded message + the `help:` follow-up in the
problems pane. Run "Format Document" and see canonical output.

### Phase 2 — Navigation (definitions, hover, document symbols)

Adds the read-the-code experience. No new compiler work.

- `textDocument/hover`: look up `ExpressionTypes[cursor-span]`,
  render the `TypeRef`. For declarations (fns, records, enums),
  render the full signature. Markdown is fine; VS Code renders it.
- `textDocument/definition`: look up `Resolutions[cursor-span]`,
  return the symbol's declaration span (with a stdlib-sentinel
  guard).
- `textDocument/documentSymbol`: walk `ModuleDecl.Declarations`,
  emit a `DocumentSymbol` tree. Drives the outline and breadcrumbs.

Exit criteria: hover on `parse(...)` shows the signature; F12
jumps to the declaration; the outline pane shows the module's
fns, records, and enums.

### Phase 3 — Refactoring (references, rename, completion)

Adds the change-the-code experience. Requires building an inverted
references map, but no new analysis kinds.

- `textDocument/references`: invert `Resolutions` once per compile,
  return all reference spans for the symbol under the cursor.
- `textDocument/rename`: compute the rename edit set
  (declaration + all references), reject if any target span is
  synthetic. Return a `WorkspaceEdit`.
- `textDocument/completion`: at cursor, ask the resolver for the
  active scope (lexical + module-imports + prelude); return one
  `CompletionItem` per visible symbol with the type as `detail`.
  Member completion (`s.<>`) is a stretch goal — needs a "type at
  this offset" query that the type-checker can answer in O(log n)
  with a spatial index.

Exit criteria: F2 renames a binding across all uses; Find All
References works; Ctrl+Space inside a function body offers in-scope
identifiers and prelude.

### Phase 4 — Polish (semantic tokens, code actions, snippets)

Niceties that pay off after the workhorse features land.

- `textDocument/semanticTokens`: emit semantic tokens for the few
  cases the TextMate grammar genuinely cannot disambiguate (uppercase
  identifier as type vs. variant constructor; effect-row identifiers
  as effects vs. type variables).
- `textDocument/codeAction`: at first, only the trivial fixes
  (whitespace, missing semicolon if Overt grows them). Real
  machine-applicable fixes (auto-add missing arm to a non-exhaustive
  match, auto-import a name) want a separate design pass.
- Snippet pack: bundled with the extension, covers `fn`, `match`,
  `extern "csharp" use`, `record`, `enum`, common test scaffolds.

## 4. Decisions to make before the first commit

These are load-bearing; getting them wrong costs a rewrite.

1. **JSON-RPC framework.** Recommended:
   `OmniSharp.Extensions.LanguageServer` (NuGet:
   `OmniSharp.Extensions.LanguageServer`). It's the Roslyn / OmniSharp
   ecosystem's standard, handles transport / lifecycle / capability
   negotiation, and stays out of the way for the actual feature
   handlers. Alternative: hand-rolled, which is ~500 lines of
   stream-pumping that already exists better elsewhere.

2. **Compile cadence.** Per-keystroke is too aggressive; per-save is
   too sluggish. 250ms debounce after the last edit is the
   conventional answer; revisit if cold parses turn out to be slow.
   The whole pipeline through type-checking takes single-digit ms on
   the corpus today, so debounce dominates wall time.

3. **Multi-file scope.** Phase 1 can be single-file
   (file-as-its-own-module); phase 2's go-to-definition demands at
   least intra-module-graph awareness. Reuse the existing
   `Modules/` resolution rather than building a separate document
   graph.

4. **Server lifecycle.** Spawned per workspace? Per file? VS Code's
   convention is per workspace, with the server holding the document
   store and doing incremental updates. Don't reinvent.

5. **Distribution.** The .NET-based server adds a runtime dependency
   to the extension. Two options: bundle a self-contained
   single-file binary per OS (~70 MB per platform; ugly but
   self-sufficient), or require the user to have the .NET 9 runtime
   installed and ship just the framework-dependent binary (~5 MB;
   prerequisite-heavy but smaller). Recommended: framework-dependent
   for the first release; pivot to self-contained only if first-run
   friction shows up in feedback.

6. **Naming.** `Overt.LanguageServer` for the project. The VS Code
   extension's published name stays `overt-language` (see
   `vscode-extension/package.json`); add a `"main"` field pointing
   at the TypeScript client when it's added.

## 5. What the LSP explicitly does not do

A guardrail list to keep scope from sprawling.

- **Code emission.** Only Tier 1 stages. Backend invocation belongs
  to `overt run` and the MSBuild task, not the editor.
- **Workspace-wide refactoring beyond rename.** No "extract function,"
  "inline fn," or similar. These can ship later as code actions, but
  none belong in the v1 scope.
- **Debugger.** Out of scope. Debugging transpiled Overt happens
  through the C# debugger via the `#line` directives the back end
  already emits (DESIGN.md §18).
- **Notebook support.** Out of scope.
- **Multiple-cursor / linked edits beyond rename.** VS Code's built-in
  multi-cursor is enough for now.

## 6. Estimated effort

Rough single-developer estimates, working in focused sessions:

- Phase 1: 1 week. Most of the time is in JSON-RPC wiring + the VS
  Code client + the binary distribution loop, not compiler work.
- Phase 2: 3–5 days on top of Phase 1. The compiler outputs are
  already shaped right; this is mostly mapping and rendering.
- Phase 3: 1 week. Inverted-references map, completion scope query,
  rename edit generation.
- Phase 4: 1–2 weeks, depending on how aggressive the semantic-tokens
  + code-action work gets.

Total to a polished extension: roughly 4–5 weeks of focused effort.
The first usable increment (Phase 1) is reachable in a single week.
