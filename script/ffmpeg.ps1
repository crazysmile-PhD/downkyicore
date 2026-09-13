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
    $verifier = Join-Path $PSScriptRoot "ffmpeg-assets.py"
    & python $verifier verify-file --path $path --sha256 $expectedSha256
    if ($LASTEXITCODE -ne 0) {
        throw "Checksum verification failed for $path."
    }
}

$downloadDir = Join-Path $PSScriptRoot "downloads"
$binaryRoot = Join-Path $PSScriptRoot "..\DownKyi.Core\Binary"
Create-Dir $downloadDir

$rid = "win-$arch"
$asset = Get-Asset "ffmpeg" $rid
if ($null -eq $asset) {
    throw "Unsupported ffmpeg architecture: $arch"
}

$archive = Join-Path $downloadDir "ffmpeg-$arch.zip"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Invoke-ExternalAssetDownload -Uri $asset.url -Destination $archive
Verify-Asset $archive $asset.sha256

$destDir = Join-Path $binaryRoot "$rid\ffmpeg"
Create-Dir $destDir
Get-ChildItem -LiteralPath $destDir -File | Remove-Item -Force

$extractDir = Join-Path $downloadDir "ffmpeg-$arch-extract"
if (Test-Path -LiteralPath $extractDir) {
    Remove-Item -LiteralPath $extractDir -Recurse -Force
}
Create-Dir $extractDir

Expand-Archive -Path $archive -DestinationPath $extractDir -Force
$ffmpeg = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter "ffmpeg.exe" | Select-Object -First 1
if ($null -eq $ffmpeg) {
    throw "ffmpeg.exe not found in $archive"
}
$ffprobe = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter "ffprobe.exe" | Select-Object -First 1
if ($null -eq $ffprobe) {
    throw "ffprobe.exe not found in $archive"
}

Copy-Item -LiteralPath $ffmpeg.FullName -Destination (Join-Path $destDir "ffmpeg.exe") -Force
Copy-Item -LiteralPath $ffprobe.FullName -Destination (Join-Path $destDir "ffprobe.exe") -Force

$extractRoot = (Resolve-Path -LiteralPath $extractDir).Path
$current = $ffmpeg.Directory
while ($null -ne $current -and $current.FullName.StartsWith($extractRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Get-ChildItem -LiteralPath $current.FullName -File |
        Where-Object { $_.Name -match '^(LICENSE|COPYING|README)(\..*)?$' } |
        ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destDir $_.Name) -Force
        }
    $current = $current.Parent
}
