[CmdletBinding()]
param(
    [AllowEmptyString()][string]$PullRequestBody,
    [Parameter(Mandatory = $true)][string]$ActualHeadSha,
    [string]$GitHubOutputPath
)

$ErrorActionPreference = 'Stop'
$pattern = '<!-- downkyi-release-candidate run-id=(?<runId>[1-9]\d*) candidate-sha=(?<candidateSha>[0-9a-f]{40}) -->'
$matches = [regex]::Matches($PullRequestBody ?? '', $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)

if ($matches.Count -eq 0) {
    if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
        'is_candidate=false' | Add-Content -LiteralPath $GitHubOutputPath -Encoding utf8
    }
    Write-Output 'Pull request is not a generated release candidate.'
    return
}
if ($matches.Count -ne 1) {
    throw 'Release pull request body must contain exactly one candidate marker.'
}

$runId = $matches[0].Groups['runId'].Value
$candidateSha = $matches[0].Groups['candidateSha'].Value
if (-not [string]::Equals($candidateSha, $ActualHeadSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release candidate head changed from $candidateSha to $ActualHeadSha. Abandon this candidate and run Prepare Release again."
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    @(
        'is_candidate=true'
        "run_id=$runId"
        "candidate_sha=$candidateSha"
    ) | Add-Content -LiteralPath $GitHubOutputPath -Encoding utf8
}

Write-Output "Release candidate marker identifies run $runId and commit $candidateSha."
