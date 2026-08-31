[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ProjectPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ClassName,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestore,

    [switch]$NoBuild,

    [string]$ResultsDirectory,

    [string]$TrxName
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
    -ClassNames @($ClassName)

if ($result.ExitCode -ne 0) {
    throw "Focused test command failed with exit code $($result.ExitCode)."
}

$result
