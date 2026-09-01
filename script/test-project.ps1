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
    throw "Focused test command failed with exit code $($result.ExitCode)."
}

$result
