[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('windows-x64', 'macos-x64', 'linux-x64')][string]$Platform,
    [Parameter(Mandatory)][string]$RuntimeAssetsRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$RepositoryRoot,
    [ValidateRange(3, 60)][int]$SmokeSeconds = 8,
    [switch]$AllowSdkFallback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HistoricalRebuild.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-RepositoryRoot }
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$RuntimeAssetsRoot = [IO.Path]::GetFullPath($RuntimeAssetsRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$platformInfo = @(Get-HistoricalPlatforms -Selector $Platform)[0]
$expectedOS = switch ($Platform) {
    'windows-x64' { [Runtime.InteropServices.OSPlatform]::Windows }
    'macos-x64' { [Runtime.InteropServices.OSPlatform]::OSX }
    'linux-x64' { [Runtime.InteropServices.OSPlatform]::Linux }
}
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform($expectedOS)) {
    throw "$Platform must be built on its native operating system."
}
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw "$Platform requires an x64 build host."
}

$tag = "archive/v$Version"
$sourceCommit = Assert-HistoricalTag -RepositoryRoot $RepositoryRoot -Version $Version
$sourceTree = (& git -C $RepositoryRoot rev-parse "$tag^{tree}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Unable to resolve tree for $tag." }
$packageName = Get-HistoricalPackageName -Version $Version -Platform $Platform
$packagePath = Join-Path $OutputRoot $packageName
$resultPath = Join-Path $OutputRoot "result-$Version-$Platform.json"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "downkyi-historical-$Version-$Platform-$([Guid]::NewGuid().ToString('N'))"
$checkout = Join-Path $tempRoot 'source'
$publish = Join-Path $tempRoot 'publish'
$compatibility = [Collections.Generic.List[string]]::new()
$smoke = [ordered]@{
    status = 'not-run'
    seconds = $SmokeSeconds
    executable = $null
    isolation = 'A temporary non-network aria2 stub replaces aria2 only in the smoke-test copy; the package retains the verified real binary.'
    dependencyVersionProbes = 'not-run'
}
$signing = [ordered]@{ type = 'none'; notarized = $false; limitation = $null }
$success = $false
$failureText = $null
$sdkVersion = $null
$worktreeAdded = $false

function Invoke-Native {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $FilePath $($Arguments -join ' ')" }
}

function Invoke-DotnetQuiet {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $commandOutput = @(& dotnet @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $tail = @($commandOutput | Select-Object -Last 40 | ForEach-Object { [string]$_ }) -join "`n"
        throw "dotnet $($Arguments[0]) failed ($exitCode).`n$tail"
    }
    $warningCount = @($commandOutput | Where-Object { [string]$_ -match ': warning ' }).Count
    Write-Host "dotnet $($Arguments[0]) completed with $warningCount historical-source warning line(s)."
}

function Test-Launch {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{}
    )
    $stdout = Join-Path $tempRoot "smoke-$([Guid]::NewGuid().ToString('N')).out"
    $stderr = Join-Path $tempRoot "smoke-$([Guid]::NewGuid().ToString('N')).err"
    $oldValues = @{}
    $process = $null
    foreach ($key in $Environment.Keys) {
        $oldValues[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], 'Process')
    }
    try {
        $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -WorkingDirectory (Split-Path -Parent $FilePath) -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        if ($process.WaitForExit($SmokeSeconds * 1000)) {
            $details = ((Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue) + ' ' + (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)).Trim()
            throw "Smoke launch exited early with code $($process.ExitCode). $details"
        }
    }
    finally {
        if ($null -ne $process) {
            if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
                & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
            }
            else {
                & pkill -TERM -P $process.Id 2>$null
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
            Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
        foreach ($key in $Environment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $oldValues[$key], 'Process')
        }
    }
}

function Invoke-VersionProbe {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments)
    $probeOutput = @(& $FilePath @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Runtime dependency version probe failed: $([IO.Path]::GetFileName($FilePath))"
    }
    if ($probeOutput.Count -eq 0) {
        throw "Runtime dependency version probe returned no identity: $([IO.Path]::GetFileName($FilePath))"
    }
}

function New-IsolatedSmokeCopy {
    param(
        [Parameter(Mandatory)][string]$ApplicationRoot,
        [Parameter(Mandatory)][string]$AriaDirectory
    )
    $stubProject = Join-Path $tempRoot 'smoke-stub'
    $stubOutput = Join-Path $stubProject 'output'
    New-Item -ItemType Directory -Path $stubProject -Force | Out-Null
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net6.0</TargetFramework>
    <AssemblyName>aria2c</AssemblyName>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $stubProject 'aria2c.csproj') -Encoding utf8
    @'
using System;
using System.Threading;
Thread.Sleep(TimeSpan.FromMinutes(2));
'@ | Set-Content -LiteralPath (Join-Path $stubProject 'Program.cs') -Encoding utf8
    Invoke-DotnetQuiet 'publish' (Join-Path $stubProject 'aria2c.csproj') '-c' 'Release' '-r' $platformInfo.Rid '--self-contained' 'true' '-p:PublishTrimmed=false' '-o' $stubOutput
    if (Test-Path -LiteralPath $AriaDirectory) {
        Remove-Item -LiteralPath $AriaDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $AriaDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $stubOutput '*') -Destination $AriaDirectory -Recurse -Force
    if ($Platform -ne 'windows-x64') { Invoke-Native chmod '+x' '--' (Join-Path $AriaDirectory 'aria2c') }
    return $ApplicationRoot
}

function Get-Dotnet6Sdk {
    $versions = @(& dotnet --list-sdks | ForEach-Object {
        if ($_ -match '^(6\.0\.\d+)\s') { [version]$Matches[1] }
    } | Sort-Object -Descending)
    if ($versions.Count -gt 0) { return $versions[0].ToString() }
    if ($AllowSdkFallback) {
        $fallback = (& dotnet --version).Trim()
        $compatibility.Add("No .NET 6 SDK was installed; used SDK $fallback with roll-forward for local converter verification.")
        return $fallback
    }
    throw 'A .NET 6 SDK is required. The workflow installs 6.0.x explicitly.'
}

function Install-RuntimeAssets {
    $binaryRoot = Join-Path $checkout "DownKyi.Core/Binary/$($platformInfo.Rid)"
    $runtimeMetadataPath = Join-Path $RuntimeAssetsRoot 'runtime-assets.json'
    if (-not (Test-Path -LiteralPath $runtimeMetadataPath -PathType Leaf)) { throw 'Prepared runtime metadata is missing.' }
    $runtimeMetadata = Get-Content -LiteralPath $runtimeMetadataPath -Raw | ConvertFrom-Json
    if ([string]$runtimeMetadata.rid -ne $platformInfo.Rid) { throw "Prepared runtime RID is '$($runtimeMetadata.rid)', expected '$($platformInfo.Rid)'." }
    $ariaName = if ($Platform -eq 'windows-x64') { 'aria2c.exe' } else { 'aria2c' }
    $ffmpegName = if ($Platform -eq 'windows-x64') { 'ffmpeg.exe' } else { 'ffmpeg' }
    $ffprobeName = if ($Platform -eq 'windows-x64') { 'ffprobe.exe' } else { 'ffprobe' }
    foreach ($entry in @(
        @{ Name = 'aria2'; Source = (Join-Path $RuntimeAssetsRoot "aria2/$ariaName"); Destination = (Join-Path $binaryRoot "aria2/$ariaName") },
        @{ Name = 'ffmpeg'; Source = (Join-Path $RuntimeAssetsRoot "ffmpeg/$ffmpegName"); Destination = (Join-Path $binaryRoot "ffmpeg/$ffmpegName") },
        @{ Name = 'ffprobe'; Source = (Join-Path $RuntimeAssetsRoot "ffmpeg/$ffprobeName"); Destination = (Join-Path $binaryRoot "ffmpeg/$ffprobeName") }
    )) {
        if (-not (Test-Path -LiteralPath $entry.Source -PathType Leaf)) { throw "Prepared runtime asset is missing: $($entry.Source)" }
        $metadataEntries = @($runtimeMetadata.assets | Where-Object { [string]$_.name -eq $entry.Name })
        if ($metadataEntries.Count -ne 1 -or [string]$metadataEntries[0].binarySha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "Prepared runtime metadata is missing a unique checksum for $($entry.Name)."
        }
        $actualSourceHash = (Get-FileHash -LiteralPath $entry.Source -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualSourceHash -ne ([string]$metadataEntries[0].binarySha256).ToLowerInvariant()) {
            throw "Prepared runtime checksum mismatch for $($entry.Name)."
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Destination) -Force | Out-Null
        Copy-Item -LiteralPath $entry.Source -Destination $entry.Destination -Force
        if ($Platform -ne 'windows-x64') { Invoke-Native chmod '+x' '--' $entry.Destination }
    }
    $compatibility.Add('Injected converter-prepared, checksum-verified aria2 and FFmpeg runtime binaries in the temporary checkout; the historical commit was not changed.')
}

function Assert-PublishOutput {
    $appName = if ($Platform -eq 'windows-x64') { 'DownKyi.exe' } else { 'DownKyi' }
    $ariaRelative = if ($Platform -eq 'windows-x64') { 'aria2/aria2c.exe' } else { 'aria2/aria2c' }
    $ffmpegRelative = if ($Platform -eq 'windows-x64') { 'ffmpeg/ffmpeg.exe' } else { 'ffmpeg/ffmpeg' }
    $ffprobeRelative = if ($Platform -eq 'windows-x64') { 'ffmpeg/ffprobe.exe' } else { 'ffmpeg/ffprobe' }
    foreach ($path in @(
        (Join-Path $publish $appName),
        (Join-Path $publish 'DownKyi.dll'),
        (Join-Path $publish 'DownKyi.deps.json'),
        (Join-Path $publish $ariaRelative),
        (Join-Path $publish $ffmpegRelative),
        (Join-Path $publish $ffprobeRelative)
    )) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Published output is missing: $path" }
    }
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish 'DownKyi.dll')).Version
    if ($assemblyVersion.ToString(3) -ne $Version) {
        throw "DownKyi.dll version is $assemblyVersion; expected $Version."
    }
    $deps = Get-Content -LiteralPath (Join-Path $publish 'DownKyi.deps.json') -Raw
    if ($deps -notmatch [regex]::Escape("DownKyi/$Version")) {
        throw "DownKyi.deps.json does not identify application version $Version."
    }
    Invoke-VersionProbe -FilePath (Join-Path $publish $ariaRelative) -Arguments @('--version')
    Invoke-VersionProbe -FilePath (Join-Path $publish $ffmpegRelative) -Arguments @('-version')
    Invoke-VersionProbe -FilePath (Join-Path $publish $ffprobeRelative) -Arguments @('-version')
    $smoke.dependencyVersionProbes = 'passed'
}

function New-WindowsPackage {
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $packagePath -CompressionLevel Optimal -Force
    $verifyRoot = Join-Path $tempRoot 'verify-zip'
    Expand-Archive -LiteralPath $packagePath -DestinationPath $verifyRoot
    foreach ($relative in @('DownKyi.exe', 'DownKyi.dll', 'aria2/aria2c.exe', 'ffmpeg/ffmpeg.exe', 'ffmpeg/ffprobe.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $verifyRoot $relative) -PathType Leaf)) { throw "ZIP verification failed: $relative is missing." }
    }
    [void](New-IsolatedSmokeCopy -ApplicationRoot $verifyRoot -AriaDirectory (Join-Path $verifyRoot 'aria2'))
    $smoke.executable = 'DownKyi.exe from extracted ZIP'
    Test-Launch -FilePath (Join-Path $verifyRoot 'DownKyi.exe')
    $smoke.status = 'passed-with-network-helper-isolated'
}

function New-MacPackage {
    $appBundle = Join-Path $tempRoot '哔哩下载姬.app'
    $macOSRoot = Join-Path $appBundle 'Contents/MacOS'
    $resourceRoot = Join-Path $appBundle 'Contents/Resources'
    New-Item -ItemType Directory -Path $macOSRoot, $resourceRoot -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $checkout 'script/macos/Info.plist') -Destination (Join-Path $appBundle 'Contents/Info.plist')
    Copy-Item -LiteralPath (Join-Path $checkout 'script/macos/logo.icns') -Destination (Join-Path $resourceRoot 'logo.icns')
    Copy-Item -Path (Join-Path $publish '*') -Destination $macOSRoot -Recurse -Force
    $plist = Join-Path $appBundle 'Contents/Info.plist'
    Invoke-Native /usr/libexec/PlistBuddy '-c' "Set :CFBundleVersion $Version" $plist
    Invoke-Native /usr/libexec/PlistBuddy '-c' "Set :CFBundleShortVersionString $Version" $plist
    Invoke-Native chmod '+x' '--' (Join-Path $macOSRoot 'DownKyi') (Join-Path $macOSRoot 'aria2/aria2c') (Join-Path $macOSRoot 'ffmpeg/ffmpeg') (Join-Path $macOSRoot 'ffmpeg/ffprobe')
    Invoke-Native codesign '--deep' '--force' '--sign' '-' '--timestamp=none' $appBundle
    Invoke-Native codesign '--verify' '--deep' '--strict' '--verbose=2' $appBundle
    $signing.type = 'ad-hoc'
    $signing.limitation = 'Ad-hoc signed only; not Apple Developer ID signed, notarized, or stapled.'
    Invoke-Native hdiutil 'create' '-volname' "DownKyi $Version Historical" '-srcfolder' $appBundle '-ov' '-format' 'UDZO' $packagePath
    Invoke-Native hdiutil 'verify' $packagePath
    $smokeBundle = Join-Path $tempRoot 'smoke/哔哩下载姬.app'
    New-Item -ItemType Directory -Path (Split-Path -Parent $smokeBundle) -Force | Out-Null
    Copy-Item -LiteralPath $appBundle -Destination $smokeBundle -Recurse
    $smokeMacOS = Join-Path $smokeBundle 'Contents/MacOS'
    [void](New-IsolatedSmokeCopy -ApplicationRoot $smokeBundle -AriaDirectory (Join-Path $smokeMacOS 'aria2'))
    $smoke.executable = '哔哩下载姬.app/Contents/MacOS/DownKyi from a DMG-source copy'
    Test-Launch -FilePath (Join-Path $smokeMacOS 'DownKyi')
    $smoke.status = 'passed-with-network-helper-isolated'
}

function New-LinuxPackage {
    $appDir = Join-Path $tempRoot 'DownKyi.AppDir'
    $binRoot = Join-Path $appDir 'usr/bin'
    New-Item -ItemType Directory -Path $binRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination $binRoot -Recurse -Force
    $icon = Join-Path $checkout 'script/pupnet/icons/logo.256.png'
    Copy-Item -LiteralPath $icon -Destination (Join-Path $appDir 'downkyi.png')
    $desktop = @"
[Desktop Entry]
Type=Application
Name=哔哩下载姬
Comment=Historical rebuild of DownKyi $Version
Exec=DownKyi
Icon=downkyi
Terminal=false
Categories=Utility;
"@
    Set-Content -LiteralPath (Join-Path $appDir 'downkyi.desktop') -Value $desktop -Encoding utf8NoBOM
    $appRun = @'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/DownKyi" "$@"
'@
    Set-Content -LiteralPath (Join-Path $appDir 'AppRun') -Value $appRun -Encoding utf8NoBOM
    Invoke-Native chmod '+x' '--' (Join-Path $appDir 'AppRun') (Join-Path $binRoot 'DownKyi') (Join-Path $binRoot 'aria2/aria2c') (Join-Path $binRoot 'ffmpeg/ffmpeg') (Join-Path $binRoot 'ffmpeg/ffprobe')
    $tool = Join-Path $RuntimeAssetsRoot 'appimagetool/appimagetool-x86_64.AppImage'
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Prepared appimagetool is missing.' }
    $toolMetadata = Get-Content -LiteralPath (Join-Path $RuntimeAssetsRoot 'runtime-assets.json') -Raw | ConvertFrom-Json
    $toolEntries = @($toolMetadata.assets | Where-Object { [string]$_.name -eq 'appimagetool' })
    if ($toolEntries.Count -ne 1 -or (Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$toolEntries[0].binarySha256).ToLowerInvariant()) {
        throw 'Prepared appimagetool checksum does not match runtime metadata.'
    }
    Invoke-Native chmod '+x' '--' $tool
    $oldArch = $env:ARCH
    $oldExtract = $env:APPIMAGE_EXTRACT_AND_RUN
    try {
        $env:ARCH = 'x86_64'
        $env:APPIMAGE_EXTRACT_AND_RUN = '1'
        Invoke-Native $tool '--no-appstream' $appDir $packagePath
        $verifyRoot = Join-Path $tempRoot 'verify-appimage'
        New-Item -ItemType Directory -Path $verifyRoot -Force | Out-Null
        Push-Location $verifyRoot
        try { Invoke-Native $packagePath '--appimage-extract' }
        finally { Pop-Location }
        foreach ($relative in @('squashfs-root/AppRun', 'squashfs-root/usr/bin/DownKyi', 'squashfs-root/usr/bin/aria2/aria2c', 'squashfs-root/usr/bin/ffmpeg/ffmpeg')) {
            if (-not (Test-Path -LiteralPath (Join-Path $verifyRoot $relative) -PathType Leaf)) { throw "AppImage verification failed: $relative is missing." }
        }
        $extractedRoot = Join-Path $verifyRoot 'squashfs-root'
        [void](New-IsolatedSmokeCopy -ApplicationRoot $extractedRoot -AriaDirectory (Join-Path $extractedRoot 'usr/bin/aria2'))
        $smoke.executable = 'AppRun from extracted AppImage'
        $launcher = (Get-Command xvfb-run -ErrorAction SilentlyContinue)
        if ($null -eq $launcher) { throw 'xvfb-run is required for the Linux GUI launch check.' }
        Test-Launch -FilePath $launcher.Source -Arguments @('-a', (Join-Path $extractedRoot 'AppRun'))
        $smoke.status = 'passed-with-network-helper-isolated'
    }
    finally {
        $env:ARCH = $oldArch
        $env:APPIMAGE_EXTRACT_AND_RUN = $oldExtract
    }
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Invoke-Native git '-C' $RepositoryRoot 'worktree' 'add' '--detach' $checkout $tag
    $worktreeAdded = $true
    $sdkVersion = Get-Dotnet6Sdk
    [ordered]@{ sdk = [ordered]@{ version = $sdkVersion; rollForward = 'latestPatch'; allowPrerelease = $false } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $checkout 'global.json') -Encoding utf8
    $compatibility.Add("Pinned the temporary checkout to installed .NET SDK $sdkVersion; global.json was not committed.")
    Install-RuntimeAssets
    New-Item -ItemType Directory -Path $publish -Force | Out-Null
    Push-Location $checkout
    try {
        Invoke-DotnetQuiet 'restore' 'DownKyi/DownKyi.csproj' '-r' $platformInfo.Rid
        Invoke-DotnetQuiet 'publish' 'DownKyi/DownKyi.csproj' '-c' 'Release' '-r' $platformInfo.Rid '--self-contained' 'true' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' "-p:Version=$Version" '-o' $publish '--no-restore'
    }
    finally { Pop-Location }
    Assert-PublishOutput
    switch ($Platform) {
        'windows-x64' { New-WindowsPackage }
        'macos-x64' { New-MacPackage }
        'linux-x64' { New-LinuxPackage }
    }
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "Package was not created: $packagePath" }
    $packageInfo = Get-Item -LiteralPath $packagePath
    if ($packageInfo.Length -le 0) { throw "Package is empty: $packagePath" }
    $packageSha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$packageSha  $packageName" | Set-Content -LiteralPath "$packagePath.sha256" -Encoding ascii
    $runtimeManifest = Get-Content -LiteralPath (Join-Path $RuntimeAssetsRoot 'runtime-assets.json') -Raw | ConvertFrom-Json
    [ordered]@{
        schemaVersion = 1
        version = $Version
        tag = $tag
        sourceCommit = $sourceCommit
        sourceTree = $sourceTree
        reconstructed = $true
        originalUpstreamBinary = $false
        platform = $Platform
        rid = $platformInfo.Rid
        package = [ordered]@{ name = $packageName; size = $packageInfo.Length; sha256 = $packageSha }
        sdk = [ordered]@{ version = $sdkVersion; targetFramework = 'net6.0' }
        runtimeAssets = $runtimeManifest.assets
        smokeTest = $smoke
        signing = $signing
        compatibilityAdjustments = @($compatibility)
        workflowRun = if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) { "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)" } else { $null }
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$packagePath.manifest.json" -Encoding utf8
    $success = $true
}
catch {
    $failureText = ConvertTo-SafeFailureText -Text ($_ | Out-String) -PathsToRedact @($tempRoot, $RepositoryRoot, $RuntimeAssetsRoot, $OutputRoot)
    Write-Error $failureText -ErrorAction Continue
    Remove-Item -LiteralPath $packagePath, "$packagePath.sha256", "$packagePath.manifest.json" -Force -ErrorAction SilentlyContinue
}
finally {
    if ($worktreeAdded) {
        & dotnet build-server shutdown 2>$null | Out-Null
        & git -C $RepositoryRoot worktree remove --force $checkout 2>$null
        if ($LASTEXITCODE -ne 0) { & git -C $RepositoryRoot worktree prune }
    }
    for ($cleanupAttempt = 1; $cleanupAttempt -le 3 -and (Test-Path -LiteralPath $tempRoot); $cleanupAttempt++) {
        try { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction Stop }
        catch {
            if ($cleanupAttempt -eq 3) {
                Write-Warning "Temporary cleanup did not complete: $(ConvertTo-SafeFailureText -Text $_.Exception.Message -PathsToRedact @($tempRoot))"
            }
            else { Start-Sleep -Seconds 1 }
        }
    }
}

[ordered]@{
    schemaVersion = 1
    version = $Version
    tag = $tag
    sourceCommit = $sourceCommit
    platform = $Platform
    rid = $platformInfo.Rid
    packageName = $packageName
    status = if ($success) { 'success' } else { 'failed' }
    error = $failureText
    recordedAtUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8

if (-not $success) { exit 1 }
Write-Host "Built and verified $packageName from $tag ($sourceCommit)."
