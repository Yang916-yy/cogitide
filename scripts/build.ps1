#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [switch] $SkipTests
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot 'bootstrap.ps1')
Push-Location $repository
try {
    & $dotnet restore Workbench.slnx --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed. Update package locks deliberately after changing dependencies.' }
    & $dotnet build Workbench.slnx --configuration $Configuration --no-restore --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    if (-not $SkipTests) {
        & (Join-Path $repository "tests/Workbench.Tests/bin/$Configuration/net11.0/playwright.ps1") install --only-shell chromium
        if ($LASTEXITCODE -ne 0) { throw 'Playwright Chromium installation failed.' }
        & $dotnet test tests/Workbench.Tests/Workbench.Tests.csproj --configuration $Configuration --no-build --no-restore --nologo --logger 'trx;LogFileName=workbench.trx' --results-directory (Join-Path $repository 'artifacts/test-results')
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
}
finally { Pop-Location }
