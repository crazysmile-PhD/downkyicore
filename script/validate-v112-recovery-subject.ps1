param(
    [Parameter(Mandatory = $true)]
    [string]$SubjectDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [string]$SubjectSha
)

$ErrorActionPreference = 'Stop'
$expectedReleaseVersion = 'v1.1.2'
$expectedApplicationVersion = '1.1.2'
$expectedSubjectSha = '16c690d8719f86eb6eecb56c24efabc1afc41d55'

if ($ReleaseVersion -cne $expectedReleaseVersion) {
    throw "Recovery release version must be exactly $expectedReleaseVersion."
}
if ($SubjectSha -cne $expectedSubjectSha) {
    throw "Recovery subject SHA must be exactly $expectedSubjectSha."
}

$subject = (Resolve-Path -LiteralPath $SubjectDirectory).Path

function Invoke-SubjectGit {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $output = & git -C $subject @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git -C $subject $($Arguments -join ' ') failed."
    }
    return ($output -join "`n").Trim()
}

$head = Invoke-SubjectGit rev-parse HEAD
if ($head -cne $expectedSubjectSha) {
    throw "Recovery subject HEAD is $head; expected $expectedSubjectSha."
}

$tagType = Invoke-SubjectGit cat-file -t $expectedReleaseVersion
if ($tagType -cne 'tag') {
    throw "$expectedReleaseVersion must remain an annotated tag."
}

$tagCommit = Invoke-SubjectGit rev-list -n 1 $expectedReleaseVersion
if ($tagCommit -cne $expectedSubjectSha) {
    throw "$expectedReleaseVersion resolves to $tagCommit; expected $expectedSubjectSha."
}

$applicationVersion = (Get-Content -LiteralPath (Join-Path $subject 'version.txt') -Raw).Trim()
if ($applicationVersion -cne $expectedApplicationVersion) {
    throw "Recovery subject version.txt is $applicationVersion; expected $expectedApplicationVersion."
}

$trackedChanges = Invoke-SubjectGit status --porcelain --untracked-files=no
if ($trackedChanges) {
    throw "Recovery subject contains tracked changes:`n$trackedChanges"
}

Write-Output "Validated immutable recovery subject $expectedReleaseVersion at $expectedSubjectSha."
