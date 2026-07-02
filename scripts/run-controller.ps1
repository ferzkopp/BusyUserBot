<#
.SYNOPSIS
    Builds and runs the BusyUserBot controller (WinForms app).

.DESCRIPTION
    Convenience wrapper that locates dotnet, builds the controller solution
    and launches the resulting app. `dotnet` is not on PATH on the dev
    machine, so this script falls back to the standard install location
    (C:\Program Files\dotnet\dotnet.exe) when needed.

.PARAMETER Configuration
    Build configuration to use. Defaults to Debug.

.PARAMETER NoBuild
    Skip the build step and just run the existing binaries.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'controller\src\BusyUserBot'
$project    = Join-Path $projectDir 'BusyUserBot.csproj'

if (-not (Test-Path $project)) {
    throw "Could not find controller project at: $project"
}

# Resolve dotnet: prefer PATH, fall back to the standard install location.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
}
if (-not (Test-Path $dotnet)) {
    throw "Could not find dotnet. Install the .NET 8 SDK or run scripts\dev-env-setup.ps1."
}

if (-not $NoBuild) {
    Write-Host "Building controller ($Configuration)..." -ForegroundColor Cyan
    & $dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed (exit code $LASTEXITCODE)."
    }
}

Write-Host "Running controller..." -ForegroundColor Cyan
& $dotnet run --project $project -c $Configuration --no-build
