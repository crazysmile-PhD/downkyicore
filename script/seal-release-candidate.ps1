[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$ReleaseNotesPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SourceSha,
    [Parameter(Mandatory = $true)][string]$CandidateSha,
    [Parameter(Mandatory = $true)][long]$PrepareRunId
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
    throw 'Candidate version must be a stable semantic version.'
}
foreach ($sha in @($SourceSha, $CandidateSha)) {
    if ($sha -notmatch '^[0-9a-f]{40}$') {
        throw "Candidate identity contains an invalid commit SHA: $sha"
    }
}
if ($PrepareRunId -le 0) {
    throw 'PrepareRunId must be positive.'
}

$inputRoot = (Resolve-Path -LiteralPath $InputDirectory).Path
$notes = (Resolve-Path -LiteralPath $ReleaseNotesPath).Path
if (Test-Path -LiteralPath $OutputDirectory) {
    $existing = @(Get-ChildItem -LiteralPath $OutputDirectory -Force)
    if ($existing.Count -ne 0) {
        throw "Candidate output directory must be empty: $OutputDirectory"
    }
}
else {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory).Path

$appImageTransports = @(Get-ChildItem -LiteralPath $inputRoot -Recurse -File -Filter 'appimage-*.transport.tar')
if ($appImageTransports.Count -ne 2) {
    throw "Expected two validated AppImage transports, found $($appImageTransports.Count)."
}
foreach ($transport in $appImageTransports) {
    & tar -xf $transport.FullName -C $inputRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restore validated AppImage transport $($transport.Name)."
    }
}

$allFiles = @(Get-ChildItem -LiteralPath $inputRoot -Recurse -File)
$packages = @(
    $allFiles | Where-Object {
        $_.Name -match '^(?:DownKyi-|downkyi_).+\.(?:zip|AppImage|deb|rpm|dmg)$'
    }
)
$sidecars = @($allFiles | Where-Object { $_.Name -match '\.(?:zip|AppImage|deb|rpm|dmg)\.sha256$' })
$publishManifests = @($allFiles | Where-Object { $_.Name -match '^publish-manifest-.+\.json$' })

if ($packages.Count -ne 9 -or $sidecars.Count -ne 9 -or $publishManifests.Count -ne 9) {
    throw "Candidate asset topology must contain 9 packages, 9 checksum sidecars, and 9 publish manifests; found $($packages.Count), $($sidecars.Count), and $($publishManifests.Count)."
}

$selectedFiles = @($packages + $sidecars + $publishManifests)
$duplicateNames = @(
    $selectedFiles |
        Group-Object Name |
        Where-Object Count -ne 1 |
        ForEach-Object Name
)
if ($duplicateNames.Count -ne 0) {
    throw "Candidate asset names are not unique: $($duplicateNames -join ', ')"
}

foreach ($package in $packages) {
    $sidecar = $sidecars | Where-Object Name -EQ "$($package.Name).sha256"
    if (@($sidecar).Count -ne 1) {
        throw "Package $($package.Name) does not have exactly one checksum sidecar."
    }
    $expectedHash = ((Get-Content -LiteralPath $sidecar.FullName -Raw).Trim() -split '\s+')[0]
    if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Malformed checksum sidecar for $($package.Name)."
    }
    $actualHash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package $($package.Name) does not match its validated checksum."
    }
}

foreach ($file in $selectedFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $outputRoot $file.Name)
}
Copy-Item -LiteralPath $notes -Destination (Join-Path $outputRoot 'release-notes.md')

$assets = @(
    Get-ChildItem -LiteralPath $outputRoot -File |
        Where-Object Name -NE 'release-notes.md' |
        Sort-Object Name |
        ForEach-Object {
            [ordered]@{
                name = $_.Name
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
$releaseNotesHash = (Get-FileHash -LiteralPath (Join-Path $outputRoot 'release-notes.md') -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    tag = "v$Version"
    sourceSha = $SourceSha
    candidateSha = $CandidateSha
    prepareRunId = $PrepareRunId
    releaseNotes = [ordered]@{
        name = 'release-notes.md'
        sha256 = $releaseNotesHash
    }
    assets = $assets
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot 'candidate.json') -Encoding utf8

Write-Output "Sealed release candidate $Version at $CandidateSha with $($assets.Count) public assets."
