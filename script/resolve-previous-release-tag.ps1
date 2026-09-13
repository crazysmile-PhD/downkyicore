[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
& (Join-Path $PSScriptRoot 'validate-release-version.ps1') -RepositoryRoot $repository | Out-Null

function Invoke-RepositoryGit {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $output = @(& git -C $repository @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git -C $repository $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return $output
}

$releaseTagsByCommit = @{}
$tagLines = @(Invoke-RepositoryGit for-each-ref '--format=%(refname:short) %(objecttype) %(*objectname)' refs/tags)
foreach ($tagLine in $tagLines) {
    if ($tagLine -notmatch '^(?<name>\S+) tag (?<commit>[0-9a-fA-F]+)$') {
        continue
    }

    $tagName = $Matches.name
    $commit = $Matches.commit.ToLowerInvariant()
    if ($tagName -notmatch '^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
        continue
    }

    if (-not $releaseTagsByCommit.ContainsKey($commit)) {
        $releaseTagsByCommit[$commit] = [System.Collections.Generic.List[string]]::new()
    }
    $releaseTagsByCommit[$commit].Add($tagName)
}

$history = @(Invoke-RepositoryGit rev-list --first-parent HEAD)
foreach ($commit in ($history | Select-Object -Skip 1)) {
    $normalizedCommit = $commit.Trim().ToLowerInvariant()
    if (-not $releaseTagsByCommit.ContainsKey($normalizedCommit)) {
        continue
    }

    $releaseTags = @($releaseTagsByCommit[$normalizedCommit])
    if ($releaseTags.Count -ne 1) {
        throw "Release history is ambiguous at $commit; found annotated SemVer tags: $($releaseTags -join ', ')."
    }

    Write-Output $releaseTags[0]
    return
}

throw 'No previous annotated stable SemVer release tag exists on the first-parent history before HEAD.'
