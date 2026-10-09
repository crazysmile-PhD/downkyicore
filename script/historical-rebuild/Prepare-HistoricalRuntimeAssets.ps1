[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'osx-x64', 'linux-x64')]
    [string]$Rid,

    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HistoricalRebuild.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Get-RepositoryRoot
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($OutputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $RepositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)) {
    throw 'Runtime output must not be the repository root.'
}

$manifestPath = Join-Path $RepositoryRoot 'script/assets/external-assets.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$downloadRoot = Join-Path $OutputRoot '.downloads'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null

function Get-VerifiedDownload {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string]$Destination
    )

    if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Invalid SHA-256 for $Url"
    }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
            $curl = Get-Command curl -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $curl) {
                & $curl.Source '--fail' '--location' '--silent' '--show-error' '--connect-timeout' '30' '--max-time' '1800' '--output' $Destination $Url
                if ($LASTEXITCODE -ne 0) { throw "curl failed with exit code $LASTEXITCODE for $Url" }
            }
            else {
                Invoke-WebRequest -Uri $Url -OutFile $Destination
            }
            $actual = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $Sha256.ToLowerInvariant()) {
                throw "Checksum mismatch for $Url. Expected $Sha256, got $actual."
            }
            return
        }
        catch {
            Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function Expand-VerifiedArchive {
    param(
        [Parameter(Mandatory)][string]$Archive,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    if ($Archive.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
        return
    }
    if ($Archive.EndsWith('.tar.xz', [StringComparison]::OrdinalIgnoreCase)) {
        & tar -xJf $Archive -C $Destination
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $Archive" }
        return
    }
    throw "Unsupported runtime archive: $Archive"
}

function Find-UniqueFile {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Name
    )

    $fileMatches = @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object Name -eq $Name)
    if ($fileMatches.Count -ne 1) {
        throw "Expected exactly one '$Name' below $Root; found $($fileMatches.Count)."
    }
    return $fileMatches[0].FullName
}

$records = [Collections.Generic.List[object]]::new()
$ariaAsset = $manifest.aria2.assets.$Rid
if ($null -eq $ariaAsset) { throw "No pinned aria2 asset for $Rid." }
$ariaArchive = Join-Path $downloadRoot ([IO.Path]::GetFileName(([Uri]$ariaAsset.url).AbsolutePath))
Get-VerifiedDownload -Url $ariaAsset.url -Sha256 $ariaAsset.sha256 -Destination $ariaArchive
$ariaExtract = Join-Path $downloadRoot 'aria2-extracted'
Expand-VerifiedArchive -Archive $ariaArchive -Destination $ariaExtract
$ariaName = if ($Rid -eq 'win-x64') { 'aria2c.exe' } else { 'aria2c' }
$ariaDestination = Join-Path $OutputRoot "aria2/$ariaName"
New-Item -ItemType Directory -Path (Split-Path -Parent $ariaDestination) -Force | Out-Null
Copy-Item -LiteralPath (Find-UniqueFile -Root $ariaExtract -Name $ariaName) -Destination $ariaDestination -Force
$ariaHash = (Get-FileHash -LiteralPath $ariaDestination -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ariaHash -ne ([string]$ariaAsset.binarySha256).ToLowerInvariant()) {
    throw "aria2 binary checksum mismatch for $Rid."
}
$records.Add([ordered]@{ name = 'aria2'; url = $ariaAsset.url; archiveSha256 = $ariaAsset.sha256; binarySha256 = $ariaHash })

$ffmpegAsset = $manifest.ffmpeg.assets.$Rid
if ($null -eq $ffmpegAsset) { throw "No pinned FFmpeg asset for $Rid." }
$ffmpegArchive = Join-Path $downloadRoot ([IO.Path]::GetFileName(([Uri]$ffmpegAsset.url).AbsolutePath))
Get-VerifiedDownload -Url $ffmpegAsset.url -Sha256 $ffmpegAsset.sha256 -Destination $ffmpegArchive
$ffmpegExtract = Join-Path $downloadRoot 'ffmpeg-extracted'
Expand-VerifiedArchive -Archive $ffmpegArchive -Destination $ffmpegExtract
$ffmpegName = if ($Rid -eq 'win-x64') { 'ffmpeg.exe' } else { 'ffmpeg' }
$ffprobeName = if ($Rid -eq 'win-x64') { 'ffprobe.exe' } else { 'ffprobe' }
$ffmpegDestination = Join-Path $OutputRoot "ffmpeg/$ffmpegName"
$ffprobeDestination = Join-Path $OutputRoot "ffmpeg/$ffprobeName"
New-Item -ItemType Directory -Path (Split-Path -Parent $ffmpegDestination) -Force | Out-Null
Copy-Item -LiteralPath (Find-UniqueFile -Root $ffmpegExtract -Name $ffmpegName) -Destination $ffmpegDestination -Force

if ($Rid -eq 'osx-x64') {
    $ffprobeArchive = Join-Path $downloadRoot ([IO.Path]::GetFileName(([Uri]$ffmpegAsset.ffprobeUrl).AbsolutePath))
    Get-VerifiedDownload -Url $ffmpegAsset.ffprobeUrl -Sha256 $ffmpegAsset.ffprobeSha256 -Destination $ffprobeArchive
    $ffprobeExtract = Join-Path $downloadRoot 'ffprobe-extracted'
    Expand-VerifiedArchive -Archive $ffprobeArchive -Destination $ffprobeExtract
    Copy-Item -LiteralPath (Find-UniqueFile -Root $ffprobeExtract -Name $ffprobeName) -Destination $ffprobeDestination -Force
}
else {
    Copy-Item -LiteralPath (Find-UniqueFile -Root $ffmpegExtract -Name $ffprobeName) -Destination $ffprobeDestination -Force
}
$records.Add([ordered]@{ name = 'ffmpeg'; url = $ffmpegAsset.url; archiveSha256 = $ffmpegAsset.sha256; binarySha256 = (Get-FileHash -LiteralPath $ffmpegDestination -Algorithm SHA256).Hash.ToLowerInvariant() })
$ffprobeUrl = if ($Rid -eq 'osx-x64') { $ffmpegAsset.ffprobeUrl } else { $ffmpegAsset.url }
$ffprobeArchiveSha = if ($Rid -eq 'osx-x64') { $ffmpegAsset.ffprobeSha256 } else { $ffmpegAsset.sha256 }
$records.Add([ordered]@{
    name = 'ffprobe'
    url = $ffprobeUrl
    archiveSha256 = $ffprobeArchiveSha
    binarySha256 = (Get-FileHash -LiteralPath $ffprobeDestination -Algorithm SHA256).Hash.ToLowerInvariant()
})

if ($Rid -eq 'linux-x64') {
    & (Join-Path $RepositoryRoot 'script/install-appimagetool.ps1') -ToolPath (Join-Path $OutputRoot 'appimagetool') -ManifestPath $manifestPath
    if ($LASTEXITCODE -ne 0) { throw 'Pinned appimagetool installation failed.' }
    $toolAsset = $manifest.appimagetool.assets.'linux-x64'
    $records.Add([ordered]@{ name = 'appimagetool'; url = $toolAsset.url; archiveSha256 = $toolAsset.sha256; binarySha256 = $toolAsset.sha256 })
}

if ($Rid -ne 'win-x64') {
    & chmod +x -- $ariaDestination $ffmpegDestination $ffprobeDestination
    if ($LASTEXITCODE -ne 0) { throw 'Failed to mark runtime binaries executable.' }
}

[ordered]@{
    schemaVersion = 1
    rid = $Rid
    preparedAtUtc = [DateTime]::UtcNow.ToString('o')
    sourceManifest = 'script/assets/external-assets.json'
    assets = $records
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputRoot 'runtime-assets.json') -Encoding utf8

Remove-Item -LiteralPath $downloadRoot -Recurse -Force
Write-Host "Prepared verified runtime assets for $Rid in $OutputRoot"
