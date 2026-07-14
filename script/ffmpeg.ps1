param($arch)

$ErrorActionPreference = "Stop"

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

Create-Dir ".\downloads"

$rid = "win-$arch"
$asset = Get-Asset "ffmpeg" $rid
if ($null -eq $asset) {
    throw "Unsupported ffmpeg architecture: $arch"
}

$archive = ".\downloads\ffmpeg-$arch.zip"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Start-BitsTransfer -Source $asset.url -Destination $archive
Verify-Asset $archive $asset.sha256

$destDir = "..\DownKyi.Core\Binary\$rid\ffmpeg\"
Create-Dir $destDir
Get-ChildItem -LiteralPath $destDir -File | Remove-Item -Force

$extractDir = ".\downloads\ffmpeg-$arch-extract"
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
