<#
.SYNOPSIS
    Publish the Overt CLI directly into the user's bin directory.

.DESCRIPTION
    Runs `dotnet publish` on src/Overt.Cli into $Bin, replacing whatever
    previous copy is there (typically from a manual copy-based workflow).
    With $Bin on PATH, `overt hello.ov` works from anywhere.

    Re-run this whenever you want the on-PATH copy to reflect new compiler
    changes. No watch mode by design — one explicit install = one known
    snapshot.

.PARAMETER Configuration
    Build configuration to publish. "Debug" or "Release". Defaults to Release.

.PARAMETER Bin
    Install location. Defaults to $HOME\bin.

.EXAMPLE
    .\tooling\install.ps1
        Publish Release and install to $HOME\bin.

.EXAMPLE
    .\tooling\install.ps1 -Configuration Debug -Bin D:\tools
        Publish Debug and install into D:\tools.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Bin = (Join-Path $HOME 'bin')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'src\Overt.Cli'

if (-not (Test-Path $Bin)) {
    New-Item -ItemType Directory -Path $Bin | Out-Null
}

# Snapshot overt.* before publish so we can tell the user what changed.
$before = @{}
Get-ChildItem -Path $Bin -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'overt.*' -or $_.Name -like 'Overt.*' } |
    ForEach-Object { $before[$_.Name] = $_.LastWriteTimeUtc }

Write-Host "Publishing $Configuration overt -> $Bin" -ForegroundColor Cyan
& dotnet publish $project -c $Configuration -o $Bin --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed"
    exit $LASTEXITCODE
}

# Compare after: everything the publish wrote that was new or updated.
$replaced = 0
$added    = 0
Get-ChildItem -Path $Bin -File |
    Where-Object { $_.Name -like 'overt.*' -or $_.Name -like 'Overt.*' } |
    ForEach-Object {
        if ($before.ContainsKey($_.Name)) {
            if ($before[$_.Name] -ne $_.LastWriteTimeUtc) { $replaced++ }
        } else {
            $added++
        }
    }

Write-Host ""
Write-Host "Installed to $Bin ($replaced replaced, $added added)." -ForegroundColor Green

# Also publish the blessed stdlib — facade files that the CLI auto-discovers
# when resolving `use stdlib.*`. Robocopy'd into `$Bin\stdlib\` so the CLI's
# walk-up search (see DiscoverSearchDirs) finds them next to overt.exe.
$stdlibSrc = Join-Path $repoRoot 'stdlib'
$stdlibDst = Join-Path $Bin 'stdlib'
if (Test-Path $stdlibSrc) {
    if (-not (Test-Path $stdlibDst)) {
        New-Item -ItemType Directory -Path $stdlibDst | Out-Null
    }
    # Copy-Item recursively. -Force overwrites existing files (intended — this
    # script always replaces).
    Copy-Item -Path (Join-Path $stdlibSrc '*') -Destination $stdlibDst -Recurse -Force
    $facadeCount = (Get-ChildItem -Path $stdlibDst -Recurse -Filter '*.ov').Count
    Write-Host "Installed stdlib to $stdlibDst ($facadeCount facades)." -ForegroundColor Green
}

Write-Host "Check: overt --version"
