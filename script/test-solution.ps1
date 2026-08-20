[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$ResultsDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")
& (Join-Path $PSScriptRoot "test-platform-selector.ps1")
$testsRoot = Join-Path $repositoryRoot "tests"
$allTestProjects = @(
    Get-ChildItem -LiteralPath $testsRoot -Filter "*.Tests.csproj" -File -Recurse |
        Sort-Object FullName
)

if ($allTestProjects.Count -eq 0) {
    throw "No test projects were found under $testsRoot."
}

$currentPlatform = Get-DownKyiCurrentTestPlatform
$testProjects = @(
    Select-DownKyiTestProjectsForCurrentPlatform `
        -Projects $allTestProjects `
        -CurrentPlatform $currentPlatform
)
if ($testProjects.Count -eq 0) {
    throw "No test projects are owned by '$currentPlatform'."
}

Write-Host (
    "Selected $($testProjects.Count) of $($allTestProjects.Count) test projects " +
    "for '$currentPlatform'.")

$resolvedResultsDirectory = $null
if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $resolvedResultsDirectory = [System.IO.Path]::GetFullPath(
        $ResultsDirectory,
        (Get-Location).Path)
    New-Item -ItemType Directory -Force -Path $resolvedResultsDirectory | Out-Null
}

foreach ($testProject in $testProjects) {
    Write-Host "Testing $($testProject.FullName)"
    $result = Invoke-DownKyiTestProject `
        -RepositoryRoot $repositoryRoot `
        -ProjectPath $testProject.FullName `
        -Configuration $Configuration `
        -NoRestore:$NoRestore `
        -NoBuild:$NoBuild `
        -ResultsDirectory $resolvedResultsDirectory `
        -TrxName "$($testProject.BaseName).trx"
    if ($result.ExitCode -ne 0) {
        throw "Test project failed: $($testProject.FullName)"
    }
}

Write-Host "Passed $($testProjects.Count) '$currentPlatform' test projects."
