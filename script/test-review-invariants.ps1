[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$NoRestore,

    [switch]$NoBuild,

    [string]$ResultsDirectory = "artifacts/review-invariants",

    [switch]$SkipNormal,

    [ValidateRange(0, 63)]
    [int]$AdversarialShardIndex = 0,

    [ValidateRange(1, 64)]
    [int]$AdversarialShardCount = 1,

    [string]$EvidencePath,

    [string]$ExpectedCommitSha
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "delegated-cgroup-scope.ps1")

if (Test-DownKyiDelegatedCgroupScopeRequired) {
    Invoke-DownKyiDelegatedCgroupScope `
        -ScriptPath $PSCommandPath `
        -ArgumentList (ConvertTo-DownKyiPowerShellArgumentList $PSBoundParameters)
    return
}

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

        for ($proofIndex = 0; $proofIndex -lt $proofs.Count; $proofIndex++) {
            $proof = $proofs[$proofIndex]
            if ($proof.kind -ne "adversarial-mutation" -or
                $proof.kind -notin $requirements -or
                [string]::IsNullOrWhiteSpace($proof.project) -or
                [string]::IsNullOrWhiteSpace($proof.class) -or
                [string]::IsNullOrWhiteSpace($proof.environmentVariable) -or
                [string]::IsNullOrWhiteSpace($proof.environmentValue) -or
                $proof.expectedOutcome -ne "test-failure" -or
                ($null -ne $proof.expectedFailedTests -and
                    [int]$proof.expectedFailedTests -lt 1)) {
                throw "Review invariant '$($invariant.id)' contains an incomplete adversarial proof."
            }

            [pscustomobject]@{
                proofId = "$($invariant.id)/$($proof.environmentVariable)"
                invariantId = [string]$invariant.id
                project = [string]$proof.project
                class = [string]$proof.class
                environmentVariable = [string]$proof.environmentVariable
                environmentValue = [string]$proof.environmentValue
                expectedOutcome = [string]$proof.expectedOutcome
                expectedFailedTests = if ($null -eq $proof.expectedFailedTests) {
                    $null
                }
                else {
                    [int]$proof.expectedFailedTests
                }
                ordinal = $proofIndex
            }
        }
    }
)
$duplicateProofIds = @(
    $adversarialProofs |
        Group-Object proofId |
        Where-Object Count -gt 1 |
        Select-Object -ExpandProperty Name
)
if ($duplicateProofIds.Count -gt 0) {
    throw "Duplicate adversarial proof identities: $($duplicateProofIds -join ', ')"
}
if ($AdversarialShardIndex -ge $AdversarialShardCount) {
    throw "Adversarial shard index must be less than shard count."
}
$selectedAdversarialProofs = @(
    $orderedProofs = @($adversarialProofs | Sort-Object proofId)
    for ($proofIndex = 0; $proofIndex -lt $orderedProofs.Count; $proofIndex++) {
        if ($proofIndex % $AdversarialShardCount -eq $AdversarialShardIndex) {
            $orderedProofs[$proofIndex]
        }
    }
)
if ($selectedAdversarialProofs.Count -eq 0) {
    throw "Adversarial shard $AdversarialShardIndex of $AdversarialShardCount owns no proofs."
}

New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$projectGroups = @($testClasses | Group-Object project | Sort-Object Name)
$totalPassed = 0

if (-not $NoBuild) {
    $requiredProjects = @(
        @($selectedAdversarialProofs.project) +
        $(if ($SkipNormal) { @() } else { @($projectGroups.Name) }) |
            Sort-Object -Unique
    )
    foreach ($requiredProject in $requiredProjects) {
        $buildArguments = @(
            "build"
            (Join-Path $repositoryRoot $requiredProject)
            "-c"
            $Configuration
            "--no-incremental"
            "-p:TreatWarningsAsErrors=true"
            "-p:CodeAnalysisTreatWarningsAsErrors=true"
            "-p:EnableNETAnalyzers=true"
            "-p:AnalysisMode=All"
            "-p:EnforceCodeStyleInBuild=true"
            "-p:UseSharedCompilation=false"
        )
        if ($NoRestore) {
            $buildArguments += "--no-restore"
        }
        & dotnet @buildArguments | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Review invariant project build failed: $requiredProject"
        }
    }
}

foreach ($projectGroup in @($projectGroups | Where-Object { -not $SkipNormal })) {
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
        -NoRestore `
        -NoBuild `
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

$proofEvidence = @()
foreach ($proof in $selectedAdversarialProofs) {
    $projectPath = Join-Path $repositoryRoot $proof.project
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Adversarial proof project is missing: $($proof.project)"
    }

    $safeProofId = $proof.proofId -replace '[^A-Za-z0-9_.-]', '-'
    $proofResultRoot = Join-Path $resultRoot "proofs/$safeProofId"
    $trxName = "result.trx"
    $childEnvironment = @{
        $proof.environmentVariable = $proof.environmentValue
    }
    Write-Host "Running adversarial proof: $($proof.proofId) in $($proof.project)"
    $result = Invoke-DownKyiTestProject `
        -RepositoryRoot $repositoryRoot `
        -ProjectPath $projectPath `
        -Configuration $Configuration `
        -NoRestore `
        -NoBuild `
        -ResultsDirectory $proofResultRoot `
        -TrxName $trxName `
        -ClassNames @($proof.class) `
        -EnvironmentVariables $childEnvironment
    $mutationExitCode = $result.ExitCode

    $trxPath = $result.TrxPath
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "Adversarial proof report is missing: $trxPath"
    }

    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $failed = [int]$counters.failed
    $executed = [int]$counters.executed
    $unexpectedFailureCount =
        $null -ne $proof.expectedFailedTests -and
        $failed -ne [int]$proof.expectedFailedTests
    if ($mutationExitCode -eq 0 -or
        $failed -eq 0 -or
        $executed -eq 0 -or
        $unexpectedFailureCount) {
        throw "Adversarial proof did not make the invariant test fail closed: project=$($proof.project) class=$($proof.class) exitCode=$mutationExitCode executed=$executed failed=$failed."
    }

    Write-Host "Adversarial proof rejected the injected mutation: executed=$executed failed=$failed."
    $proofEvidence += [pscustomobject]@{
        proofId = [string]$proof.proofId
        invariantId = [string]$proof.invariantId
        project = [string]$proof.project
        class = [string]$proof.class
        environmentVariable = [string]$proof.environmentVariable
        environmentValue = [string]$proof.environmentValue
        trxFile = "proofs/$safeProofId/$trxName"
        runnerExitCode = [int]$mutationExitCode
        executed = [int]$executed
        failed = [int]$failed
        expectedFailedTests = $proof.expectedFailedTests
    }
}

if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    . (Join-Path $PSScriptRoot "ci-evidence.ps1")
    $head = Assert-DownKyiCiEvidenceAuthority `
        -RepositoryRoot $repositoryRoot `
        -ExpectedCommitSha $ExpectedCommitSha
    $platform = Get-DownKyiCurrentTestPlatform `
        -RepositoryRoot $repositoryRoot `
        -Configuration $Configuration
    Write-DownKyiJsonEvidence -EvidencePath $EvidencePath -Evidence ([ordered]@{
        schemaVersion = 1
        kind = "review-mutations"
        identity = "review/$platform/$AdversarialShardIndex-of-$AdversarialShardCount"
        commitSha = $head
        platform = $platform
        shardIndex = $AdversarialShardIndex
        shardCount = $AdversarialShardCount
        proofs = $proofEvidence
        successful = $true
    })
}

Write-Host "Review invariant gate passed: $($invariants.Count) root-cause invariants, $($projectGroups.Count) declared test projects, $totalPassed normal tests, $($selectedAdversarialProofs.Count) of $($adversarialProofs.Count) adversarial proofs in shard $AdversarialShardIndex/$AdversarialShardCount."

# Expected mutation failures leave the native exit code nonzero on Unix hosts.
exit 0
