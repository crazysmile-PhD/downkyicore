[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$ResultsDirectory,
    [ValidateRange(1, 3600)]
    [int]$ExecutionTimeoutSeconds = 300,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")

$result = Invoke-DownKyiTestSolution `
    -RepositoryRoot $repositoryRoot `
    -Configuration $Configuration `
    -NoRestore:$NoRestore `
    -NoBuild:$NoBuild `
    -ResultsDirectory $ResultsDirectory `
    -ExecutionTimeoutSeconds $ExecutionTimeoutSeconds `
    -EvidenceDirectory $EvidenceDirectory
if ($result.ExitCode -ne 0) {
    try {
        $evidence = if (-not [string]::IsNullOrWhiteSpace($result.EvidenceDirectory) -and
            (Test-Path -LiteralPath $result.EvidenceDirectory -PathType Container)) {
            @(Get-ChildItem -LiteralPath $result.EvidenceDirectory -Filter "*.json" -File |
                ForEach-Object { [IO.Path]::GetRelativePath($repositoryRoot, $_.FullName).Replace("\", "/") })
        }
        else {
            @()
        }
        $diagnostic = if ($evidence.Count -gt 0) {
            "CentralTestRunner exited $($result.ExitCode); evidence: $($evidence -join ', ')"
        }
        else {
            "CentralTestRunner exited $($result.ExitCode); no flight recorder evidence was produced."
        }
        [Console]::Error.WriteLine($diagnostic)
    }
    catch {
        # Human presentation cannot change the runner's completed result.
    }
}
exit $result.ExitCode
