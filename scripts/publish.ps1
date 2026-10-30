#requires -Version 7.2
<## Produces a portable directory containing Desktop, Host and their dependencies.
    Example: pwsh scripts/publish.ps1 -Runtime linux-x64 -OutputDirectory artifacts/linux
    Run build.ps1 first to validate tests. This script does not launch the GUI. ##>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [Alias('RuntimeIdentifier')][string] $Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [string] $OutputDirectory,
    [switch] $FrameworkDependent,
    [switch] $HostOnly
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$dotnet = & (Join-Path $PSScriptRoot 'bootstrap.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository "artifacts/publish/$Runtime" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
    throw "Output directory is not empty: $output. Choose a new -OutputDirectory to avoid stale or overwritten files."
}
$selfContained = (-not $FrameworkDependent).ToString().ToLowerInvariant()
$publishWork = Join-Path $repository ('artifacts/publish-work/' + [Guid]::NewGuid().ToString('N'))
$lockDirectory = Join-Path $publishWork 'locks'
New-Item -ItemType Directory -Path $lockDirectory -Force | Out-Null
$targets = Join-Path $publishWork 'publish.targets'
# Separate per-project RID locks keep source locks unchanged. MSBuild evaluates the project name here.
'<Project><PropertyGroup><NuGetLockFilePath>$(MSBuildThisFileDirectory)locks/$(MSBuildProjectName).packages.lock.json</NuGetLockFilePath></PropertyGroup></Project>' | Set-Content -LiteralPath $targets -Encoding utf8
$lockOption = '-p:CustomBeforeMicrosoftCommonTargets=' + $targets
$projects = if ($HostOnly) { @('Workbench.Host') } else { @('Workbench.Desktop', 'Workbench.Host') }
$sourceProjects = Get-ChildItem -LiteralPath (Join-Path $repository 'src') -Filter '*.csproj' -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
foreach ($project in $sourceProjects) {
    $sourceLock = Join-Path $project.DirectoryName 'packages.lock.json'
    if (-not (Test-Path -LiteralPath $sourceLock)) { throw "Missing committed dependency lock: $sourceLock" }
    Copy-Item -LiteralPath $sourceLock -Destination (Join-Path $lockDirectory ($project.BaseName + '.packages.lock.json'))
}
Push-Location $repository
try {
    & $dotnet restore Workbench.slnx --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Repository dependency lock validation failed.' }
    foreach ($name in $projects) {
        & $dotnet restore "src/$name/$name.csproj" --runtime $Runtime "-p:SelfContained=$selfContained" $lockOption --nologo
        if ($LASTEXITCODE -ne 0) { throw "Runtime restore failed for $name." }
    }
    # RID targets may be added, but all existing package versions and hashes must stay pinned.
    foreach ($project in $sourceProjects) {
        $original = Get-Content -LiteralPath (Join-Path $project.DirectoryName 'packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
        $resolved = Get-Content -LiteralPath (Join-Path $lockDirectory ($project.BaseName + '.packages.lock.json')) -Raw | ConvertFrom-Json -AsHashtable
        foreach ($framework in $original.dependencies.Keys) {
            $before = $original.dependencies[$framework] | ConvertTo-Json -Depth 100 -Compress
            $after = $resolved.dependencies[$framework] | ConvertTo-Json -Depth 100 -Compress
            if ($before -cne $after) { throw "Publish restore changed pinned dependencies for $($project.BaseName)/$framework." }
        }
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $marker = Join-Path $output '.publish-incomplete'
    'This directory is incomplete until publish.ps1 succeeds.' | Set-Content -LiteralPath $marker
    foreach ($name in $projects) {
        & $dotnet publish "src/$name/$name.csproj" --configuration $Configuration --runtime $Runtime --self-contained $selfContained --no-restore --output $output $lockOption -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:ContinuousIntegrationBuild=true --nologo -warnaserror
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $name; output retains its incomplete marker." }
    }
    $suffix = if ($Runtime.StartsWith('win-')) { '.exe' } else { '' }
    foreach ($name in $projects) {
        if (-not (Test-Path -LiteralPath (Join-Path $output ($name + $suffix)))) { throw "Published executable missing: $name" }
    }
    Copy-Item -LiteralPath $lockDirectory -Destination (Join-Path $output 'dependency-locks') -Recurse
    @{
        sdkVersion = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
        runtime = $Runtime
        selfContained = -not $FrameworkDependent
        configuration = $Configuration
        headlessOnly = [bool]$HostOnly
        guiVerification = 'Not performed by the publish script; verify on the target operating system.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'publish-info.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'write-release-metadata.ps1') -PublishDirectory $output
    # PowerShell treats dot-prefixed Unix files as hidden, even when their owner can delete them.
    Remove-Item -LiteralPath $marker -Force
    Write-Host "Published portable Workbench to $output"
    Write-Output $output
}
finally { Pop-Location }
