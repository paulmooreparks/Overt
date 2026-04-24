# Compiler Explorer (godbolt) integration

Plan for listing Overt on [godbolt.org](https://godbolt.org) (the Compiler Explorer service). Scaffolding lives under [`/tooling/godbolt/`](../../tooling/godbolt/).

## Why

Godbolt is the first place many developers (and agents) poke at an unfamiliar language. A working CE entry turns "what does Overt look like?" into a shareable URL: no install, no toolchain setup. It also forces the compiler to support a clean stdin/stdout mode with stable, flag-gated output, which is independently useful for CI and for agent-driven dev loops.

## What Overt would display

Overt transpiles to C# (primary, via Roslyn) and Go (secondary), rather than lowering to IR/asm itself. That gives three natural "output" modes, and CE supports all of them through the same mechanism:

1. **Transpiled C# source.** Fastest to ship, highest value for early reviewers. User pastes `.ov`, sees Roslyn-bound C# on the right.
2. **Transpiled Go source.** Same, alternative target. Skip at launch; add once the Go backend stabilises.
3. **True asm via compiler chaining.** CE has first-class support for "language X compiles to language Y, which CE already knows how to compile." Register Overt as a language whose compiler emits C#, then let users chain to `csc`/`dotnet` (or `gccgo` for Go) to see final assembly. Haxe, V, and Carbon ship this way.

Launch target: (1) + (3). Defer (2).

## Prerequisites (gate items, in order)

These are what must exist before a CE submission makes sense. None are blocked on CE; they are blocked on the transpiler.

- [ ] **A compiler binary.** Must accept source from a file path or stdin and emit transpiled source on stdout, diagnostics on stderr.
- [ ] **Stable, flag-gated emit modes.** At minimum:
  - `--emit=csharp`: transpiled C# source
  - `--emit=go`: transpiled Go source
  - `--emit=tokens` / `--emit=ast`: useful for debugging, optional for CE
  - `--no-color`: CE captures raw stdout
- [ ] **Deterministic formatting.** Same input → byte-identical output. No timestamps, absolute paths, or run-dependent IDs in the emission.
- [ ] **Linux x64 binary.** CE builds compilers in Linux containers. `dotnet publish -r linux-x64 --self-contained -p:PublishSingleFile=true` is the minimum; AOT (`-p:PublishAot=true`) is nicer if the compiler stays AOT-friendly.
- [x] **OSI-compatible license.** Apache-2.0 (done).
- [ ] **Versioned release tarballs at stable URLs.** GitHub Releases is fine. CE's install recipes fetch by version; you need at least one tagged release with the Linux artifact attached before submission.

## The actual submission

Two PRs against the Compiler Explorer org:

### [compiler-explorer/infra](https://github.com/compiler-explorer/infra)

Add an install recipe under `bin/yaml/overt.yaml`. It declares how CE's build farm fetches each released version. Placeholder lives at [`tooling/godbolt/infra/overt.yaml`](../../tooling/godbolt/infra/overt.yaml).

### [compiler-explorer/compiler-explorer](https://github.com/compiler-explorer/compiler-explorer)

Three required edits, one optional:

1. **`lib/languages.ts`**: register the `overt` language (id, extension `.ov`, monaco mode, example file). Snippet at [`tooling/godbolt/config/languages-entry.ts.snippet`](../../tooling/godbolt/config/languages-entry.ts.snippet).
2. **`etc/config/overt.defaults.properties`**: declare the compiler list, default flags, `supports-binary=false`, `supports-asm=false` until chaining to `csc` is wired. Template at [`tooling/godbolt/config/overt.defaults.properties`](../../tooling/godbolt/config/overt.defaults.properties).
3. **`examples/overt/default.ov`**: a small hello-world shown by default. See [`tooling/godbolt/examples/default.ov`](../../tooling/godbolt/examples/default.ov).
4. *(Optional but recommended)* **`static/modes/overt-mode.ts`**: a Monaco tokenizer so the editor pane gets syntax highlighting. Without it, Overt shows as plain text, which is a poor first impression. Template at [`tooling/godbolt/monaco/overt-mode.ts`](../../tooling/godbolt/monaco/overt-mode.ts); keywords are cribbed from the TextMate grammar in [`vscode-extension/syntaxes/overt.tmLanguage.json`](../../vscode-extension/syntaxes/overt.tmLanguage.json) and should stay in sync with it.

CE maintainers are responsive; clean PRs typically land within one to two weeks.

## Effort estimate

- Parser + C# emit sufficient for `hello.ov` and one non-trivial example: **the real work; blocks everything**.
- Linux release artifact + `--emit=csharp` flag: **~1 day**.
- Both CE PRs including Monaco mode: **~1–2 days** including review churn.

Total CE-specific glue: ~2–3 days once the transpiler MVP exists.

## Parser MVP acceptance criteria (worth flagging)

So the transpiler lands CE-ready rather than needing retrofit:

- `overt --emit=csharp <file>` prints transpiled C# to stdout, exits non-zero on any diagnostic error.
- Diagnostics go to stderr in a stable, line-prefixed format (`path:line:col: severity: message`). CE parses this to show inline errors.
- `--version` prints a single line; CE uses this to label the compiler in the UI.
- No color codes, no interactive prompts, no network calls during a compile.

Retrofitting output flags after the fact is annoying; baking them into the MVP is cheap.
