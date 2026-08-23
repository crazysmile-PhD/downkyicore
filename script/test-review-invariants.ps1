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
. (Join-Path $PSScriptRoot "test-project-runner.ps1")
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
    foreach ($invariant in $invariants) {
        if ([string]::IsNullOrWhiteSpace($invariant.id) -or
            [string]::IsNullOrWhiteSpace($invariant.guards) -or
            @($invariant.historicalRoots).Count -eq 0 -or
            @($invariant.testClasses).Count -eq 0) {
            throw "Review invariant '$($invariant.id)' has an incomplete contract."
        }

        foreach ($testClass in @($invariant.testClasses)) {
            if ([string]::IsNullOrWhiteSpace($testClass.project) -or
                [string]::IsNullOrWhiteSpace($testClass.class)) {
                throw "Review invariant '$($invariant.id)' contains an incomplete test reference."
            }
            $testClass
        }
    }
)
$adversarialProofs = @(
    foreach ($invariant in $invariants) {
        $requirements = @(
            $invariant.proofRequirements | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
        $proofs = @(
            $invariant.adversarialProofs | Where-Object { $null -ne $_ }
        )
        if ("adversarial-mutation" -in $requirements -and $proofs.Count -eq 0) {
            throw "Review invariant '$($invariant.id)' requires an adversarial mutation proof but declares no executable profile."
        }

        foreach ($proof in $proofs) {
            if ($proof.kind -ne "adversarial-mutation" -or
                $proof.kind -notin $requirements -or
                [string]::IsNullOrWhiteSpace($proof.project) -or
                [string]::IsNullOrWhiteSpace($proof.filter) -or
                [string]::IsNullOrWhiteSpace($proof.environmentVariable) -or
                [string]::IsNullOrWhiteSpace($proof.environmentValue) -or
                $proof.expectedOutcome -ne "test-failure") {
                throw "Review invariant '$($invariant.id)' contains an incomplete adversarial proof."
            }

            $proof
        }
    }
)

New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$projectGroups = @($testClasses | Group-Object project | Sort-Object Name)
$totalPassed = 0

foreach ($projectGroup in $projectGroups) {
    $projectPath = Join-Path $repositoryRoot $projectGroup.Name
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Review invariant test project is missing: $($projectGroup.Name)"
    }

    $classNames = @($projectGroup.Group.class | Sort-Object -Unique)
    $safeName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $trxName = "$safeName.trx"
    Write-Host "Running review invariants in $($projectGroup.Name)"
    $result = Invoke-DownKyiTestProject `
        -RepositoryRoot $repositoryRoot `
        -ProjectPath $projectPath `
        -Configuration $Configuration `
        -NoRestore:$NoRestore `
        -NoBuild:$NoBuild `
        -ResultsDirectory $resultRoot `
        -TrxName $trxName `
        -ClassNames $classNames
    if ($result.ExitCode -ne 0) {
        throw "Review invariant tests failed for $($projectGroup.Name)."
    }

    $trxPath = $result.TrxPath
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "Review invariant test report is missing: $trxPath"
    }

    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $failed = [int]$counters.failed
    $notExecuted = [int]$counters.notExecuted
    $passed = [int]$counters.passed
    $executedClasses = @(
        $trx.TestRun.TestDefinitions.UnitTest.TestMethod |
            ForEach-Object { [string]$_.className } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique
    )
    $missingClasses = @($classNames | Where-Object { $_ -notin $executedClasses })

    if ($failed -ne 0 -or $notExecuted -ne 0 -or $missingClasses.Count -ne 0) {
        throw "Review invariant coverage was incomplete for $($projectGroup.Name): passed=$passed failed=$failed notExecuted=$notExecuted missingClasses=$($missingClasses -join ',')."
    }

    $totalPassed += $passed
}

foreach ($proof in $adversarialProofs) {
    $projectPath = Join-Path $repositoryRoot $proof.project
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Adversarial proof project is missing: $($proof.project)"
    }

    $safeName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $trxName = "$safeName.adversarial.trx"
    $previousValue = [Environment]::GetEnvironmentVariable(
        $proof.environmentVariable,
        [EnvironmentVariableTarget]::Process)
    try {
        [Environment]::SetEnvironmentVariable(
            $proof.environmentVariable,
            $proof.environmentValue,
            [EnvironmentVariableTarget]::Process)
        Write-Host "Running adversarial proof: $($proof.kind) in $($proof.project)"
        $result = Invoke-DownKyiTestProject `
            -RepositoryRoot $repositoryRoot `
            -ProjectPath $projectPath `
            -Configuration $Configuration `
            -NoRestore:$NoRestore `
            -NoBuild:$NoBuild `
            -ResultsDirectory $resultRoot `
            -TrxName $trxName `
            -Filter $proof.filter
        $mutationExitCode = $result.ExitCode
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $proof.environmentVariable,
            $previousValue,
            [EnvironmentVariableTarget]::Process)
    }

    $trxPath = $result.TrxPath
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "Adversarial proof report is missing: $trxPath"
    }

    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $failed = [int]$counters.failed
    $executed = [int]$counters.executed
    if ($mutationExitCode -eq 0 -or $failed -eq 0 -or $executed -eq 0) {
        throw "Adversarial proof did not make the invariant test fail closed: project=$($proof.project) filter=$($proof.filter) exitCode=$mutationExitCode executed=$executed failed=$failed."
    }

    Write-Host "Adversarial proof rejected the injected mutation: executed=$executed failed=$failed."
}

Write-Host "Review invariant gate passed: $($invariants.Count) root-cause invariants, $($projectGroups.Count) test projects, $totalPassed tests, $($adversarialProofs.Count) adversarial proofs."

# Expected mutation failures leave the native exit code nonzero on Unix hosts.
exit 0
