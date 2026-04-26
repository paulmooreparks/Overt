#!/usr/bin/env bash
# build-vsix.sh
#
# Package the Overt VS Code extension into a .vsix artifact. Runs from
# any working directory; produces `vscode-extension/overt-language-<v>.vsix`
# where <v> is the version field in `vscode-extension/package.json`.
#
# Why a script and not just `vsce package`: this wrapper installs vsce
# on demand (so a fresh CI runner needs only Node + this script) and
# emits a stable single line of output naming the produced file, which
# the workflow captures as a step output for the upload step.
#
# Prerequisites:
#   - Node.js 18+ on PATH (npm comes with it).
#
# Usage:
#   bash scripts/build-vsix.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
EXT_DIR="$ROOT/vscode-extension"

if [ ! -f "$EXT_DIR/package.json" ]; then
    echo "build-vsix: vscode-extension/package.json not found at $EXT_DIR" >&2
    exit 1
fi

if ! command -v node >/dev/null 2>&1; then
    echo "build-vsix: node is required (Node 18+); install from https://nodejs.org/" >&2
    exit 1
fi

if ! command -v vsce >/dev/null 2>&1; then
    echo "==> Installing @vscode/vsce"
    npm install -g @vscode/vsce
fi

cd "$EXT_DIR"

# Read the version field with node so we don't depend on jq being present.
VERSION="$(node -e "console.log(require('./package.json').version)")"
ARTIFACT="overt-language-${VERSION}.vsix"

echo "==> Packaging Overt extension v${VERSION}"
vsce package --out "$ARTIFACT"

echo "==> Produced ${EXT_DIR}/${ARTIFACT}"
echo "vsix=${EXT_DIR}/${ARTIFACT}"
