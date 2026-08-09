[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestore,

    [switch]$NoBuild,

    [string]$ResultsDirectory = "artifacts/review-invariants"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot "docs/testing/review-invariant-corpus.json"
$resultRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ResultsDirectory))

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Review invariant corpus is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported review invariant corpus schema: $($manifest.schemaVersion)"
}

$invariants = @($manifest.prInvariants)
if ($invariants.Count -eq 0) {
    throw "The PR review invariant corpus is empty."
}

$duplicateIds = @(
    $invariants |
        Group-Object id |
        Where-Object Count -gt 1 |
        Select-Object -ExpandProperty Name
)
if ($duplicateIds.Count -gt 0) {
    throw "Duplicate review invariant ids: $($duplicateIds -join ', ')"
}

$testClasses = @(
    $invariants |
        ForEach-Object { @($_.testClasses) } |
        Where-Object { $_ -and $_.project -and $_.class }
)
if ($testClasses.Count -eq 0) {
    throw "The PR review invariant corpus does not reference any tests."
}

New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$projectGroups = @($testClasses | Group-Object project | Sort-Object Name)
$totalPassed = 0

foreach ($projectGroup in $projectGroups) {
    $projectPath = Join-Path $repositoryRoot $projectGroup.Name
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Review invariant test project is missing: $($projectGroup.Name)"
    }

    $classNames = @($projectGroup.Group.class | Sort-Object -Unique)
    $filter = ($classNames | ForEach-Object { "FullyQualifiedName~$_" }) -join "|"
    $safeName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $trxName = "$safeName.trx"
    $arguments = @(
        "test",
        $projectPath,
        "-c", $Configuration,
        "--filter", $filter,
        "--logger", "trx;LogFileName=$trxName",
        "--results-directory", $resultRoot
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }
    if ($NoBuild) {
        $arguments += "--no-build"
    }

    Write-Host "Running review invariants in $($projectGroup.Name)"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Review invariant tests failed for $($projectGroup.Name)."
    }

    $trxPath = Join-Path $resultRoot $trxName
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "Review invariant test report is missing: $trxPath"
    }

    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $passed = [int]$counters.passed
    $failed = [int]$counters.failed
    $notExecuted = [int]$counters.notExecuted
    if ($failed -ne 0 -or $notExecuted -ne 0 -or $passed -lt $classNames.Count) {
        throw "Review invariant coverage was incomplete for $($projectGroup.Name): passed=$passed failed=$failed notExecuted=$notExecuted requiredClasses=$($classNames.Count)."
    }

    $totalPassed += $passed
}

Write-Host "Review invariant gate passed: $($invariants.Count) root-cause invariants, $($projectGroups.Count) test projects, $totalPassed tests."
