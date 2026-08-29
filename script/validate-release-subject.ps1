[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SubjectDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [string]$SubjectSha,

    [switch]$RequireMainAncestry,

    [switch]$RequireExactMain,

    [string]$MainRef = 'refs/remotes/origin/main'
)

$ErrorActionPreference = 'Stop'
$subject = (Resolve-Path -LiteralPath $SubjectDirectory).Path

function Invoke-SubjectGit {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $output = & git -C $subject @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git -C $subject $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return ($output -join [Environment]::NewLine).Trim()
}

if ($ReleaseVersion -notmatch '^v(?<applicationVersion>\d+\.\d+\.\d+)$') {
    throw "Release version '$ReleaseVersion' must use the vMAJOR.MINOR.PATCH format."
}
$expectedApplicationVersion = $Matches.applicationVersion

$head = Invoke-SubjectGit rev-parse HEAD
if (-not [string]::Equals($head, $SubjectSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release subject HEAD is $head; expected $SubjectSha."
}

$tagType = Invoke-SubjectGit cat-file -t $ReleaseVersion
if ($tagType -cne 'tag') {
    throw "$ReleaseVersion must be an annotated tag; found object type '$tagType'."
}

$tagCommit = Invoke-SubjectGit rev-list -n 1 $ReleaseVersion
if (-not [string]::Equals($tagCommit, $SubjectSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "$ReleaseVersion resolves to $tagCommit; expected $SubjectSha."
}

$applicationVersion = (Get-Content -LiteralPath (Join-Path $subject 'version.txt') -Raw).Trim()
if ($applicationVersion -cne $expectedApplicationVersion) {
    throw "$ReleaseVersion requires application version $expectedApplicationVersion, but version.txt contains $applicationVersion."
}

$trackedChanges = Invoke-SubjectGit status --porcelain --untracked-files=no
if ($trackedChanges) {
    throw "Release subject contains tracked changes:`n$trackedChanges"
}

if ($RequireMainAncestry -or $RequireExactMain) {
    Invoke-SubjectGit fetch --no-tags origin "+refs/heads/main:$MainRef" | Out-Null
    $mainCommit = Invoke-SubjectGit rev-parse "$MainRef^{commit}"

    & git -C $subject merge-base --is-ancestor $SubjectSha $mainCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Release subject $SubjectSha is not an ancestor of current main $mainCommit."
    }

    if ($RequireExactMain -and
        -not [string]::Equals($SubjectSha, $mainCommit, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release subject $SubjectSha does not equal current main $mainCommit."
    }
}

Write-Output "Validated release subject $ReleaseVersion at $SubjectSha."
