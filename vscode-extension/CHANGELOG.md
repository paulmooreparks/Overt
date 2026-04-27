# Change Log

All notable changes to the Overt VS Code extension are documented
here. The format follows [Keep a Changelog](https://keepachangelog.com/);
versions follow [SemVer](https://semver.org/).

## [Unreleased]

## [0.1.3] - 2026-04-27

### Added

- Highlight new stdlib type `LogLevel` (the four-arm Debug / Info /
  Warn / Error sum that ships with the reshaped `TraceEvent`).
- Highlight the new `Log` namespace via the namespace-call pattern, so
  `Log.info(...)`, `Log.warn(...)`, etc. render in the same style as
  the other stdlib namespaces.

### Removed

- Drop `len` from the prelude-function highlight list. `len` was a
  duplicate of `size` and is removed from the runtime; programs that
  used it now fail at parse, so highlighting it would be misleading.

## [0.1.2] - 2026-04-27

### Added

- Highlight new stdlib types: `ProcessOutput`, `MapEntry`,
  `RefinementError`, `RefinementViolation`, `Bytes` in the
  type list.
- Highlight new namespace calls: `File.read_to_string`, `File.exists`,
  `File.write_all_text`, `Path.join`, `Path.parent`, `Path.file_name`,
  `Path.extension`, `Process.run`, `Directory.*`, `Bytes.*` —
  via the namespace-call pattern.
- Highlight new prelude functions: `print`, `eprint`, `read_line`,
  `read_to_end`.

### Changed

- Namespace-call pattern now supports the Form-3 generic-call syntax
  `Type<Args>.method()` introduced in the Overt repo's recent
  redesign. The optional `<...>` between namespace and dot is part
  of the same call token; before this version, `List<Int>.empty()`
  highlighted `List` as a type but the `.empty(` portion fell back
  to the default style.

## [0.1.1] - 2026-04-26

### Added

- Marketplace-ready README and CHANGELOG bundled into the .vsix.
- `scripts/build-vsix.sh` (and PowerShell twin) that wraps
  `vsce package` for consistent local + CI builds.
- GitHub Actions workflow (`.github/workflows/vscode-extension.yml`)
  that builds the .vsix on every push touching
  `vscode-extension/` and uploads it as a downloadable artifact,
  so anyone can grab the current build from the Actions tab
  without local Node.js setup.

### Changed

- Version bump policy documented: every Marketplace publish needs
  the patch incremented in `vscode-extension/package.json`. The
  CI workflow won't refuse to build at a duplicate version (the
  artifact is harmless), but `vsce publish` rejects duplicates,
  so the bump is the gate.

## [0.1.0] - 2026-04-26

### Added

- Initial public release alongside the first non-toy Overt project
  ([SemVer Kit](https://github.com/paulmooreparks/SemVerKit)).
- TextMate grammar covering the language surface as of Overt commit
  series leading up to the bare-`for` form, the `chars()` /
  `code_points()` / `Int.range()` iterator helpers, and the `all` /
  `any` predicate combinators.
- Syntax highlighting for comments, string literals (with escape
  sequences and `${...}` / `$name` interpolation), numeric literals
  (decimal, hex `0x`, binary `0b`, float with exponent), keywords
  (control flow, declarations, `await`, `as`), effect rows (`!{io}`,
  effect-row variables), annotations (`@doc`, `@csharp`, `@derive`),
  postfix `.await`, the `_` discard target, all operators including
  `|>` / `|>?` / `?` / `->` / `=>`, built-in types (`Result`,
  `Option`, `List`, `Map`, `Set`, `Task`, `IoError`, etc.), prelude
  functions (`println`, `map`, `filter`, `fold`, `all`, `any`,
  `size`, `length`, etc.), and module-qualified stdlib calls
  (`String.chars`, `Int.range`, `List.at`, etc.).
- Language configuration: auto-closing and surrounding pairs for
  `{}`, `[]`, `()`, `""`; comment toggle for `//` and `/* */`;
  indent and outdent rules tied to `{` / `}`; a `wordPattern` for
  clean double-click identifier selection.
