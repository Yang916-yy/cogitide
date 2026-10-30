#requires -Version 7.2
<## Installs the SDK pinned by global.json only when it is not already available.
    Returns the dotnet executable path and sets environment variables for this process. ##>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$sdkVersion = (Get-Content -LiteralPath (Join-Path $repository 'global.json') -Raw | ConvertFrom-Json).sdk.version
$installDirectory = Join-Path $repository '.tools/dotnet'
$executableName = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
$privateExecutable = Join-Path $installDirectory $executableName

function Test-PinnedSdk([string] $candidate) {
    if (-not $candidate -or -not (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $false }
    try {
        $version = & $candidate --version 2>$null
        return $LASTEXITCODE -eq 0 -and ($version | Select-Object -Last 1) -eq $sdkVersion
    }
    catch { return $false }
}

Push-Location $repository
try {
    $dotnet = $null
    if (Test-PinnedSdk $privateExecutable) { $dotnet = $privateExecutable }
    if (-not $dotnet) {
        $existing = Get-Command $executableName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($existing -and (Test-PinnedSdk $existing.Source)) {
            $dotnet = $existing.Source
            $target = (Get-Item -LiteralPath $dotnet).ResolveLinkTarget($true)
            if ($target) { $dotnet = $target.FullName }
        }
    }
    if (-not $dotnet) {
        New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
        $installerName = if ($IsWindows) { 'dotnet-install.ps1' } else { 'dotnet-install.sh' }
        $installer = Join-Path $repository ('.tools/' + $installerName)
        Write-Host "Installing .NET SDK $sdkVersion into $installDirectory"
        # Microsoft installer performs the archive selection; no hand-selected SDK archive is used.
        Invoke-WebRequest -Uri ('https://dot.net/v1/' + $installerName) -OutFile $installer -MaximumRetryCount 3
        if ($IsWindows) {
            & $installer -Version $sdkVersion -InstallDir $installDirectory -NoPath | Out-Host
        }
        else {
            & bash $installer --version $sdkVersion --install-dir $installDirectory --no-path | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'The official .NET installer failed.' }
        }
        if (-not (Test-PinnedSdk $privateExecutable)) { throw "The required SDK $sdkVersion was not installed successfully." }
        $dotnet = $privateExecutable
    }
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:DOTNET_HOST_PATH = $dotnet
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    if (($env:PATH -split [IO.Path]::PathSeparator) -notcontains $env:DOTNET_ROOT) {
        $env:PATH = $env:DOTNET_ROOT + [IO.Path]::PathSeparator + $env:PATH
    }
    Write-Host "Using .NET SDK $sdkVersion ($dotnet)"
    Write-Output $dotnet
}
finally { Pop-Location }
