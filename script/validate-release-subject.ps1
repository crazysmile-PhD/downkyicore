[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SubjectDirectory,
    [Parameter(Mandatory = $true)]
    [string]$ReleaseVersion,
    [Parameter(Mandatory = $true)]
    [string]$SubjectSha
)

$ErrorActionPreference = 'Stop'
$subject = (Resolve-Path -LiteralPath $SubjectDirectory).Path
$versionValidator = Join-Path $PSScriptRoot 'validate-release-version.ps1'
& $versionValidator -RepositoryRoot $subject -GitRef "refs/tags/$ReleaseVersion" | Out-Null
$expectedApplicationVersion = (Get-Content -LiteralPath (Join-Path $subject 'version.txt') -Raw).Trim()
$expectedReleaseVersion = "v$expectedApplicationVersion"

function Invoke-SubjectGit {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $output = & git -C $subject @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git -C $subject $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return ($output -join [Environment]::NewLine).Trim()
}

$head = Invoke-SubjectGit rev-parse HEAD
if (-not [string]::Equals($head, $SubjectSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release subject HEAD is $head; expected $SubjectSha."
}

$tagType = Invoke-SubjectGit cat-file -t $expectedReleaseVersion
if ($tagType -cne 'tag') {
    throw "$expectedReleaseVersion must be an annotated tag; found object type '$tagType'."
}

$tagCommit = Invoke-SubjectGit rev-list -n 1 $expectedReleaseVersion
if (-not [string]::Equals($tagCommit, $SubjectSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "$expectedReleaseVersion resolves to $tagCommit; expected $SubjectSha."
}

$trackedChanges = Invoke-SubjectGit status --porcelain --untracked-files=no
if ($trackedChanges) {
    throw "Release subject contains tracked changes:`n$trackedChanges"
}

Invoke-SubjectGit fetch --no-tags origin '+refs/heads/main:refs/remotes/origin/main' | Out-Null
$mainCommit = Invoke-SubjectGit rev-parse 'refs/remotes/origin/main^{commit}'
if (-not [string]::Equals($SubjectSha, $mainCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release subject $SubjectSha does not equal current main $mainCommit."
}

Write-Output "Validated exact $expectedReleaseVersion release subject at $SubjectSha."
