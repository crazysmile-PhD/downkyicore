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
$testsRoot = Join-Path $repositoryRoot "tests"
$testProjects = @(
    Get-ChildItem -LiteralPath $testsRoot -Filter "*.Tests.csproj" -File -Recurse |
        Sort-Object FullName
)

if ($testProjects.Count -eq 0) {
    throw "No test projects were found under $testsRoot."
}

$resolvedResultsDirectory = $null
if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $resolvedResultsDirectory = [System.IO.Path]::GetFullPath(
        $ResultsDirectory,
        (Get-Location).Path)
    New-Item -ItemType Directory -Force -Path $resolvedResultsDirectory | Out-Null
}

foreach ($testProject in $testProjects) {
    $arguments = @(
        "test",
        $testProject.FullName,
        "-c",
        $Configuration
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    if ($NoBuild) {
        $arguments += "--no-build"
    }

    if ($resolvedResultsDirectory) {
        $arguments += @(
            "--logger",
            "trx;LogFileName=$($testProject.BaseName).trx",
            "--results-directory",
            $resolvedResultsDirectory
        )
    }

    Write-Host "Testing $($testProject.FullName)"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Test project failed: $($testProject.FullName)"
    }
}

Write-Host "Passed $($testProjects.Count) test projects."
