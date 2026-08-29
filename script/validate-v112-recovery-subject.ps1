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

& "$PSScriptRoot/validate-release-subject.ps1" `
    -SubjectDirectory $SubjectDirectory `
    -ReleaseVersion $ReleaseVersion `
    -SubjectSha $SubjectSha `
    -RequireMainAncestry

Write-Output "Validated immutable recovery subject $expectedReleaseVersion at $expectedSubjectSha."
