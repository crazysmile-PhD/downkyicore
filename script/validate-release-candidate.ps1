[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CandidateDirectory,
    [Parameter(Mandatory = $true)][string]$ExpectedCandidateSha,
    [Parameter(Mandatory = $true)][long]$ExpectedPrepareRunId,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$GitHubOutputPath
)

$ErrorActionPreference = 'Stop'
$candidateRoot = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifestPath = Join-Path $candidateRoot 'candidate.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported release candidate manifest schema: $($manifest.schemaVersion)"
}
if (-not [string]::Equals($manifest.candidateSha, $ExpectedCandidateSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The sealed candidate SHA does not match the Release PR head. Run Prepare Release again.'
}
if ([long]$manifest.prepareRunId -ne $ExpectedPrepareRunId) {
    throw 'The sealed candidate does not belong to the Prepare Release run recorded by the Release PR.'
}
if ($manifest.version -notmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$' -or
    $manifest.tag -ne "v$($manifest.version)") {
    throw 'The sealed candidate contains an invalid version identity.'
}
foreach ($sha in @($manifest.sourceSha, $manifest.candidateSha)) {
    if ($sha -notmatch '^[0-9a-f]{40}$') {
        throw 'The sealed candidate contains an invalid commit identity.'
    }
}

$version = (Get-Content -LiteralPath (Join-Path $repository 'version.txt') -Raw).Trim()
if ($version -ne $manifest.version) {
    throw 'version.txt no longer matches the sealed candidate. Run Prepare Release again.'
}

$parent = (& git -C $repository rev-parse "$ExpectedCandidateSha^").Trim()
if ($LASTEXITCODE -ne 0 -or $parent -ne $manifest.sourceSha) {
    throw 'The candidate parent is not the source commit recorded by Prepare Release.'
}
$metadataChanges = @(& git -C $repository diff --name-only $manifest.sourceSha $ExpectedCandidateSha)
if ($LASTEXITCODE -ne 0 -or
    [string]::Join("`n", $metadataChanges) -ne "CHANGELOG.md`nversion.txt") {
    throw "The candidate commit must change only CHANGELOG.md and version.txt; found: $($metadataChanges -join ', ')"
}

$expectedFileNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($asset in @($manifest.assets)) {
    if (-not $expectedFileNames.Add([string]$asset.name)) {
        throw "Duplicate candidate asset in manifest: $($asset.name)"
    }
    $path = Join-Path $candidateRoot ([string]$asset.name)
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne [long]$asset.size) {
        throw "Candidate asset size changed: $($asset.name)"
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not [string]::Equals($hash, [string]$asset.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Candidate asset hash changed: $($asset.name)"
    }
}
if ($expectedFileNames.Count -ne 27) {
    throw "Sealed candidate must contain exactly 27 public files, found $($expectedFileNames.Count)."
}

$notesPath = Join-Path $candidateRoot ([string]$manifest.releaseNotes.name)
$notesHash = (Get-FileHash -LiteralPath $notesPath -Algorithm SHA256).Hash
if (-not [string]::Equals($notesHash, [string]$manifest.releaseNotes.sha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release notes no longer match the sealed candidate manifest.'
}
$actualFileNames = @(
    Get-ChildItem -LiteralPath $candidateRoot -File |
        ForEach-Object Name |
        Sort-Object
)
$manifestFileNames = @($expectedFileNames | Sort-Object) + @('candidate.json', [string]$manifest.releaseNotes.name) | Sort-Object
if ([string]::Join("`n", $actualFileNames) -ne [string]::Join("`n", $manifestFileNames)) {
    throw 'The sealed candidate directory contains files outside its manifest.'
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    @(
        "version=$($manifest.version)"
        "tag=$($manifest.tag)"
        "source_sha=$($manifest.sourceSha)"
        "candidate_sha=$($manifest.candidateSha)"
    ) | Add-Content -LiteralPath $GitHubOutputPath -Encoding utf8
}

Write-Output "Validated sealed release candidate $($manifest.version) at $($manifest.candidateSha)."
