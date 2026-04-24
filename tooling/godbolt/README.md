# Compiler Explorer scaffolding

Pre-staged files for submitting Overt to [godbolt.org](https://godbolt.org). Nothing here is live yet; these are templates that get filled in and copied into the two Compiler Explorer repos once the transpiler produces stable output.

**See [`docs/tooling/godbolt.md`](../../docs/tooling/godbolt.md) for the plan, gate items, and submission process.**

## Layout

```
tooling/godbolt/
├── README.md                        This file
├── infra/
│   └── overt.yaml                   Install recipe → compiler-explorer/infra:bin/yaml/overt.yaml
├── config/
│   ├── overt.defaults.properties    Compiler config → compiler-explorer:etc/config/overt.defaults.properties
│   └── languages-entry.ts.snippet   Language registration → merge into compiler-explorer:lib/languages.ts
├── examples/
│   └── default.ov                   Default example → compiler-explorer:examples/overt/default.ov
└── monaco/
    └── overt-mode.ts                Monaco syntax mode → compiler-explorer:static/modes/overt-mode.ts
```

## Status

Blocked on transpiler. Specifically:

- `overt` CLI with `--emit=csharp` producing stable stdout
- Linux x64 release artifact published to GitHub Releases
- At least one tagged version (`v0.1.0` or similar)

Once those exist, every `TODO(release)` marker in these files resolves to a concrete value, and the two CE PRs can go out.

## Keeping things in sync

The Monaco mode keyword list must track [`vscode-extension/syntaxes/overt.tmLanguage.json`](../../vscode-extension/syntaxes/overt.tmLanguage.json). When keywords change there, update [`monaco/overt-mode.ts`](monaco/overt-mode.ts) in the same commit.
