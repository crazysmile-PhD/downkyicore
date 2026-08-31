[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("Local", "PR", "Main", "Rehearsal", "Flaky")]
    [string]$Profile = "Local",
    [ValidateRange(0, 10000)]
    [int]$Iterations = 0,
    [ValidateRange(1, 3600)]
    [int]$PhaseTimeoutSeconds = 180,
    [ValidateRange(0.1, 60)]
    [double]$SlowPhaseThresholdSeconds = 5,
    [ValidateRange(0.01, 60)]
    [double]$ExitThresholdSeconds = 1,
    [string[]]$AssemblyPattern = @("*"),
    [string]$ResultsDirectory = "artifacts/assembly-lifecycle",
    [string]$DiagnosticsToolPath,
    [switch]$ValidateForensics,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")
$solutionPath = Join-Path $repositoryRoot "DownKyi.sln"
$probeProject = Join-Path $repositoryRoot "tools/DownKyi.AssemblyLifecycleProbe/DownKyi.AssemblyLifecycleProbe.csproj"
$probeAssembly = Join-Path $repositoryRoot "tools/DownKyi.AssemblyLifecycleProbe/bin/$Configuration/net10.0/DownKyi.AssemblyLifecycleProbe.dll"
$processSupervisionAssembly = Join-Path $repositoryRoot (
    "tools/DownKyi.ProcessSupervision/bin/$Configuration/net10.0/" +
    "DownKyi.ProcessSupervision.dll")
$profileIterations = @{
    Local = 1
    PR = 3
    Main = 5
    Rehearsal = 100
    Flaky = 500
}
$resolvedIterations = if ($Iterations -gt 0) {
    $Iterations
}
else {
    $profileIterations[$Profile]
}
$runId = [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ")
$outputRoot = [System.IO.Path]::GetFullPath($ResultsDirectory, $repositoryRoot)
$runRoot = Join-Path $outputRoot $runId
$rawRoot = Join-Path $runRoot "raw"
$evidenceRoot = Join-Path $runRoot "evidence"
$ownershipRoot = Join-Path $runRoot "ownership"
$script:markerReadContentionCount = 0
$script:markerReadRetriesExhaustedCount = 0
$script:markerReadErrorCount = 0
$script:markerReadErrorType = $null
$slowEvidenceCaptureLeadMilliseconds = 1000
$processCleanupGraceSeconds = 5
$residualChildQuiescenceMilliseconds = 500
$residualChildPollMilliseconds = 25

$componentRoot = Join-Path $PSScriptRoot "assembly-lifecycle"
. (Join-Path $componentRoot "forensics.ps1")
. (Join-Path $componentRoot "process-execution.ps1")
. (Join-Path $componentRoot "result-classification.ps1")
. (Join-Path $componentRoot "report-rendering.ps1")

$forensicsSelfTestCaptureLeadValidated = $false
$markerReaderSelfTestRequired = $IsWindows -and
    @("PR", "Main", "Rehearsal", "Flaky").Contains($Profile)
$markerReaderSelfTestComplete = $false
$residualChildSelfTestComplete = $false
$residualChildSelfTest = New-ResidualChildSelfTestState `
    -Required ($IsWindows -and $ValidateForensics)
$markerReaderSelfTest = New-MarkerReaderSelfTestState `
    -Required $markerReaderSelfTestRequired

New-Item -ItemType Directory -Force -Path $rawRoot | Out-Null
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

if ($markerReaderSelfTestRequired -and -not $ValidateForensics) {
    throw "Formal Windows lifecycle profiles require -ValidateForensics."
}


$reporterContractSelfTestPassed = Test-XunitReporterContractMutation
if (-not $reporterContractSelfTestPassed) {
    throw "xUnit reporter contract mutation self-test did not fail closed."
}

$script:diagnosticsTool = Resolve-DiagnosticsTool
$ownershipPassed = $true
$ownershipError = $null
try {
    & (Join-Path $PSScriptRoot "audit-lifecycle-ownership.ps1") `
        -OutputDirectory $ownershipRoot
}
catch {
    $ownershipPassed = $false
    $ownershipError = $_.Exception.GetType().Name
    Write-Warning "Lifecycle ownership audit failed; dynamic probing will continue."
}

if (-not $NoBuild) {
    & dotnet build $solutionPath `
        -c $Configuration `
        --no-incremental `
        -p:TreatWarningsAsErrors=true `
        -p:CodeAnalysisTreatWarningsAsErrors=true `
        -p:EnableNETAnalyzers=true `
        -p:AnalysisMode=All `
        -p:EnforceCodeStyleInBuild=true `
        -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "Strict solution build failed."
    }
}

if (-not (Test-Path -LiteralPath $probeAssembly -PathType Leaf)) {
    throw "Assembly lifecycle probe was not built: $probeAssembly"
}
if (-not (Test-Path -LiteralPath $processSupervisionAssembly -PathType Leaf)) {
    throw "Process supervision assembly was not built: $processSupervisionAssembly"
}
[Reflection.Assembly]::LoadFrom($processSupervisionAssembly) | Out-Null

$allTestProjects = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "tests") `
        -Filter "*.Tests.csproj" `
        -File `
        -Recurse |
        Sort-Object BaseName
)
$currentPlatform = Get-DownKyiCurrentTestPlatform
$platformTestProjects = @(
    Select-DownKyiTestProjectsForCurrentPlatform `
        -Projects $allTestProjects `
        -CurrentPlatform $currentPlatform
)
$testProjects = @(
    $platformTestProjects |
        Where-Object {
            $project = $_
            @($AssemblyPattern | Where-Object { $project.BaseName -like $_ }).Count -gt 0
        }
)
if ($testProjects.Count -eq 0) {
    throw "No '$currentPlatform' xUnit test assemblies matched the requested patterns."
}

$phaseResults = @()
$forensicsValidation = Invoke-AssemblyLifecycleForensicsSelfTests `
    -ValidateForensics:$ValidateForensics `
    -TestProjects $testProjects `
    -Configuration $Configuration `
    -ProbeAssembly $probeAssembly `
    -RawRoot $rawRoot `
    -RunRoot $runRoot `
    -EvidenceRoot $evidenceRoot `
    -RepositoryRoot $repositoryRoot `
    -PhaseTimeoutSeconds $PhaseTimeoutSeconds `
    -SlowPhaseThresholdSeconds $SlowPhaseThresholdSeconds `
    -ExitThresholdSeconds $ExitThresholdSeconds `
    -SlowEvidenceCaptureLeadMilliseconds $slowEvidenceCaptureLeadMilliseconds `
    -ResidualChildQuiescenceMilliseconds $residualChildQuiescenceMilliseconds `
    -ResidualChildPollMilliseconds $residualChildPollMilliseconds `
    -DiagnosticsTool $script:diagnosticsTool `
    -ResidualChildSelfTest $residualChildSelfTest `
    -MarkerReaderSelfTest $markerReaderSelfTest
$phaseResults += @($forensicsValidation.phaseResults)
$forensicsSelfTestCaptureLeadValidated =
    $forensicsValidation.forensicsSelfTestCaptureLeadValidated
$residualChildSelfTestComplete =
    $forensicsValidation.residualChildSelfTestComplete
$markerReaderSelfTestComplete =
    $forensicsValidation.markerReaderSelfTestComplete

foreach ($testProject in $testProjects) {
    $assemblyName = $testProject.BaseName
    $assemblyPath = Join-Path $testProject.DirectoryName (
        "bin/$Configuration/net10.0/$assemblyName.dll")
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "Test assembly was not built: $assemblyPath"
    }

    Write-Host "Lifecycle probing $assemblyName ($resolvedIterations iteration(s))"
    for ($iteration = 1; $iteration -le $resolvedIterations; $iteration++) {
        $load = Invoke-IsolatedProcess `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "load" `
            -FileName "dotnet" `
            -Arguments @($probeAssembly, "--assembly", $assemblyPath)
        $phaseResults += New-ProcessPhaseResult -ProcessResult $load

        $assemblyInfo = Invoke-XunitAutomatedPhase `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "assembly-info" `
            -Arguments @(
                $assemblyPath,
                "-assemblyInfo",
                "-automated",
                "sync"
            )
        $phaseResults += New-ProcessPhaseResult -ProcessResult $assemblyInfo

        $discovery = Invoke-XunitAutomatedPhase `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "discovery" `
            -Arguments @(
                $assemblyPath,
                "-list",
                "full",
                "-automated",
                "sync",
                "-noLogo",
                "-noColor"
            )
        $phaseResults += New-ProcessPhaseResult -ProcessResult $discovery

        $markerPath = Join-Path $rawRoot (
            "$assemblyName/iteration-{0:D4}/execution.lifecycle" -f $iteration)
        $execution = Invoke-XunitAutomatedPhase `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Phase "execution" `
            -Arguments @(
                $assemblyPath,
                "-automated",
                "sync",
                "-noLogo",
                "-noColor",
                "-parallel",
                "none"
            ) `
            -Environment @{
                DOWNKYI_LIFECYCLE_MARKER = $markerPath
            } `
            -LifecycleMarkerPath $markerPath
        $phaseResults += New-ProcessPhaseResult -ProcessResult $execution

        $marker = Read-TeardownMarker -Path $markerPath
        $phaseResults += New-AssemblyCompletionPhaseResults `
            -AssemblyName $assemblyName `
            -Iteration $iteration `
            -Marker $marker `
            -Execution $execution `
            -ExitThresholdSeconds $ExitThresholdSeconds
    }
}

$runtime = (& dotnet --version).Trim()
$commitSha = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$workingTreeDirty = @(& git -C $repositoryRoot status --porcelain).Count -gt 0
$report = New-AssemblyLifecycleReport `
    -PhaseResults $phaseResults `
    -Profile $Profile `
    -ResolvedIterations $resolvedIterations `
    -Runtime $runtime `
    -CommitSha $commitSha `
    -WorkingTreeDirty $workingTreeDirty `
    -TestAssemblyCount $testProjects.Count `
    -PhaseTimeoutSeconds $PhaseTimeoutSeconds `
    -SlowPhaseThresholdSeconds $SlowPhaseThresholdSeconds `
    -SlowEvidenceCaptureLeadMilliseconds $slowEvidenceCaptureLeadMilliseconds `
    -ResidualChildQuiescenceMilliseconds $residualChildQuiescenceMilliseconds `
    -ResidualChildPollMilliseconds $residualChildPollMilliseconds `
    -ForensicsSelfTestCaptureLeadValidated $forensicsSelfTestCaptureLeadValidated `
    -ReporterContractSelfTestPassed $reporterContractSelfTestPassed `
    -ExitThresholdSeconds $ExitThresholdSeconds `
    -DiagnosticsTool $script:diagnosticsTool `
    -OwnershipPassed $ownershipPassed `
    -OwnershipError $ownershipError `
    -MarkerReadContentionCount $script:markerReadContentionCount `
    -MarkerReadRetriesExhaustedCount $script:markerReadRetriesExhaustedCount `
    -MarkerReadErrorCount $script:markerReadErrorCount `
    -MarkerReadErrorType $script:markerReadErrorType `
    -MarkerReaderSelfTest $markerReaderSelfTest `
    -MarkerReaderSelfTestComplete $markerReaderSelfTestComplete `
    -ResidualChildSelfTest $residualChildSelfTest `
    -ResidualChildSelfTestComplete $residualChildSelfTestComplete
Write-AssemblyLifecycleReport `
    -Report $report `
    -RunRoot $runRoot `
    -OutputRoot $outputRoot `
    -RunId $runId

if (-not $report.successful) {
    throw "Assembly Lifecycle Stability Gate failed."
}
