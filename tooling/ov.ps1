# ov.ps1 — convenience wrapper around the Overt CLI.
#
# Usage: ov [--emit=<stage>] <file.ov> [extra args...]
#
# Defaults to `--emit=csharp` when no --emit flag is present, so the common case
# `ov hello.ov` prints the transpiled C# to stdout.
#
# Install: copy or symlink this file into a directory on your PATH (e.g.
# $HOME\bin or C:\Users\paul\bin). PowerShell picks up .ps1 files by name if
# execution policy permits. If `ov` doesn't run, check:
#   Get-ExecutionPolicy -Scope CurrentUser
# and if needed:
#   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
#
# Alternative: add a function to your PowerShell profile ($PROFILE) instead of
# copying this file — see the bottom of this script for the equivalent snippet.

$ErrorActionPreference = 'Stop'

# Repo root. Hard-coded because that's where the CLI is built; if you move the
# repo, update this or set $env:OVERT_ROOT before invoking.
$OvertRoot = if ($env:OVERT_ROOT) { $env:OVERT_ROOT } else { 'C:\Users\paul\source\repos\Overt' }
$OvertCli  = Join-Path $OvertRoot 'src\Overt.Cli\bin\Debug\net9.0\overt.dll'

if (-not (Test-Path $OvertCli)) {
    Write-Error "Overt CLI not built at $OvertCli. Run 'dotnet build' in $OvertRoot first."
    exit 1
}

# If the user didn't pass an --emit=... flag, default to csharp.
$hasEmit = $false
foreach ($a in $args) {
    if ($a -like '--emit=*') { $hasEmit = $true; break }
}

if ($hasEmit) {
    & dotnet $OvertCli @args
} else {
    & dotnet $OvertCli '--emit=csharp' @args
}

exit $LASTEXITCODE

# ----------------------------------------------------------------------------
# Profile-function alternative (paste into $PROFILE instead of using this file):
#
# function ov {
#     $cli = 'C:\Users\paul\source\repos\Overt\src\Overt.Cli\bin\Debug\net9.0\overt.dll'
#     if ($args -match '^--emit=') {
#         dotnet $cli @args
#     } else {
#         dotnet $cli '--emit=csharp' @args
#     }
# }
#
# Then `. $PROFILE` (or restart PowerShell) and `ov hello.ov` works everywhere.
