[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SubjectSha,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path

function Invoke-RepositoryGit {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $output = @(& git -C $repository @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git -C $repository $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }

    return ($output -join [Environment]::NewLine).Trim()
}

$head = Invoke-RepositoryGit rev-parse 'HEAD^{commit}'
if (-not [string]::Equals($head, $SubjectSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release-preparation subject HEAD is $head; expected $SubjectSha."
}

$version = (Get-Content -LiteralPath (Join-Path $repository 'version.txt') -Raw).Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'Release-preparation version.txt must contain one stable semantic version.'
}

$changelog = (Get-Content -LiteralPath (Join-Path $repository 'CHANGELOG.md') -Raw).
    Replace("`r`n", "`n", [StringComparison]::Ordinal)
$markerPattern = "\A# 更新日志\n\n## \[$([regex]::Escape($version))\] - \d{4}-\d{2}-\d{2}\n\n<!-- release-prepared-from: (?<sha>[0-9a-fA-F]{40}) -->\n"
$markerMatch = [regex]::Match($changelog, $markerPattern)
if (-not $markerMatch.Success) {
    throw "CHANGELOG.md must begin with the $version release and its exact preparation source marker."
}

$sourceSha = $markerMatch.Groups['sha'].Value
$sourceCommit = Invoke-RepositoryGit rev-parse "$sourceSha^{commit}"
$firstParent = Invoke-RepositoryGit rev-parse "$SubjectSha^1"
if (-not [string]::Equals($sourceCommit, $firstParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release preparation is stale: selected source $sourceCommit is not subject first parent $firstParent. Regenerate it from current main."
}

$changedOutput = Invoke-RepositoryGit diff --name-only $sourceCommit $SubjectSha
$changedPaths = @($changedOutput -split '\r?\n') | Where-Object { $_ }
$expectedPaths = @('CHANGELOG.md', 'version.txt')
$pathDifference = @(Compare-Object -ReferenceObject $expectedPaths -DifferenceObject ($changedPaths | Sort-Object))
if ($pathDifference.Count -ne 0) {
    throw "Release preparation changed files outside its authority: $($changedPaths -join ', ')."
}

Write-Output "Validated release preparation $SubjectSha from exact source $sourceCommit."
