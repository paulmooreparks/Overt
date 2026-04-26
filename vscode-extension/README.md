# Overt for VS Code

Syntax highlighting and editor configuration for the [Overt programming
language](https://github.com/paulmooreparks/Overt). Activates on any
file with the `.ov` extension.

Overt is an agent-first language designed to be written, read, and
maintained primarily by LLM agents. The extension makes Overt source
readable in the editor for the human-in-the-loop review and audit
roles that the language explicitly accommodates.

## Features

Syntax highlighting via a TextMate grammar. The grammar is updated
alongside the language; if a recent language addition isn't colored,
the extension is behind and a refresh is in order.

- **Comments**: `//` line comments and `/* ... */` block comments.
- **String literals** with escape sequences and string interpolation:
  `"hello, ${name}"` and the bare `$name` form both highlight inside
  the string body.
- **Numeric literals**: decimal, hexadecimal (`0x...`), binary
  (`0b...`), and floating-point with optional exponent.
- **Keywords**: control flow (`if`, `else`, `match`, `for`, `in`,
  `while`, `loop`, `break`, `continue`, `return`, `parallel`, `race`,
  `trace`, `with`), declarations (`fn`, `let`, `mut`, `record`,
  `enum`, `type`, `module`, `use`, `as`, `pub`, `extern`, `unsafe`,
  `where`, `await`), and constants (`true`, `false`, `Ok`, `Err`,
  `Some`, `None`).
- **Effect rows**: `!{io}`, `!{io, async}`, `!{E}`. Concrete effect
  names (`io`, `async`, `inference`, `fails`) and effect-row
  variables (uppercase identifiers) get distinct scopes.
- **Annotations**: `@doc("...")`, `@csharp("...")`, `@derive(...)`,
  and any other `@name`-prefixed marker.
- **Postfix `.await`**: highlighted as a single syntactic unit, the
  way Rust and JavaScript editors color it.
- **Operators**: pipe (`|>`), pipe-propagate (`|>?`), propagate
  (`?`), arrows (`->`, `=>`), comparison, logical, arithmetic.
- **Discard target**: bare `_` in `let _ = expr` and pattern
  positions.
- **Built-in types**: `Int`, `Int64`, `Float`, `Bool`, `String`,
  `Unit`, `Option`, `Result`, `List`, `Map`, `Set`, `Task`,
  `IoError`, and the standard error and FFI-boundary types.
- **Built-in functions**: prelude calls (`println`, `eprintln`,
  `map`, `filter`, `fold`, `all`, `any`, `try_map`, `par_map`,
  `size`, `length`, `len`, `args`).
- **Module-qualified stdlib calls**: `String.chars(...)`,
  `Int.range(...)`, `List.at(...)`, and similar shapes get a
  function-call scope distinct from plain field access.

Editor configuration via [`language-configuration.json`](./language-configuration.json):

- Auto-closing pairs for `{}`, `[]`, `()`, and `"..."`.
- Surrounding pairs for the same set, so selecting text and typing
  `(` wraps the selection.
- Comment toggling for `//` and `/* */` via the standard VS Code
  shortcut.
- Indent / outdent rules tied to `{` and `}`.
- A `wordPattern` so double-click selects identifiers cleanly without
  breaking on dots or operators.

## Installation

From the Marketplace: search for "Overt" under publisher
`paulmooreparks`, or use the command palette: `Extensions: Install
Extensions` then search.

From a local `.vsix`:

```
vsce package
code --install-extension overt-language-0.1.0.vsix
```

The repository's `vscode-extension/` directory contains the source
for the extension; `vsce package` produces the installable artifact.

## Status

The extension is intentionally minimal. A full language server
(diagnostics, hover, go-to-definition, rename) is scoped in
[`docs/tooling/lsp.md`](https://github.com/paulmooreparks/Overt/blob/main/docs/tooling/lsp.md)
and will land as a separate `Overt.LanguageServer` companion when the
language stabilizes enough to justify the investment.

## Links

- [Overt repository](https://github.com/paulmooreparks/Overt)
- [Design document](https://github.com/paulmooreparks/Overt/blob/main/DESIGN.md) (the source of truth for language semantics)
- [Agent-authoring reference](https://github.com/paulmooreparks/Overt/blob/main/AGENTS.md) (the operational guide for writing `.ov`)
- [Examples](https://github.com/paulmooreparks/Overt/tree/main/examples) (living test cases that exercise every feature)

## License

Apache 2.0. See [LICENSE](./LICENSE).
