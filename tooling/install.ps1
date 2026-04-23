<#
.SYNOPSIS
    Publish the Overt CLI and install a shim into the user's bin directory.

.DESCRIPTION
    Runs `dotnet publish` on src/Overt.Cli and drops the artifacts into
    $Bin\overt\, then writes a tiny `overt.cmd` shim at $Bin\overt.cmd. With
    $Bin on PATH, `overt hello.ov` works from anywhere — no manual DLL copying
    per build.

    By design there is no watch mode. Re-run this script whenever you want the
    installed copy to reflect new compiler changes. One explicit install =
    one known snapshot, no mystery about which overt is on PATH.

.PARAMETER Configuration
    Build configuration to publish. "Debug" or "Release". Defaults to Release.

.PARAMETER Bin
    Install location. Defaults to $HOME\bin.

.PARAMETER Force
    Delete a stale $Bin\overt.exe (typically left over from an earlier manual
    copy-based install) before writing the shim. Without -Force the script
    warns about it and leaves the file alone so you can decide.

.EXAMPLE
    .\tooling\install.ps1
        Publish Release and install to $HOME\bin.

.EXAMPLE
    .\tooling\install.ps1 -Configuration Debug
        Publish Debug and install to $HOME\bin — useful while iterating on the
        compiler.

.EXAMPLE
    .\tooling\install.ps1 -Force
        Replace a previous manual install outright. Removes $Bin\overt.exe
        first, then installs.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Bin = (Join-Path $HOME 'bin'),

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$config = $Configuration
$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src\Overt.Cli'
$installDir = Join-Path $Bin 'overt'

if (-not (Test-Path $Bin)) {
    New-Item -ItemType Directory -Path $Bin | Out-Null
}

Write-Host "Publishing $config overt -> $installDir" -ForegroundColor Cyan
& dotnet publish $project -c $config -o $installDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed"
    exit $LASTEXITCODE
}

# One-line cmd shim. Uses %~dp0 so it works regardless of cwd. The published
# overt.exe is a native launcher that finds its DLLs via the install dir, so
# this is as fast as launching a normal .NET app.
$shim = Join-Path $Bin 'overt.cmd'
@"
@echo off
"%~dp0overt\overt.exe" %*
"@ | Set-Content -Path $shim -Encoding ASCII

# Handle a previously-manual install that would shadow the new shim. On
# Windows, PATHEXT puts .EXE ahead of .CMD, so a stray overt.exe in $Bin wins.
# With -Force we just remove it; without, warn and leave it.
$strayExe = Join-Path $Bin 'overt.exe'
if (Test-Path $strayExe) {
    if ($Force) {
        Remove-Item $strayExe -Force
        Write-Host "Removed stale $strayExe" -ForegroundColor Yellow
    } else {
        Write-Warning "$strayExe exists and will shadow the new overt.cmd shim (PATHEXT puts .EXE before .CMD)."
        Write-Warning "Re-run with -Force to remove it, or delete it manually."
    }
}

Write-Host ""
Write-Host "Installed:" -ForegroundColor Green
Write-Host "  $shim"
Write-Host "  $installDir\  (artifacts)"
Write-Host ""
Write-Host "Check: overt --version"
