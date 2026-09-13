param($arch)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "download-external-asset.ps1")

function Create-Dir($dir) {
    if (!(Test-Path -Path $dir)) {
        New-Item $dir -ItemType "directory" | Out-Null
    }
}

function Get-Asset($tool, $rid) {
    $manifestPath = Join-Path $PSScriptRoot "assets\external-assets.json"
    $manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
    return $manifest.$tool.assets.$rid
}

function Verify-Asset($path, $expectedSha256) {
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expectedSha256) {
        throw "Checksum mismatch for $path. Expected $expectedSha256, got $actual."
    }
}

$downloadDir = Join-Path $PSScriptRoot "downloads"
$binaryRoot = Join-Path $PSScriptRoot "..\DownKyi.Core\Binary"
Create-Dir $downloadDir

$rid = "win-$arch"
$asset = Get-Asset "aria2" $rid
if ($null -eq $asset) {
    throw "Unsupported aria2 architecture: $arch"
}

$archive = Join-Path $downloadDir "aria2-$arch.zip"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Invoke-ExternalAssetDownload -Uri $asset.url -Destination $archive
Verify-Asset $archive $asset.sha256

$destDir = Join-Path $binaryRoot "$rid\aria2"
$extractDir = Join-Path $downloadDir "aria2-$arch-extract"

if (Test-Path -LiteralPath $extractDir) {
    Remove-Item -LiteralPath $extractDir -Recurse -Force
}
Expand-Archive -Path $archive -DestinationPath $extractDir -Force
Create-Dir $destDir

$aria2 = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter "aria2c.exe" |
    Select-Object -First 1
if ($null -eq $aria2) {
    throw "aria2c.exe not found in $archive"
}
Copy-Item -LiteralPath $aria2.FullName -Destination (Join-Path $destDir "aria2c.exe") -Force
$installedBinary = Join-Path $destDir "aria2c.exe"
$installedChecksum = Join-Path $destDir "aria2c.exe.sha256"
Verify-Asset $installedBinary $asset.binarySha256
Set-Content `
    -LiteralPath $installedChecksum `
    -Value $asset.binarySha256 `
    -Encoding ascii `
    -NoNewline

Remove-Item -LiteralPath $extractDir -Recurse -Force
