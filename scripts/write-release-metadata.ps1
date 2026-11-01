#requires -Version 7.2
<## Adds launchers, release instructions and runtime metadata to an existing publish directory.
    Called by publish.ps1; does not build, run or rename the applications. ##>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $PublishDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PublishDirectory).Path
$infoPath = Join-Path $package 'publish-info.json'
$info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json -AsHashtable
$runtime = $info.runtime
if ($runtime -notmatch '^(win|linux|osx)-(x64|arm64)$') { throw 'Publish metadata contains an unsupported runtime identifier.' }
$suffix = if ($runtime.StartsWith('win-')) { '.exe' } else { '' }
$versions = @()
$hostFrameworks = @()
$headlessOnly = $info.ContainsKey('headlessOnly') -and [bool]$info.headlessOnly
$applications = if ($headlessOnly) { @('Workbench.Host') } else { @('Workbench.Desktop', 'Workbench.Host') }
foreach ($application in $applications) {
    foreach ($file in @(($application + $suffix), ($application + '.dll'), ($application + '.deps.json'), ($application + '.runtimeconfig.json'))) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $file) -PathType Leaf)) { throw "Missing published component: $file" }
    }
    $config = Get-Content -LiteralPath (Join-Path $package ($application + '.runtimeconfig.json')) -Raw | ConvertFrom-Json
    $frameworks = if ($info.selfContained) { $config.runtimeOptions.includedFrameworks } else { @($config.runtimeOptions.framework) + @($config.runtimeOptions.frameworks) }
    $version = @($frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' } | Select-Object -ExpandProperty version)
    if ($version.Count -ne 1) { throw "Unable to identify the required runtime for $application." }
    $versions += $version[0]
    if ($application -eq 'Workbench.Host') { $hostFrameworks = @($frameworks | Where-Object { $_.name }) }
}
if (@($versions | Select-Object -Unique).Count -ne 1) { throw 'Published components require different .NET runtime versions.' }
$aspNet = @($hostFrameworks | Where-Object { $_.name -eq 'Microsoft.AspNetCore.App' })
if ($aspNet.Count -ne 1) { throw 'The durable Host requires the ASP.NET Core framework in its runtime metadata.' }
$hostDependencies = Get-Content -LiteralPath (Join-Path $package 'Workbench.Host.deps.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($dependency in @('StreamJsonRpc/2.26.150', 'Microsoft.Mxc.Sdk/1.0.1')) {
    if (-not $hostDependencies.libraries.ContainsKey($dependency)) { throw "Missing pinned development dependency: $dependency" }
}
# Preserve the original upstream package notice, not a reconstruction of its transitive attribution.
# Restore has already resolved these roots; no SDK operation or download is performed here.
$assets = Get-Content -LiteralPath (Join-Path $repository 'src/Workbench.Tools/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$noticeSources = @($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ 'streamjsonrpc/2.26.150/NOTICE' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
if ($noticeSources.Count -eq 0) { throw 'The restored StreamJsonRpc 2.26.150 package NOTICE is missing.' }
$noticeSource = $noticeSources[0]
$noticeHash = 'fe05842fd5c4c46d9da8559e6a7bb61e5e6d09ff44d4d4c1caead34f4e576798'
if ((Get-FileHash -LiteralPath $noticeSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $noticeHash) { throw 'The pinned StreamJsonRpc package NOTICE has changed.' }
$lspLicenses = Join-Path $package 'Lsp/Licenses'
New-Item -ItemType Directory -Path $lspLicenses -Force | Out-Null
Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $lspLicenses 'StreamJsonRpc-NOTICE.txt')
$nativeIsolationAssets = @(if ($runtime.StartsWith('win-')) { 'mxc_ffi.dll'; 'plm.exe'; 'wslcsdk.dll'; 'wxc-wslc-daemon.exe' }
    elseif ($runtime.StartsWith('linux-')) { 'libmxc_ffi.so' }
    elseif ($runtime -eq 'osx-arm64') { 'libmxc_ffi.dylib' }) # Microsoft.Mxc.Sdk 1.0.1 has no Intel macOS native asset.
foreach ($asset in $nativeIsolationAssets) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $asset) -PathType Leaf)) { throw "Missing MXC native package asset: $asset" }
}
$info.formatVersion = 1
$info.displayName = 'Cogitide'
$info.releaseChannel = 'development-preview'
$info.runtimeVersion = $versions[0]
$info.desktopExecutable = if ($headlessOnly) { $null } else { 'Workbench.Desktop' + $suffix }
$info.packageKind = if ($headlessOnly) { 'host' } else { 'desktop' }
$info.hostExecutable = 'Workbench.Host' + $suffix
$info.defaultDataLocation = 'Per-user local application data / Workbench; independent of this package directory.'
$info.launcher = if ($runtime.StartsWith('win-')) { 'start.cmd' } else { 'start.sh' }
$info.runtimeBundled = [bool]$info.selfContained
$info.hostRuntimeFrameworks = @($hostFrameworks | ForEach-Object { @{ name = $_.name; version = $_.version } })
$info.osIsolation = @{
    sdkVersion = '1.0.1'
    supportedByPinnedPackage = $nativeIsolationAssets.Count -gt 0
    nativeAssets = @($nativeIsolationAssets)
    verification = 'Package content only; native policy launch and denial tests are separate platform acceptance.'
}
$info | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $infoPath -Encoding utf8

if ($runtime.StartsWith('win-')) {
    $launcher = @'
@echo off
setlocal
set "WORKBENCH_HOST_PATH=%~dp0Workbench.Host.exe"
"%~dp0Workbench.Desktop.exe" %*
exit /b %errorlevel%
'@
    if ($headlessOnly) {
        $launcher = '@echo off' + "`n" + '"%~dp0Workbench.Host.exe" --stdio %*' + "`n" + 'exit /b %errorlevel%'
    }
    [IO.File]::WriteAllText((Join-Path $package 'start.cmd'), $launcher.Replace("`r`n", "`n").Replace("`n", "`r`n") + "`r`n", [Text.Encoding]::ASCII)
}
else {
    $launcher = @'
#!/bin/sh
set -eu
app_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
for app in Workbench.Desktop Workbench.Host; do
    if [ ! -x "$app_dir/$app" ]; then
        chmod u+x "$app_dir/$app" || { printf '%s\n' "Cannot enable execution for $app. Copy the complete package to a writable local directory." >&2; exit 1; }
    fi
done
WORKBENCH_HOST_PATH="$app_dir/Workbench.Host"
export WORKBENCH_HOST_PATH
exec "$app_dir/Workbench.Desktop" "$@"
'@
    if ($headlessOnly) {
        $launcher = @'
#!/bin/sh
set -eu
app_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ ! -x "$app_dir/Workbench.Host" ]; then chmod u+x "$app_dir/Workbench.Host"; fi
exec "$app_dir/Workbench.Host" --stdio "$@"
'@
    }
    [IO.File]::WriteAllText((Join-Path $package 'start.sh'), $launcher.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
    if (-not $IsWindows) {
        $executables = @((Join-Path $package 'start.sh')) + @($applications | ForEach-Object { Join-Path $package $_ })
        & chmod u+x @executables
        if ($LASTEXITCODE -ne 0) { throw 'Could not set executable permissions on the package launchers.' }
    }
}
$readme = if ($headlessOnly) { 'docs/remote-host-package.md' } else { 'docs/release.md' }
Copy-Item -LiteralPath (Join-Path $repository $readme) -Destination (Join-Path $package 'README.md')
foreach ($notice in @('LICENSE', 'NOTICE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $repository $notice) -Destination (Join-Path $package $notice)
}
$hashes = Get-ChildItem -LiteralPath $package -File -Recurse -Force | Where-Object { $_.FullName -ne (Join-Path $package 'SHA256SUMS') -and $_.Name -ne '.publish-incomplete' } | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
}
$hashes | Set-Content -LiteralPath (Join-Path $package 'SHA256SUMS') -Encoding utf8
Write-Host "Prepared launch instructions and runtime metadata: $package"
