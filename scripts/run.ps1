#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot 'bootstrap.ps1')
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -SkipTests }
$desktop = Join-Path $repository "src/Workbench.Desktop/bin/$Configuration/net11.0/Workbench.Desktop.dll"
$host = Join-Path $repository "src/Workbench.Host/bin/$Configuration/net11.0/Workbench.Host.dll"
if (-not (Test-Path -LiteralPath $desktop) -or -not (Test-Path -LiteralPath $host)) { throw 'Desktop or Host output is missing. Run scripts/build.ps1 first.' }
$previousHost = $env:WORKBENCH_HOST_PATH
Push-Location $repository
try {
    $env:WORKBENCH_HOST_PATH = $host
    & $dotnet $desktop
    if ($LASTEXITCODE -ne 0) { throw "Workbench Desktop exited with code $LASTEXITCODE." }
}
finally { $env:WORKBENCH_HOST_PATH = $previousHost; Pop-Location }
