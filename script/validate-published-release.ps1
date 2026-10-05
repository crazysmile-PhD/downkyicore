[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsDirectory,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedTag,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$ReleaseJsonPath,
    [string]$LatestReleaseJsonPath
)

$ErrorActionPreference = 'Stop'
$artifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Invoke-GitHubApi {
    param([Parameter(Mandatory = $true)][string]$Endpoint)

    $output = & gh api $Endpoint 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "gh api $Endpoint failed: $($output -join [Environment]::NewLine)"
    }

    return ($output -join [Environment]::NewLine) | ConvertFrom-Json
}

$hasFixturePaths = -not [string]::IsNullOrWhiteSpace($ReleaseJsonPath) -or
    -not [string]::IsNullOrWhiteSpace($LatestReleaseJsonPath)
if ($hasFixturePaths) {
    if ([string]::IsNullOrWhiteSpace($ReleaseJsonPath) -or
        [string]::IsNullOrWhiteSpace($LatestReleaseJsonPath)) {
        throw 'ReleaseJsonPath and LatestReleaseJsonPath must be provided together.'
    }

    $release = Read-JsonFile -Path $ReleaseJsonPath
    $latest = Read-JsonFile -Path $LatestReleaseJsonPath
}
else {
    if ([string]::IsNullOrWhiteSpace($Repository)) {
        throw 'Repository is required when release fixture paths are not provided.'
    }

    $release = Invoke-GitHubApi -Endpoint "repos/$Repository/releases/tags/$ExpectedTag"
    $latest = Invoke-GitHubApi -Endpoint "repos/$Repository/releases/latest"
}

if (-not [string]::Equals($release.tag_name, $ExpectedTag, [StringComparison]::Ordinal)) {
    throw "Published release tag '$($release.tag_name)' does not match '$ExpectedTag'."
}

if ($release.draft -ne $false -or $release.prerelease -ne $false) {
    throw "Release '$ExpectedTag' must be stable, published, and non-draft."
}

if ($null -eq $release.id -or $null -eq $latest.id -or $release.id -ne $latest.id) {
    throw "Release '$ExpectedTag' is not the repository's Latest release."
}

$expectedFiles = @(Get-ChildItem -LiteralPath $artifacts -File | Sort-Object Name)
if ($expectedFiles.Count -eq 0) {
    throw "No release artifacts were found in '$artifacts'."
}

$actualAssets = @($release.assets)
$duplicateNames = @($actualAssets | Group-Object name | Where-Object Count -ne 1)
if ($duplicateNames.Count -ne 0) {
    throw "Release '$ExpectedTag' contains duplicate asset names: $($duplicateNames.Name -join ', ')."
}

$expectedNames = @($expectedFiles.Name | Sort-Object)
$actualNames = @($actualAssets.name | Sort-Object)
$nameDifference = @(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames)
if ($nameDifference.Count -ne 0) {
    throw "Release '$ExpectedTag' assets do not exactly match the validated publication input: $($nameDifference.InputObject -join ', ')."
}

$filesByName = @{}
foreach ($file in $expectedFiles) {
    $filesByName[$file.Name] = $file
}

foreach ($asset in $actualAssets) {
    if ($asset.name -like '*.internal.*') {
        throw "Internal transport reached the public release: $($asset.name)."
    }

    if ($asset.state -ne 'uploaded') {
        throw "Release asset '$($asset.name)' is not uploaded."
    }

    if ([string]::IsNullOrWhiteSpace($asset.browser_download_url)) {
        throw "Release asset '$($asset.name)' has no public download URL."
    }

    $expectedLength = [long]$filesByName[$asset.name].Length
    if ([long]$asset.size -ne $expectedLength -or $expectedLength -le 0) {
        throw "Release asset '$($asset.name)' size $($asset.size) does not match validated input size $expectedLength."
    }
}

Write-Output "Validated Latest release $ExpectedTag with $($actualAssets.Count) public assets."
