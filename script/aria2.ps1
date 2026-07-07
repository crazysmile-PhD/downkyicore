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
$asset = Get-Asset "aria2" $rid
if ($null -eq $asset) {
    throw "Unsupported aria2 architecture: $arch"
}

$archive = ".\downloads\aria2-$arch.zip"
Start-BitsTransfer -Source $asset.url -Destination $archive
Verify-Asset $archive $asset.sha256

$destDir = "..\DownKyi.Core\Binary\$rid\aria2\"

Expand-Archive -Path $archive -DestinationPath ".\aria2" -Force
Create-Dir $destDir

Copy-Item ".\aria2\aria2c.exe" "$destDir\aria2c.exe" -Force

Remove-Item ".\aria2" -Recurse -Force
