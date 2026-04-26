# build-vsix.ps1
#
# PowerShell twin of build-vsix.sh. Same model: installs vsce on
# demand, runs `vsce package` against vscode-extension/, emits a
# single-line `vsix=<full-path>` marker on stdout that callers can
# capture for downstream steps.
#
# Prerequisites:
#   - Node.js 18+ on PATH.
#
# Usage:
#   pwsh -File scripts/build-vsix.ps1

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $scriptDir "..")
$extDir = Join-Path $root "vscode-extension"

if (-not (Test-Path (Join-Path $extDir "package.json"))) {
    Write-Error "build-vsix: vscode-extension/package.json not found at $extDir"
    exit 1
}

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error "build-vsix: node is required (Node 18+); install from https://nodejs.org/"
    exit 1
}

if (-not (Get-Command vsce -ErrorAction SilentlyContinue)) {
    Write-Output "==> Installing @vscode/vsce"
    npm install -g "@vscode/vsce"
}

Push-Location $extDir
try {
    $version = (node -e "console.log(require('./package.json').version)").Trim()
    $artifact = "overt-language-$version.vsix"

    Write-Output "==> Packaging Overt extension v$version"
    vsce package --out $artifact

    Write-Output "==> Produced $extDir\$artifact"
    Write-Output "vsix=$extDir\$artifact"
}
finally {
    Pop-Location
}
