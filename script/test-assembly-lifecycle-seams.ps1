[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$ResultsDirectory = "artifacts/test-results/lifecycle-seams",
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$RepositoryRoot,
    [switch]$BehaviorOnly,
    [ValidateSet("None", "FormalFailure", "Timeout", "InvariantFailure")]
    [string]$FixtureMode = "None",
    [string]$FixtureOutputDirectory
)

$ErrorActionPreference = "Stop"
$testScriptPath = $PSCommandPath
$repositoryRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    Split-Path -Parent $PSScriptRoot
}
else {
    [System.IO.Path]::GetFullPath($RepositoryRoot)
}
$scriptRoot = Join-Path $repositoryRoot "script"
$componentRoot = Join-Path $scriptRoot "assembly-lifecycle"
$resolvedResultsDirectory = [System.IO.Path]::GetFullPath(
    $ResultsDirectory,
    $repositoryRoot)
New-Item -ItemType Directory -Force -Path $resolvedResultsDirectory | Out-Null

$lifecycleScripts = @(
    (Join-Path $scriptRoot "test-assembly-lifecycle.ps1")
    (Join-Path $componentRoot "forensics.ps1")
    (Join-Path $componentRoot "process-execution.ps1")
    (Join-Path $componentRoot "result-classification.ps1")
    (Join-Path $componentRoot "report-rendering.ps1")
)
foreach ($scriptPath in $lifecycleScripts) {
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    if (@($parseErrors).Count -ne 0) {
        throw "Lifecycle script failed to parse: $scriptPath"
    }
}

. (Join-Path $componentRoot "forensics.ps1")
. (Join-Path $componentRoot "process-execution.ps1")
. (Join-Path $componentRoot "result-classification.ps1")
. (Join-Path $componentRoot "report-rendering.ps1")

function Assert-Contract([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw [System.InvalidOperationException]::new($Message)
    }
}

function New-ProcessFixture {
    param(
        [string]$Assembly = "Gate.AlarmParity",
        [int]$ExitCode = 0,
        [bool]$TimedOut = $false,
        [bool]$Slow = $true,
        [string]$SlowStatus = "captured",
        [AllowNull()][object]$SlowError = $null,
        [bool]$Lead = $true,
        [bool]$FormalGate = $true,
        [long]$ExitedAt = 11000,
        [object[]]$TimeoutEvidence = @(),
        [object[]]$Invariants = @(),
        [object[]]$Failures = @()
    )

    $slowEvidence = if ($SlowStatus -eq "captured") {
        @("evidence/$Assembly/slow")
    }
    else {
        @()
    }
    [pscustomobject]@{
        assembly = $Assembly
        iteration = 1
        phase = "execution"
        processId = 4242
        exitCode = $ExitCode
        durationMs = 6000.0
        timedOut = $TimedOut
        stdout = "{}"
        stderr = ""
        stdoutPath = "raw/$Assembly/execution.stdout.txt"
        stderrPath = "raw/$Assembly/execution.stderr.txt"
        observedChildren = @()
        transientChildren = @()
        residualChildren = @()
        childProcessObservationSampleCount = 1
        childProcessObservationDurationMs = 0.0
        residualChildEvidence = @()
        residualChildEvidenceStatus = "not-triggered"
        residualChildEvidenceErrorType = $null
        evidence = @($slowEvidence) + @($TimeoutEvidence)
        slowEvidence = $slowEvidence
        exitEvidence = @("evidence/$Assembly/exit")
        timeoutEvidence = @($TimeoutEvidence)
        diagnosticCaptureDurationMs = 10.0
        slowThresholdExceeded = $Slow
        slowEvidenceStatus = $SlowStatus
        slowEvidenceErrorType = $SlowError
        slowEvidenceTriggeredBeforeThreshold = $Lead
        processExitedAtUnixMs = $ExitedAt
        observedAtUnixMs = $ExitedAt
        ownedProcessFormalGatePassed = $FormalGate
        ownedProcessInvariants = @($Invariants)
        ownedProcessFailures = @($Failures)
        ownedProcessProofPath = "raw/$Assembly/execution.owned-process.json"
    }
}

function New-Report([object[]]$PhaseResults) {
    $marker = New-MarkerReaderSelfTestState -Required $false
    $arguments = @{
        PhaseResults = @($PhaseResults)
        Profile = "Local"
        ResolvedIterations = 1
        Runtime = (& dotnet --version).Trim()
        CommitSha = (& git -C $repositoryRoot rev-parse HEAD).Trim()
        WorkingTreeDirty = @(& git -C $repositoryRoot status --porcelain).Count -gt 0
        TestAssemblyCount = 1
        PhaseTimeoutSeconds = 1
        SlowPhaseThresholdSeconds = 5
        SlowEvidenceCaptureLeadMilliseconds = 1000
        ForensicsSelfTestCaptureLeadValidated = $true
        ReporterContractSelfTestPassed = $true
        ExitThresholdSeconds = 1
        OwnershipPassed = $true
        OwnershipError = $null
        MarkerReadContentionCount = 0
        MarkerReadRetriesExhaustedCount = 0
        MarkerReadErrorCount = 0
        MarkerReadErrorType = $null
        MarkerReaderSelfTest = $marker
        MarkerReaderSelfTestComplete = $false
    }
    $parameters = (Get-Command New-AssemblyLifecycleReport).Parameters
    if ($parameters.ContainsKey("ResidualChildQuiescenceMilliseconds")) {
        $arguments.ResidualChildQuiescenceMilliseconds = 500
        $arguments.ResidualChildPollMilliseconds = 25
        $arguments.DiagnosticsTool = $null
        $arguments.ResidualChildSelfTest = New-ResidualChildSelfTestState -Required $false
        $arguments.ResidualChildSelfTestComplete = $false
    }
    New-AssemblyLifecycleReport @arguments
}

function Write-Report([object[]]$PhaseResults, [string]$OutputDirectory) {
    $runRoot = Join-Path $OutputDirectory "fixture"
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    $report = New-Report $PhaseResults
    Write-AssemblyLifecycleReport -Report $report -RunRoot $runRoot `
        -OutputRoot $OutputDirectory -RunId "fixture"
    $report
}

function Invoke-FixtureMode([string]$Mode, [string]$OutputDirectory) {
    if ($Mode -eq "FormalFailure") {
        $raw = New-ProcessFixture -ExitCode 1 -SlowStatus "capture-failed" `
            -SlowError "InvalidOperationException" -FormalGate $false `
            -Invariants @([pscustomobject]@{ kind = "OperationCompletion"; state = "Violated" }) `
            -Failures @([pscustomobject]@{
                kind = "ExecutionFailed"; phase = "TargetExecution"
                channel = "Operation"; errorType = "InvalidOperationException"
            })
        $report = Write-Report @(New-ProcessPhaseResult $raw) $OutputDirectory
        Assert-Contract (-not $report.successful) "Formal failure unexpectedly passed."
        throw "Expected nonzero exit after aggregate report publication."
    }

    if ($Mode -eq "Timeout") {
        $script:diagnosticsTool = $null
        $rawRoot = Join-Path $OutputDirectory "raw"
        $evidenceRoot = Join-Path $OutputDirectory "evidence"
        $runRoot = $OutputDirectory
        $PhaseTimeoutSeconds = 1
        $SlowPhaseThresholdSeconds = 5
        $slowEvidenceCaptureLeadMilliseconds = 1000
        $residualChildQuiescenceMilliseconds = 500
        $residualChildPollMilliseconds = 25
        $processCleanupGraceSeconds = 2
        $processContainmentRequirement = "AllowWeakerFallback"
        New-Item -ItemType Directory -Force -Path $rawRoot, $evidenceRoot | Out-Null

        function Save-ProcessEvidence {
            param(
                [AllowNull()][object]$Process,
                [int]$TargetProcessId,
                [string]$AssemblyName,
                [int]$Iteration,
                [string]$Phase,
                [string]$Reason,
                [switch]$SkipManagedStack
            )

            $relativePath = "evidence/$AssemblyName/iteration-0001/$Phase-$Reason"
            $directory = Join-Path $runRoot $relativePath
            New-Item -ItemType Directory -Force -Path $directory | Out-Null
            [ordered]@{
                reason = $Reason
                processId = if ($null -ne $Process) {
                    $Process.Id
                }
                else {
                    $TargetProcessId
                }
                fixture = "lifecycle-alarm-parity"
            } | ConvertTo-Json | Set-Content -LiteralPath (
                Join-Path $directory "process-evidence.json") -Encoding utf8
            return $relativePath
        }

        function Wait-ResidualProcessTree {
            param(
                [int]$RootProcessId,
                [DateTimeOffset]$NotBeforeUtc,
                [int]$QuiescenceMilliseconds,
                [int]$PollMilliseconds
            )

            [pscustomobject]@{
                observedChildren = @()
                transientChildren = @()
                residualChildren = @()
                sampleCount = 1
                elapsedMilliseconds = 0.0
            }
        }

        $supervision = Join-Path $repositoryRoot (
            "tools/DownKyi.ProcessSupervision/bin/$Configuration/net10.0/" +
            "DownKyi.ProcessSupervision.dll")
        if (Test-Path -LiteralPath $supervision -PathType Leaf) {
            [Reflection.Assembly]::LoadFrom($supervision) | Out-Null
        }
        $pwsh = (Get-Process -Id $PID).Path
        $raw = Invoke-IsolatedProcess -AssemblyName "Gate.Timeout" -Iteration 1 `
            -Phase "execution" -FileName $pwsh -Arguments @(
                "-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Out.WriteLine('{}'); [System.Threading.Thread]::Sleep(5000)")
        $report = Write-Report @(New-ProcessPhaseResult $raw) $OutputDirectory
        Assert-Contract (-not $report.successful) "Timeout unexpectedly passed."
        throw "Expected nonzero timeout exit after aggregate report publication."
    }

    if ($Mode -eq "InvariantFailure") {
        $kinds = @(
            "TargetTerminal", "RequiredContainment", "OperationCompletion",
            "OperationBudget", "TreeQuiescence", "BoundedCleanup",
            "StreamDrain", "OwnershipLifetime")
        $results = @(
            foreach ($kind in $kinds) {
                foreach ($state in @("Unknown", "Violated")) {
                    $raw = New-ProcessFixture -Assembly "Gate.$kind.$state" `
                        -ExitCode 0 -Slow $false -SlowStatus "not-triggered"
                    $phase = New-ProcessPhaseResult $raw
                    Assert-Contract $phase.success `
                        "Invariant fixture baseline must have no unrelated failure."
                    $expectedErrorType = "RequiredInvariant$state"
                    $phase.success = $false
                    $phase.failureType = "OwnedProcessInvariantFailure"
                    $phase.errorType = $expectedErrorType
                    $phase.exitCode = 1
                    $phase | Add-Member requiredInvariantKind $kind
                    $phase | Add-Member requiredInvariantState $state
                    $phase | Add-Member invariantIsSoleFailureCause $true
                    $phase | Add-Member processExitCode $raw.exitCode
                    $phase | Add-Member ownedProcessFormalGatePassed $false -Force
                    $phase | Add-Member ownedProcessInvariants @(
                        [pscustomobject]@{ kind = $kind; state = $state }) -Force
                    Assert-Contract (-not $phase.success) `
                        "Invariant row must fail orchestration."
                    Assert-Contract ($phase.failureType -eq "OwnedProcessInvariantFailure") `
                        "Invariant row must retain OwnedProcessInvariantFailure."
                    Assert-Contract ($phase.errorType -eq $expectedErrorType) `
                        "Invariant row must retain its exact typed state."
                    $phase
                }
            })
        $report = Write-Report $results $OutputDirectory
        Assert-Contract (-not $report.successful) "Invariant failures unexpectedly passed."
        throw "Expected nonzero invariant exit after aggregate report publication."
    }
    throw "Unsupported fixture mode: $Mode"
}

function Invoke-ChildFixture([string]$Mode) {
    $output = Join-Path $resolvedResultsDirectory (
        "fixtures/{0}-{1}" -f $Mode, [Guid]::NewGuid().ToString("N"))
    $pwsh = (Get-Process -Id $PID).Path
    $text = @(& $pwsh -NoProfile -NonInteractive -File $testScriptPath `
        -Configuration $Configuration -RepositoryRoot $repositoryRoot `
        -FixtureMode $Mode -FixtureOutputDirectory $output 2>&1)
    [pscustomobject]@{
        exitCode = $LASTEXITCODE
        output = $text -join [Environment]::NewLine
        json = Join-Path $output "fixture/assembly-lifecycle-report.json"
        markdown = Join-Path $output "fixture/assembly-lifecycle-report.md"
    }
}

if ($FixtureMode -ne "None") {
    Assert-Contract (-not [string]::IsNullOrWhiteSpace($FixtureOutputDirectory)) `
        "FixtureOutputDirectory is required."
    Invoke-FixtureMode $FixtureMode ([System.IO.Path]::GetFullPath($FixtureOutputDirectory))
    return
}

$results = [System.Collections.Generic.List[object]]::new()
function Test-Behavior([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $results.Add([pscustomobject]@{ name = $Name; passed = $true; error = $null })
    }
    catch {
        $results.Add([pscustomobject]@{
            name = $Name; passed = $false; error = $_.Exception.Message })
    }
}

Test-Behavior "slow-captured-passes" {
    $phase = New-ProcessPhaseResult (New-ProcessFixture)
    Assert-Contract $phase.success "Captured slow evidence must pass."
}
Test-Behavior "slow-capture-failed-is-typed" {
    $phase = New-ProcessPhaseResult (New-ProcessFixture `
        -SlowStatus "capture-failed" -SlowError "InvalidOperationException")
    Assert-Contract (-not $phase.success) "Capture failure must fail."
    Assert-Contract ($phase.failureType -eq "SlowEvidenceMissing") `
        "Capture failure must retain SlowEvidenceMissing."
    Assert-Contract ($phase.errorType -eq "InvalidOperationException") `
        "Capture failure must retain its error type."
}
Test-Behavior "slow-process-exited-before-capture-is-typed" {
    $phase = New-ProcessPhaseResult (New-ProcessFixture `
        -SlowStatus "process-exited-before-capture")
    Assert-Contract (-not $phase.success) "Exited-before-capture must fail."
    Assert-Contract ($phase.failureType -eq "SlowEvidenceMissing") `
        "Exited-before-capture must retain SlowEvidenceMissing."
}
Test-Behavior "process-exit-threshold-is-inclusive" {
    $marker = [pscustomobject]@{
        started = [pscustomobject]@{ processId = 2147483000; timestamp = 9000 }
        disposing = [pscustomobject]@{ processId = 2147483000; timestamp = 9500 }
        disposed = [pscustomobject]@{ processId = 2147483000; timestamp = 10000 }
    }
    $getExit = {
        param($name, $time)
        @(New-AssemblyCompletionPhaseResults -AssemblyName $name -Iteration 1 `
            -Marker $marker -Execution (New-ProcessFixture -Assembly $name `
                -ExitedAt $time) -ExitThresholdSeconds 1 |
            Where-Object phase -eq "process-exit")[0]
    }
    Assert-Contract (& $getExit "Gate.Below" 10999).success "Below threshold must pass."
    Assert-Contract (& $getExit "Gate.Equal" 11000).success "Equal threshold must pass."
    $above = & $getExit "Gate.Above" 11001
    Assert-Contract (-not $above.success) "Above threshold must fail."
    Assert-Contract ($above.failureType -eq "ProcessExitFailed") `
        "Above threshold must retain ProcessExitFailed."
}

$evidence = @([pscustomobject]@{
    diagnosticOnly = $true
    captureMode = "owned-diagnostic-collector"
    managedStack = [pscustomobject]@{ captured = $true }
})
function Get-Forensics(
    [AllowEmptyCollection()][object[]]$Evidence = $evidence,
    [bool]$Lead = $true) {
    $raw = New-ProcessFixture -Assembly "Gate.Forensics" -Lead $Lead
    New-ForensicsSelfTestPhaseResult -SelfTest $raw `
        -SelfTestPhase (New-ProcessPhaseResult $raw) -EvidenceReports $Evidence
}
Test-Behavior "forensics-all-predicates-pass" {
    Assert-Contract (Get-Forensics).phaseResult.success `
        "Complete forensics proof must pass."
}
Test-Behavior "forensics-missing-evidence-fails" {
    $rejected = $false
    try {
        $null = Get-Forensics @()
    }
    catch {
        $rejected = $true
    }
    Assert-Contract $rejected "Missing forensics evidence must fail."
}
Test-Behavior "forensics-missing-managed-stack-fails" {
    $missing = @([pscustomobject]@{
        diagnosticOnly = $true
        captureMode = "owned-diagnostic-collector"
        managedStack = [pscustomobject]@{ captured = $false }
    })
    Assert-Contract (-not (Get-Forensics $missing).phaseResult.success) `
        "Missing managed stack must fail."
}
Test-Behavior "forensics-unvalidated-lead-fails" {
    Assert-Contract (-not (Get-Forensics $evidence $false).phaseResult.success) `
        "Unvalidated capture lead must fail."
}
Test-Behavior "formal-failure-reports-before-nonzero-exit" {
    $child = Invoke-ChildFixture "FormalFailure"
    Assert-Contract ($child.exitCode -ne 0) "Formal failure must exit nonzero."
    Assert-Contract (Test-Path -LiteralPath $child.json) `
        "Formal failure lost JSON. $($child.output)"
    Assert-Contract (Test-Path -LiteralPath $child.markdown) "Formal failure lost Markdown."
    $report = Get-Content -LiteralPath $child.json -Raw | ConvertFrom-Json
    Assert-Contract (@($report.results)[0].failureType -eq "SlowEvidenceMissing") `
        "Formal failure lost its typed lifecycle alarm."
}
Test-Behavior "timeout-preserves-report-and-boundary-evidence" {
    $child = Invoke-ChildFixture "Timeout"
    Assert-Contract ($child.exitCode -ne 0) "Timeout must exit nonzero."
    Assert-Contract (Test-Path -LiteralPath $child.json) "Timeout lost JSON. $($child.output)"
    Assert-Contract (Test-Path -LiteralPath $child.markdown) "Timeout lost Markdown."
    $report = Get-Content -LiteralPath $child.json -Raw | ConvertFrom-Json
    $failure = @($report.results)[0]
    Assert-Contract ($failure.failureType -eq "Timeout") "Timeout lost typed failure."
    Assert-Contract ($failure.timedOut -eq $true) "Timeout boundary was not retained."
    Assert-Contract (@($failure.timeoutEvidence).Count -gt 0) "Timeout evidence was lost."
}
Test-Behavior "each-required-invariant-state-reports-before-exit" {
    $child = Invoke-ChildFixture "InvariantFailure"
    Assert-Contract ($child.exitCode -ne 0) "Invariant failures must exit nonzero."
    Assert-Contract (Test-Path -LiteralPath $child.json) "Invariant failures lost JSON."
    Assert-Contract (Test-Path -LiteralPath $child.markdown) "Invariant failures lost Markdown."
    $report = Get-Content -LiteralPath $child.json -Raw | ConvertFrom-Json
    $rows = @($report.results)
    Assert-Contract (-not $report.successful) "Invariant aggregate unexpectedly passed."
    Assert-Contract ($rows.Count -eq 16) "Invariant matrix must contain 16 rows."
    Assert-Contract (@($rows | Where-Object { -not $_.success }).Count -eq 16) `
        "Every invariant row must be unsuccessful."
    Assert-Contract (@($rows | Where-Object {
        $_.failureType -eq "OwnedProcessInvariantFailure"
    }).Count -eq 16) "Every invariant row must retain its typed classification."
    Assert-Contract (@($rows | Where-Object {
        $_.invariantIsSoleFailureCause -eq $true
    }).Count -eq 16) "No unrelated failure may keep an invariant row red."
    Assert-Contract (@($rows | Where-Object processExitCode -eq 0).Count -eq 16) `
        "Invariant rows must start from a successful process exit."
    Assert-Contract (@($rows | Where-Object requiredInvariantState -eq "Unknown").Count -eq 8) `
        "Every invariant needs an Unknown row."
    Assert-Contract (@($rows | Where-Object requiredInvariantState -eq "Violated").Count -eq 8) `
        "Every invariant needs a Violated row."
    Assert-Contract (@($rows | Where-Object {
        $_.requiredInvariantState -eq "Unknown" -and
        $_.errorType -eq "RequiredInvariantUnknown"
    }).Count -eq 8) "Unknown rows must retain RequiredInvariantUnknown."
    Assert-Contract (@($rows | Where-Object {
        $_.requiredInvariantState -eq "Violated" -and
        $_.errorType -eq "RequiredInvariantViolated"
    }).Count -eq 8) "Violated rows must retain RequiredInvariantViolated."
}

$summary = [ordered]@{
    repositoryRoot = $repositoryRoot
    commitSha = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    passed = @($results | Where-Object passed).Count
    failed = @($results | Where-Object { -not $_.passed }).Count
    results = @($results)
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (
    Join-Path $resolvedResultsDirectory "lifecycle-alarm-contracts.json") -Encoding utf8
$failed = @($results | Where-Object { -not $_.passed })
if ($failed.Count -gt 0) {
    throw "Lifecycle alarm parity failed: $(
        ($failed | ForEach-Object { "$($_.name): $($_.error)" }) -join '; ')"
}
Write-Host "Lifecycle alarm parity passed: $($results.Count) behavioral contracts."
if ($BehaviorOnly) { return }

$proof = New-MarkerReaderSelfTestState -Required $false
$proof.executed = $true
$marker = [pscustomobject]@{
    started = [pscustomobject]@{ processId = 123 }
    disposing = [pscustomobject]@{ processId = 123 }
    disposed = [pscustomobject]@{ processId = 123 }
}
Set-MarkerReaderSelfTestObservations -SelfTest $proof -LockedMarker $null `
    -UnlockedMarker $marker -ContentionCount 1
$classification = Complete-MarkerReaderSelfTestClassification -SelfTest $proof
if (-not $classification.complete) {
    throw "Marker-reader classification seam did not accept a valid proof."
}

. (Join-Path $scriptRoot "test-project-runner.ps1")
$runnerArguments = @{
    RepositoryRoot = $repositoryRoot
    ProjectPath = Join-Path $repositoryRoot (
        "tests/DownKyi.Architecture.Tests/DownKyi.Architecture.Tests.csproj")
    Configuration = $Configuration
    ClassNames = @("DownKyi.Architecture.Tests.AssemblyLifecycleArchitectureTests")
    ResultsDirectory = $resolvedResultsDirectory
    TrxName = "AssemblyLifecycleArchitectureTests.trx"
}
if ($NoRestore) { $runnerArguments.NoRestore = $true }
if ($NoBuild) { $runnerArguments.NoBuild = $true }
$result = Invoke-DownKyiTestProject @runnerArguments
if ($result.ExitCode -ne 0) {
    throw "Assembly lifecycle seam validation failed with exit code $($result.ExitCode)."
}
Write-Host "Assembly lifecycle seam validation passed."
