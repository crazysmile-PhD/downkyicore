[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ProjectPath,
    [string[]]$ClassName = @(),
    [string]$Filter,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$ResultsDirectory,
    [string]$TrxName,
    [ValidateRange(1, 3600)]
    [int]$ExecutionTimeoutSeconds = 300,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedProjectPath = [IO.Path]::GetFullPath($ProjectPath, $repositoryRoot)
$resolvedResultsDirectory = if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $null
}
else {
    [IO.Path]::GetFullPath($ResultsDirectory, $repositoryRoot)
}

. (Join-Path $PSScriptRoot "test-project-runner.ps1")
$result = Invoke-DownKyiTestProject `
    -RepositoryRoot $repositoryRoot `
    -ProjectPath $resolvedProjectPath `
    -Configuration $Configuration `
    -NoRestore:$NoRestore `
    -NoBuild:$NoBuild `
    -ResultsDirectory $resolvedResultsDirectory `
    -TrxName $TrxName `
    -ClassNames $ClassName `
    -Filter $Filter `
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
