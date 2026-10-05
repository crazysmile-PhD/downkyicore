[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ToolPath,

    [string]$RuntimePath,

    [string]$ManifestPath = (Join-Path $PSScriptRoot "assets/external-assets.json")
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "download-external-asset.ps1")

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux)) {
    throw "AppImage packaging assets are only installed by this script on Linux."
}

if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw "The pinned appimagetool package requires an x64 Linux build host."
}

$toolDirectory = [IO.Path]::GetFullPath($ToolPath)
if ([string]::IsNullOrWhiteSpace($RuntimePath)) {
    $runtimeDirectory = Join-Path $toolDirectory "appimage-runtimes"
}
else {
    $runtimeDirectory = [IO.Path]::GetFullPath($RuntimePath)
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$appImageTool = $manifest.appimagetool
$appImageRuntime = $manifest.appimageRuntime
if ($null -eq $appImageTool) {
    throw "The external asset manifest does not define appimagetool."
}
if ($null -eq $appImageRuntime) {
    throw "The external asset manifest does not define appimageRuntime."
}

function Get-ValidatedAsset {
    param(
        [Parameter(Mandatory)]
        [object]$Group,

        [Parameter(Mandatory)]
        [string]$Rid,

        [Parameter(Mandatory)]
        [string]$ExpectedFileName
    )

    $version = [string]$Group.version
    if ($version -notmatch '^[0-9a-f]{40}$') {
        throw "External asset group has an invalid source commit: $version"
    }

    $assetProperty = $Group.assets.PSObject.Properties[$Rid]
    if ($null -eq $assetProperty) {
        throw "External asset group does not define assets.$Rid."
    }

    $asset = $assetProperty.Value
    $url = [string]$asset.url
    $sha256 = ([string]$asset.sha256).ToLowerInvariant()
    $fileName = [string]$asset.fileName
    $expectedSize = [long]$asset.size
    $mirrorRepository = [string]$Group.mirror.repository
    $tagPrefix = [string]$Group.mirror.tagPrefix
    $expectedRelease = "$tagPrefix$version"
    $mirrorPrefix = "https://github.com/$mirrorRepository/releases/download/$expectedRelease/"

    if ($url -notlike "$mirrorPrefix*") {
        throw "$ExpectedFileName must come from the immutable DownKyi runtime-assets release $expectedRelease."
    }
    if ($fileName -ne $ExpectedFileName) {
        throw "Unexpected external asset file name: $fileName"
    }
    if ([IO.Path]::GetFileName(([Uri]$url).AbsolutePath) -ne $fileName) {
        throw "External asset fileName does not match its URL: $fileName"
    }
    if ($sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "$fileName has an invalid SHA-256 value."
    }
    if ($expectedSize -le 0) {
        throw "$fileName has an invalid expected size."
    }
    if ([string]$asset.provenance.upstreamRepository -ne [string]$Group.repository -or
        [string]$asset.provenance.upstreamCommit -ne $version -or
        [string]$asset.provenance.originalAssetName -ne $fileName -or
        [string]$asset.provenance.mirroredRelease -ne $expectedRelease) {
        throw "$fileName provenance does not match its authoritative asset identity."
    }

    return $asset
}

$toolAsset = Get-ValidatedAsset `
    -Group $appImageTool `
    -Rid "linux-x64" `
    -ExpectedFileName "appimagetool-x86_64.AppImage"
$runtimeX64Asset = Get-ValidatedAsset `
    -Group $appImageRuntime `
    -Rid "linux-x64" `
    -ExpectedFileName "runtime-x86_64"
$runtimeArm64Asset = Get-ValidatedAsset `
    -Group $appImageRuntime `
    -Rid "linux-arm64" `
    -ExpectedFileName "runtime-aarch64"

$toolParent = [IO.Path]::GetDirectoryName($toolDirectory)
if ([string]::IsNullOrWhiteSpace($toolParent)) {
    throw "AppImage tool path has no parent directory: $toolDirectory"
}
$stagingDirectory = Join-Path $toolParent (".downkyi-appimage-assets-" + [Guid]::NewGuid().ToString("N"))
$transferOperation = {
    param([Uri]$Source, [string]$Destination)

    Invoke-WebRequest -Uri $Source.AbsoluteUri -OutFile $Destination
}

try {
    $toolDestination = Join-Path $toolDirectory ([string]$toolAsset.fileName)
    Install-VerifiedExternalAsset `
        -Uri ([Uri][string]$toolAsset.url) `
        -Destination $toolDestination `
        -StagingDirectory $stagingDirectory `
        -Sha256 ([string]$toolAsset.sha256) `
        -ExpectedSize ([long]$toolAsset.size) `
        -TransferOperation $transferOperation

    foreach ($runtimeAsset in @($runtimeX64Asset, $runtimeArm64Asset)) {
        Install-VerifiedExternalAsset `
            -Uri ([Uri][string]$runtimeAsset.url) `
            -Destination (Join-Path $runtimeDirectory ([string]$runtimeAsset.fileName)) `
            -StagingDirectory $stagingDirectory `
            -Sha256 ([string]$runtimeAsset.sha256) `
            -ExpectedSize ([long]$runtimeAsset.size) `
            -TransferOperation $transferOperation
    }

    & chmod +x -- $toolDestination
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $toolDestination -Force
        throw "Failed to mark appimagetool executable."
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Force
    }
}

Write-Host "Installed verified AppImage packaging assets at $toolDirectory"
