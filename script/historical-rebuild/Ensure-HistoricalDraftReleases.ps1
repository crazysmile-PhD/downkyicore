[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Repository,
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HistoricalRebuild.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Get-RepositoryRoot }

$releasePages = & gh api --paginate --slurp "repos/$Repository/releases?per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Unable to list repository releases.' }
$releases = @($releasePages | ConvertFrom-Json | ForEach-Object { $_ })
$releaseByTag = @{}
foreach ($release in $releases) { $releaseByTag[[string]$release.tag_name] = $release }

foreach ($item in @(Get-HistoricalVersions -Selector $Version)) {
    $commit = Assert-HistoricalTag -RepositoryRoot $RepositoryRoot -Version $item
    $tag = "archive/v$item"
    $releaseName = "v$item — Historical Rebuild"
    if (-not $releaseByTag.ContainsKey($tag)) {
        $detachedDrafts = @($releases | Where-Object {
            [bool]$_.draft -and
            [string]$_.name -eq $releaseName -and
            [string]$_.tag_name -like 'untagged-*'
        })
        if ($detachedDrafts.Count -gt 1) {
            throw "Multiple detached draft releases match $releaseName. Refusing an ambiguous repair."
        }
        if ($detachedDrafts.Count -eq 1) {
            $repairPayload = [ordered]@{
                tag_name = $tag
                target_commitish = $commit
                draft = $true
                prerelease = $false
                make_latest = 'false'
            } | ConvertTo-Json -Depth 5
            $repairJson = $repairPayload | & gh api --method PATCH "repos/$Repository/releases/$($detachedDrafts[0].id)" --input -
            if ($LASTEXITCODE -ne 0) { throw "Unable to restore the historical tag association for $tag." }
            $releaseByTag[$tag] = $repairJson | ConvertFrom-Json
            Write-Host "Restored draft release tag association: $tag"
        }
    }
    if ($releaseByTag.ContainsKey($tag)) {
        if (-not [bool]$releaseByTag[$tag].draft) {
            throw "A non-draft release already uses $tag. Refusing to alter it."
        }
        Write-Host "Draft release already exists: $tag"
        continue
    }

    $body = @"
## Historical rebuild

This draft release contains packages rebuilt from the preserved historical source tag ``$tag`` (commit ``$commit``). It is **not** an original package published by the upstream author at that time.

Build results are pending. The historical converter will update this draft with per-platform verification status. GitHub provides the source archives from the tag automatically.
"@
    $payload = [ordered]@{
        tag_name = $tag
        # GitHub validates target_commitish even when tag_name already exists;
        # an archive/* tag name is not accepted here, while its peeled commit is.
        target_commitish = $commit
        name = $releaseName
        body = $body
        draft = $true
        prerelease = $false
        make_latest = 'false'
    } | ConvertTo-Json -Depth 5
    $payload | & gh api --method POST "repos/$Repository/releases" --input - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to create draft release for $tag." }
    Write-Host "Created draft release: $tag"
}
