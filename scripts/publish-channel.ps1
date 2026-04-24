# publish-channel.ps1
#
# Pack Overt and Overt.Build as prerelease NuGet packages on the
# current channel. For the `local` channel this also reinstalls the
# `overt` global tool from the freshly packed nupkg so your PATH has
# the new build immediately. No network traffic. No GitHub push.
#
# Model borrowed from the Tela repo's publish-channel.ps1: VERSION
# file at repo root holds MAJOR.MINOR; a per-channel build-counter
# file lives next to this script; each run increments the counter
# and produces a tag like `v0.1.0-local.7` and packages versioned
# `0.1.0-local.7`. Tela ships binaries to a self-hosted hub; we ship
# .nupkgs to a local folder (and into your global tool cache).
#
# Configuration
# -------------
# Reads a .env-style file next to itself (publish.env) or matching
# process environment variables. Env wins.
#
# Optional keys:
#   OVERT_PUBLISH_CHANNEL    Target channel name. Default: reads
#                            scripts/channel.txt (created with "local"
#                            if absent). Must match [A-Za-z0-9-].
#   OVERT_PUBLISH_REPO_ROOT  Path to the Overt repo. Default: script's
#                            parent directory.
#   OVERT_PUBLISH_DIST_DIR   Where `dotnet pack` drops output.
#                            Default: <repo>/dist
#   OVERT_PUBLISH_SKIP_INSTALL
#                            Set to "1" to skip the `dotnet tool install`
#                            reinstall step. Default: install when the
#                            channel is "local", skip otherwise.
#
# CI channels (dev, beta, stable) use the GitHub Actions workflows in
# .github/workflows/ rather than this script. The script stays channel-
# agnostic, though, so it can run any channel in a pinch.
#
# Usage
# -----
#   pwsh scripts/publish-channel.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Import-PublishEnv {
    $envPath = Join-Path $PSScriptRoot 'publish.env'
    $config = @{}
    if (-not (Test-Path $envPath)) { return $config }
    foreach ($line in (Get-Content $envPath)) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        $eq = $trimmed.IndexOf('=')
        if ($eq -lt 1) { continue }
        $key = $trimmed.Substring(0, $eq).Trim()
        $val = $trimmed.Substring($eq + 1).Trim()
        if (($val.StartsWith('"') -and $val.EndsWith('"')) -or
            ($val.StartsWith("'") -and $val.EndsWith("'"))) {
            $val = $val.Substring(1, $val.Length - 2)
        }
        $config[$key] = $val
    }
    return $config
}

function Get-PublishConfig($cfg, $key, $default) {
    $fromEnv = [System.Environment]::GetEnvironmentVariable($key, 'Process')
    if ($fromEnv) { return $fromEnv }
    if ($cfg.ContainsKey($key) -and $cfg[$key]) { return $cfg[$key] }
    return $default
}

$PUBLISH_ENV = Import-PublishEnv

$ROOT = Get-PublishConfig $PUBLISH_ENV 'OVERT_PUBLISH_REPO_ROOT' (Resolve-Path "$PSScriptRoot\..").Path
$DIST = Get-PublishConfig $PUBLISH_ENV 'OVERT_PUBLISH_DIST_DIR'  "$ROOT\dist"

# Channel selection
$CHANNEL_FILE = "$PSScriptRoot\channel.txt"
$CHANNEL = Get-PublishConfig $PUBLISH_ENV 'OVERT_PUBLISH_CHANNEL' $null
if (-not $CHANNEL) {
    if (-not (Test-Path $CHANNEL_FILE)) { Set-Content $CHANNEL_FILE "local" }
    $CHANNEL = (Get-Content $CHANNEL_FILE -Raw).Trim()
}
if ($CHANNEL -notmatch '^[A-Za-z0-9-]+$') {
    throw "Invalid channel name '$CHANNEL'. Use [A-Za-z0-9-] only."
}

# Per-channel build counter and version
$BASE = (Get-Content "$ROOT\VERSION" -Raw).Trim()
$COUNTER_FILE = "$PSScriptRoot\$CHANNEL-build-counter"
$N = 1
if (Test-Path $COUNTER_FILE) {
    $current = [int]((Get-Content $COUNTER_FILE -Raw).Trim())
    $N = $current + 1
}
Set-Content $COUNTER_FILE $N

$SUFFIX = "$CHANNEL.$N"
$VERSION = "$BASE.0-$SUFFIX"
$TAG = "v$VERSION"
# FileVersion is four numeric fields; we reuse the counter as the build
# field so Explorer's File Version column shows the increment. The
# AssemblyVersion stays at MAJOR.MINOR.0.0 (set in Directory.Build.props)
# so binding-redirect consumers don't churn across prereleases.
$FILE_VERSION = "$BASE.0.$N"

# Install decision: default on for local, off elsewhere
$SKIP_INSTALL_VAL = Get-PublishConfig $PUBLISH_ENV 'OVERT_PUBLISH_SKIP_INSTALL' $null
if ($SKIP_INSTALL_VAL) {
    $DO_INSTALL = $SKIP_INSTALL_VAL -notin @('1','true','yes')
} else {
    $DO_INSTALL = ($CHANNEL -eq 'local')
}

New-Item -ItemType Directory -Force -Path $DIST | Out-Null

Write-Host ""
Write-Host "==> Packing Overt $VERSION (channel: $CHANNEL, tag: $TAG)" -ForegroundColor Cyan
Write-Host "    dist: $DIST"
Write-Host ""

$packages = @(
    @{ Project = "src\Overt.Cli\Overt.Cli.csproj";     Id = "Overt" },
    @{ Project = "src\Overt.Build\Overt.Build.csproj"; Id = "Overt.Build" }
)

Push-Location $ROOT
try {
    foreach ($pkg in $packages) {
        $proj = $pkg.Project
        $id = $pkg.Id
        Write-Host "    pack $id" -ForegroundColor DarkCyan
        dotnet pack $proj -c Release -o $DIST --version-suffix $SUFFIX -p:FileVersion=$FILE_VERSION --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet pack $proj failed" }
    }
} finally {
    Pop-Location
}

$cliNupkg = Join-Path $DIST "Overt.$VERSION.nupkg"
$buildNupkg = Join-Path $DIST "Overt.Build.$VERSION.nupkg"
foreach ($p in @($cliNupkg, $buildNupkg)) {
    if (-not (Test-Path $p)) { throw "Expected package not produced: $p" }
}

Write-Host ""
Write-Host "==> Produced" -ForegroundColor Green
Write-Host "    $cliNupkg"
Write-Host "    $buildNupkg"

if ($DO_INSTALL) {
    Write-Host ""
    Write-Host "==> Reinstalling global tool: overt $VERSION" -ForegroundColor Cyan
    # Uninstall is best-effort; first install has nothing to uninstall.
    dotnet tool uninstall --global Overt 2>$null | Out-Null
    $toolConfig = Join-Path $PSScriptRoot 'nuget.tool.config'
    dotnet tool install --global Overt --version $VERSION --add-source $DIST --configfile $toolConfig --ignore-failed-sources | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool install failed" }
    Write-Host ""
    Write-Host "==> overt is on PATH as version $VERSION" -ForegroundColor Green
    Write-Host "    Try: overt --version"
}

Write-Host ""
Write-Host "Channel counter: $COUNTER_FILE -> $N" -ForegroundColor DarkGray
