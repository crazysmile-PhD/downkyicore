[CmdletBinding()]
param([string]$RepositoryRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HistoricalRebuild.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-RepositoryRoot }

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$versions = @(Get-HistoricalVersions -Selector 'all')
Assert-True ($versions.Count -eq 25) 'Expected exactly 25 historical versions.'
Assert-True ($versions[0] -eq '1.0.0' -and $versions[-1] -eq '1.0.24') 'Historical version range is incorrect.'
$platforms = @(Get-HistoricalPlatforms -Selector 'all')
Assert-True ($platforms.Count -eq 3) 'Expected exactly three historical platforms.'

$commitCount = 0
foreach ($version in $versions) {
    $commit = Assert-HistoricalTag -RepositoryRoot $RepositoryRoot -Version $version
    Assert-True ($commit -match '^[0-9a-f]{40}$') "Invalid commit for archive/v$version."
    $commitCount++
    foreach ($platform in $platforms) {
        $assets = @(Get-HistoricalAssetNames -Version $version -Platform $platform.Name)
        Assert-True ($assets.Count -eq 3) "Expected a package, checksum, and manifest for $version / $($platform.Name)."
        Assert-True (($assets | Select-Object -Unique).Count -eq 3) "Asset names are not unique for $version / $($platform.Name)."
    }
}
Assert-True ($commitCount -eq 25) 'Not all historical tags were validated.'

$runtimeManifest = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'script/assets/external-assets.json') -Raw | ConvertFrom-Json
foreach ($rid in $platforms.Rid) {
    Assert-True ($null -ne $runtimeManifest.aria2.assets.$rid) "Missing pinned aria2 asset for $rid."
    Assert-True ($null -ne $runtimeManifest.ffmpeg.assets.$rid) "Missing pinned FFmpeg asset for $rid."
    Assert-True (([string]$runtimeManifest.aria2.assets.$rid.sha256) -match '^[0-9a-f]{64}$') "Invalid aria2 checksum for $rid."
    Assert-True (([string]$runtimeManifest.ffmpeg.assets.$rid.sha256) -match '^[0-9a-f]{64}$') "Invalid FFmpeg checksum for $rid."
}

$workflowPath = Join-Path $RepositoryRoot '.github/workflows/historical-rebuild.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
Assert-True ($workflow -match '(?m)^\s*workflow_dispatch:') 'Historical workflow must be manually dispatched.'
Assert-True ($workflow -notmatch '(?m)^\s*(push|release|schedule):') 'Historical workflow must not have automatic release triggers.'
Assert-True ($workflow -match '(?m)^permissions:\s*\r?\n\s+contents: read') 'Workflow default permissions must be read-only.'
Assert-True ($workflow -match 'persist-credentials: false') 'Historical build checkouts must not persist credentials.'
Assert-True ($workflow -notmatch 'MACOS_CERTIFICATE|APPLE_ID|APP_SPECIFIC_PASSWORD|TEAM_ID') 'Historical workflow must not request Apple publishing secrets.'
Assert-True ($workflow -match 'continue-on-error: true') 'Build matrix must continue after individual failures.'

$syncScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Sync-HistoricalDraftReleases.ps1') -Raw
Assert-True ($syncScript -match '(?m)^\s*tag_name\s*=\s*\$tag\s*$') 'Draft updates must preserve the historical tag name.'
Assert-True ($syncScript -match '(?m)^\s*target_commitish\s*=\s*\$expectedCommit\s*$') 'Draft updates must preserve the historical source commit.'

$scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)
foreach ($script in $scripts) {
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    $parseMessage = @($parseErrors | ForEach-Object { $_.Message }) -join '; '
    Assert-True (@($parseErrors).Count -eq 0) "PowerShell parse failure in $($script.Name): $parseMessage"
}

$packageCount = $versions.Count * $platforms.Count
Write-Host "PASS: validated $commitCount tags, $packageCount package identities, pinned runtime entries, workflow boundaries, and $($scripts.Count) PowerShell scripts."
