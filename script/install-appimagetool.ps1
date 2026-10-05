[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ToolPath,

    [string]$ManifestPath = (Join-Path $PSScriptRoot "assets/external-assets.json")
)

$ErrorActionPreference = "Stop"

if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Linux)) {
    throw "appimagetool is only installed by this script on Linux."
}

if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw "The pinned appimagetool package requires an x64 Linux build host."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$appImageTool = $manifest.appimagetool
if ($null -eq $appImageTool -or $null -eq $appImageTool.assets.'linux-x64') {
    throw "The external asset manifest does not define appimagetool.assets.linux-x64."
}

$asset = $appImageTool.assets.'linux-x64'
$url = [string]$asset.url
$sha256 = ([string]$asset.sha256).ToLowerInvariant()
$fileName = [string]$asset.fileName
$expectedSize = [long]$asset.size

$mirrorPrefix = "https://github.com/crazysmile-PhD/downkyi-runtime-assets/releases/download/appimagetool-"
if (-not $url.StartsWith($mirrorPrefix, [StringComparison]::Ordinal)) {
    throw "appimagetool must come from the fixed DownKyi runtime-assets mirror."
}
if ($fileName -ne "appimagetool-x86_64.AppImage") {
    throw "Unexpected appimagetool file name: $fileName"
}
if ([IO.Path]::GetFileName(([Uri]$url).AbsolutePath) -ne $fileName) {
    throw "appimagetool fileName does not match its URL."
}
if ($sha256 -notmatch '^[0-9a-f]{64}$') {
    throw "appimagetool has an invalid SHA-256 value."
}
if ($expectedSize -le 0) {
    throw "appimagetool has an invalid expected size."
}

New-Item -ItemType Directory -Path $ToolPath -Force | Out-Null
$destination = Join-Path $ToolPath $fileName

for ($attempt = 1; $attempt -le 3; $attempt++) {
    try {
        if (Test-Path -LiteralPath $destination) {
            Remove-Item -LiteralPath $destination -Force
        }

        Invoke-WebRequest -Uri $url -OutFile $destination
        break
    }
    catch {
        if ($attempt -eq 3) {
            throw
        }
        Start-Sleep -Seconds 2
    }
}

$download = Get-Item -LiteralPath $destination -ErrorAction Stop
if ($download.Length -ne $expectedSize) {
    throw "appimagetool size mismatch. Expected $expectedSize bytes, got $($download.Length)."
}

$actualSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not [String]::Equals($actualSha256, $sha256, [StringComparison]::Ordinal)) {
    throw "appimagetool checksum mismatch. Expected $sha256, got $actualSha256."
}

& chmod +x -- $destination
if ($LASTEXITCODE -ne 0) {
    throw "Failed to mark appimagetool executable."
}

Write-Host "Installed verified appimagetool at $destination"
