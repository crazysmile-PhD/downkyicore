[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Platform,
    [Parameter(Mandatory)][string]$Repository,
    [bool]$RebuildExisting = $false,
    [string]$RepositoryRoot,
    [string]$GitHubOutputPath,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HistoricalRebuild.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-RepositoryRoot }

$versions = @(Get-HistoricalVersions -Selector $Version)
$platforms = @(Get-HistoricalPlatforms -Selector $Platform)
foreach ($item in $versions) { [void](Assert-HistoricalTag -RepositoryRoot $RepositoryRoot -Version $item) }

$releasePages = & gh api --paginate --slurp "repos/$Repository/releases?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Unable to list repository releases.' }
$releases = @($releasePages | ConvertFrom-Json | ForEach-Object { $_ })
$releaseByTag = @{}
foreach ($release in $releases) { $releaseByTag[[string]$release.tag_name] = $release }

$include = [Collections.Generic.List[object]]::new()
$runtimeByName = [ordered]@{}
foreach ($item in $versions) {
    $tag = "archive/v$item"
    if (-not $releaseByTag.ContainsKey($tag)) { throw "Draft release is missing for $tag." }
    if (-not [bool]$releaseByTag[$tag].draft) { throw "Refusing to use a published release for $tag." }
    $existingNames = @($releaseByTag[$tag].assets | ForEach-Object { [string]$_.name })
    foreach ($platformItem in $platforms) {
        $requiredNames = @(Get-HistoricalAssetNames -Version $item -Platform $platformItem.Name)
        $complete = @($requiredNames | Where-Object { $existingNames -contains $_ }).Count -eq $requiredNames.Count
        if ($complete -and -not $RebuildExisting) {
            Write-Host "Skipping complete asset set: $tag / $($platformItem.Name)"
            continue
        }
        $include.Add([ordered]@{
            version = $item
            platform = $platformItem.Name
            rid = $platformItem.Rid
            runner = $platformItem.Runner
            package_name = Get-HistoricalPackageName -Version $item -Platform $platformItem.Name
        })
        $runtimeByName[$platformItem.Name] = [ordered]@{
            platform = $platformItem.Name
            rid = $platformItem.Rid
            runner = $platformItem.Runner
        }
    }
}

$plan = [ordered]@{
    versionSelector = $Version
    platformSelector = $Platform
    rebuildExisting = $RebuildExisting
    buildMatrix = [ordered]@{ include = @($include) }
    runtimeMatrix = [ordered]@{ include = @($runtimeByName.Values) }
    buildCount = $include.Count
}
$planJson = $plan | ConvertTo-Json -Depth 10
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-Content -LiteralPath $OutputPath -Value $planJson -Encoding utf8
}
if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    Write-GitHubOutputValue -Path $GitHubOutputPath -Name 'matrix' -Value (($plan.buildMatrix | ConvertTo-Json -Depth 5 -Compress))
    Write-GitHubOutputValue -Path $GitHubOutputPath -Name 'runtime_matrix' -Value (($plan.runtimeMatrix | ConvertTo-Json -Depth 5 -Compress))
    Write-GitHubOutputValue -Path $GitHubOutputPath -Name 'has_work' -Value ($include.Count -gt 0).ToString().ToLowerInvariant()
}
$planJson
