[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Platform,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$ResultsRoot,
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HistoricalRebuild.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-RepositoryRoot }
$ResultsRoot = [IO.Path]::GetFullPath($ResultsRoot)
$versions = @(Get-HistoricalVersions -Selector $Version)
$selectedPlatforms = @(Get-HistoricalPlatforms -Selector $Platform)
$allPlatforms = @(Get-HistoricalPlatforms -Selector 'all')

$resultByKey = @{}
foreach ($resultFile in @(Get-ChildItem -LiteralPath $ResultsRoot -Recurse -Filter 'result-*.json' -File -ErrorAction SilentlyContinue)) {
    try {
        $result = Get-Content -LiteralPath $resultFile.FullName -Raw | ConvertFrom-Json
        $resultByKey["$($result.version)|$($result.platform)"] = [pscustomobject]@{ Data = $result; Directory = $resultFile.DirectoryName }
    }
    catch {
        Write-Warning "Ignoring unreadable result file $($resultFile.FullName): $($_.Exception.Message)"
    }
}

$syncFailures = [Collections.Generic.List[string]]::new()
foreach ($item in $versions) {
    $tag = "archive/v$item"
    $expectedCommit = Assert-HistoricalTag -RepositoryRoot $RepositoryRoot -Version $item
    foreach ($platformItem in $selectedPlatforms) {
        $key = "$item|$($platformItem.Name)"
        if (-not $resultByKey.ContainsKey($key)) { continue }
        $entry = $resultByKey[$key]
        $result = $entry.Data
        if ([string]$result.status -ne 'success') { continue }
        if ([string]$result.sourceCommit -ne $expectedCommit) {
            $syncFailures.Add("$tag / $($platformItem.Name): result source commit does not match the tag")
            continue
        }
        $expectedNames = @(Get-HistoricalAssetNames -Version $item -Platform $platformItem.Name)
        $paths = @($expectedNames | ForEach-Object { Join-Path $entry.Directory $_ })
        if (@($paths | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -gt 0) {
            $syncFailures.Add("$tag / $($platformItem.Name): successful result is missing one or more files")
            continue
        }
        try {
            $packagePath = $paths[0]
            $shaLine = (Get-Content -LiteralPath $paths[1] -Raw).Trim()
            $actualSha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($shaLine -notmatch "^$actualSha\s+$([regex]::Escape($expectedNames[0]))$") {
                throw 'Package SHA-256 sidecar does not match the package.'
            }
            $manifest = Get-Content -LiteralPath $paths[2] -Raw | ConvertFrom-Json
            if ([string]$manifest.version -ne $item -or [string]$manifest.platform -ne $platformItem.Name -or [string]$manifest.sourceCommit -ne $expectedCommit -or [string]$manifest.package.sha256 -ne $actualSha) {
                throw 'Build manifest identity or checksum does not match the expected tag and platform.'
            }
            & gh release upload $tag @paths --repo $Repository --clobber
            if ($LASTEXITCODE -ne 0) { throw 'gh release upload failed.' }
            Write-Host "Uploaded verified asset set: $tag / $($platformItem.Name)"
        }
        catch {
            $safe = ConvertTo-SafeFailureText -Text ($_ | Out-String) -PathsToRedact @($RepositoryRoot, $ResultsRoot)
            $syncFailures.Add("$tag / $($platformItem.Name): $safe")
        }
    }

    $encodedTag = [Uri]::EscapeDataString($tag)
    $releaseJson = & gh api "repos/$Repository/releases/tags/$encodedTag"
    if ($LASTEXITCODE -ne 0) {
        $syncFailures.Add("${tag}: unable to read draft release")
        continue
    }
    $release = $releaseJson | ConvertFrom-Json
    if (-not [bool]$release.draft) { throw "Refusing to alter non-draft release $tag." }
    $assetNames = @($release.assets | ForEach-Object { [string]$_.name })
    $rows = [Collections.Generic.List[string]]::new()
    foreach ($platformItem in $allPlatforms) {
        $expectedNames = @(Get-HistoricalAssetNames -Version $item -Platform $platformItem.Name)
        $isComplete = @($expectedNames | Where-Object { $assetNames -contains $_ }).Count -eq $expectedNames.Count
        $key = "$item|$($platformItem.Name)"
        if ($isComplete) {
            $state = 'Verified asset set uploaded'
        }
        elseif ($resultByKey.ContainsKey($key) -and [string]$resultByKey[$key].Data.status -eq 'failed') {
            $errorText = ConvertTo-SafeFailureText -Text ([string]$resultByKey[$key].Data.error)
            $state = "Build failed — $errorText"
        }
        elseif (@($selectedPlatforms.Name) -contains $platformItem.Name) {
            $state = 'Missing — build or upload did not complete'
        }
        else {
            $state = 'Not selected in this run'
        }
        $rows.Add("| $($platformItem.Name) | $($platformItem.Rid) | $state |")
    }
    $runLink = if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY -and $env:GITHUB_RUN_ID) { "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)" } else { 'local/manual run' }
    $body = @"
## Historical rebuild

These packages were **rebuilt from preserved historical source** at ``$tag`` (commit ``$expectedCommit``). They are not the original binaries published by the upstream author. GitHub provides source archives from the preserved tag automatically.

| Platform | Runtime | Status |
| --- | --- | --- |
$($rows -join "`n")

Every successful package has a ``.sha256`` sidecar and a ``.manifest.json`` recording its source commit, package checksum, SDK, runtime dependencies, compatibility-only adjustments, signing status, and smoke-test result.

macOS packages use ad-hoc signing only. They are not Apple Developer ID signed or notarized, so Gatekeeper may require an explicit user override.

Converter run: $runLink
"@
    $payload = [ordered]@{
        name = "v$item — Historical Rebuild"
        body = $body
        draft = $true
        prerelease = $false
        make_latest = 'false'
    } | ConvertTo-Json -Depth 5
    $payload | & gh api --method PATCH "repos/$Repository/releases/$($release.id)" --input - | Out-Null
    if ($LASTEXITCODE -ne 0) { $syncFailures.Add("${tag}: unable to update draft release notes") }
}

foreach ($item in $versions) {
    $tag = "archive/v$item"
    $encodedTag = [Uri]::EscapeDataString($tag)
    $releaseJson = & gh api "repos/$Repository/releases/tags/$encodedTag"
    if ($LASTEXITCODE -ne 0) { continue }
    $release = $releaseJson | ConvertFrom-Json
    $assetNames = @($release.assets | ForEach-Object { [string]$_.name })
    foreach ($platformItem in $selectedPlatforms) {
        $expectedNames = @(Get-HistoricalAssetNames -Version $item -Platform $platformItem.Name)
        if (@($expectedNames | Where-Object { $assetNames -contains $_ }).Count -ne $expectedNames.Count) {
            $syncFailures.Add("$tag / $($platformItem.Name): selected asset set remains incomplete")
        }
    }
}

if ($syncFailures.Count -gt 0) {
    Write-Error ("Historical rebuild completed with failures:`n- " + (($syncFailures | Select-Object -Unique) -join "`n- "))
    exit 1
}
Write-Host 'All selected historical draft release asset sets are complete.'
