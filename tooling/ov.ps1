# ov.ps1 — convenience wrapper around the Overt CLI.
#
# Usage:
#   ov hello.ov              emit transpiled C# to stdout (defaults --emit=csharp)
#   ov --emit=tokens f.ov    any explicit --emit=<stage> passes through
#   ov run hello.ov          transpile, compile, and execute
#   ov --version             print version
#   ov --help                print usage
#
# Defaults to `--emit=csharp` only when no --emit flag and no subcommand
# (`run`) is present.
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

# Default to --emit=csharp only if no --emit flag AND no subcommand like `run`
# AND no info flag like --version/--help is present. Subcommands and flags
# forward verbatim.
$hasEmit = $false
$isSubcommand = $false
$isInfoFlag = $false
foreach ($a in $args) {
    if ($a -like '--emit=*') { $hasEmit = $true }
    if ($a -eq 'run') { $isSubcommand = $true }
    if ($a -eq '--version' -or $a -eq '--help' -or $a -eq '-h') { $isInfoFlag = $true }
}

if ($hasEmit -or $isSubcommand -or $isInfoFlag) {
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
