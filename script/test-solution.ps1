[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [switch]$ListProjects,
    [switch]$ProbeExecution,
    [string]$ResultsDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")
$testsRoot = Join-Path $repositoryRoot "tests"
$testProjects = @(
    Get-ChildItem -LiteralPath $testsRoot -Filter "*.Tests.csproj" -File -Recurse |
        Sort-Object FullName
)

if ($testProjects.Count -eq 0) {
    throw "No test projects were found under $testsRoot."
}

$mutation = [Environment]::GetEnvironmentVariable("DOWNKYI_TEST_MUTATE_FULL_SUITE")
if ($ListProjects) {
    $listedProjects = if ($mutation -eq "omit-discovery") {
        @($testProjects | Select-Object -SkipLast 1)
    }
    else {
        $testProjects
    }

    foreach ($testProject in $listedProjects) {
        [System.IO.Path]::GetRelativePath($repositoryRoot, $testProject.FullName).Replace('\', '/')
    }

    return
}

$resolvedResultsDirectory = $null
if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $resolvedResultsDirectory = [System.IO.Path]::GetFullPath(
        $ResultsDirectory,
        (Get-Location).Path)
    New-Item -ItemType Directory -Force -Path $resolvedResultsDirectory | Out-Null
}

$executedProjects = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($testProject in $testProjects) {
    if ($ProbeExecution -and
        $mutation -eq "omit-execution" -and
        $testProject.FullName -eq $testProjects[-1].FullName) {
        continue
    }

    if ($ProbeExecution) {
        $result = [pscustomobject]@{ ExitCode = 0 }
    }
    else {
        Write-Host "Testing $($testProject.FullName)"
        $result = Invoke-DownKyiTestProject `
            -RepositoryRoot $repositoryRoot `
            -ProjectPath $testProject.FullName `
            -Configuration $Configuration `
            -NoRestore:$NoRestore `
            -NoBuild:$NoBuild `
            -ResultsDirectory $resolvedResultsDirectory `
            -TrxName "$($testProject.BaseName).trx"
    }

    if ($result.ExitCode -ne 0) {
        throw "Test project failed: $($testProject.FullName)"
    }

    if (-not $executedProjects.Add($testProject.FullName)) {
        throw "Test project executed more than once: $($testProject.FullName)"
    }

    if ($ProbeExecution) {
        [System.IO.Path]::GetRelativePath($repositoryRoot, $testProject.FullName).Replace('\', '/')
    }
}

$missingProjects = @(
    $testProjects |
        Where-Object { -not $executedProjects.Contains($_.FullName) }
)
if ($missingProjects.Count -gt 0) {
    throw "Test execution omitted projects: $($missingProjects.FullName -join ', ')"
}

if (-not $ProbeExecution) {
    Write-Host "Passed $($executedProjects.Count) test projects."
}
