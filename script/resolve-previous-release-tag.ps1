[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$IncludeHead
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

$head = (Invoke-RepositoryGit rev-parse HEAD | Select-Object -First 1).Trim().ToLowerInvariant()
$tagLines = @(
    Invoke-RepositoryGit for-each-ref `
        '--sort=-version:refname' `
        '--format=%(refname:short) %(objecttype) %(*objectname)' `
        refs/tags
)
foreach ($tagLine in $tagLines) {
    if ($tagLine -notmatch '^(?<name>\S+) tag (?<commit>[0-9a-fA-F]+)$') {
        continue
    }

    $tagName = $Matches.name
    $commit = $Matches.commit.ToLowerInvariant()
    if ($tagName -notmatch '^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
        continue
    }

    if (-not $IncludeHead -and $commit -eq $head) {
        continue
    }

    & git -C $repository merge-base --is-ancestor $commit HEAD *> $null
    $reachabilityExitCode = $LASTEXITCODE
    if ($reachabilityExitCode -eq 1) {
        continue
    }
    if ($reachabilityExitCode -ne 0) {
        throw "Unable to determine whether release tag $tagName at $commit is reachable from HEAD."
    }

    Write-Output $tagName
    return
}

throw 'No reachable annotated stable SemVer release tag exists before HEAD.'
