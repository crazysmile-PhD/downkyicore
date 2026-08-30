[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCommitSha,
    [Parameter(Mandatory)]
    [string]$ResultsDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$expectedCommit = $ExpectedCommitSha.ToLowerInvariant()
$observedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $observedCommit -ne $expectedCommit) {
    throw "Lifecycle lock preflight must run at exact commit $expectedCommit; observed $observedCommit."
}
if (@(& git -C $repositoryRoot status --porcelain).Count -ne 0) {
    throw "Lifecycle lock preflight requires a clean exact-commit checkout."
}

$topologyPath = Join-Path $repositoryRoot "script/assembly-lifecycle-release-topology.json"
$topology = Get-Content -LiteralPath $topologyPath -Raw | ConvertFrom-Json -Depth 10
$preflightProjects = @($topology.preflightProjects)
$expectedArchitectureClasses = @(
    "DownKyi.Architecture.Tests.WorkflowTestOwnershipArchitectureTests",
    "DownKyi.Architecture.Tests.AssemblyLifecycleArchitectureTests",
    "DownKyi.Architecture.Tests.AssemblyLifecycleReleaseEvidenceTests",
    "DownKyi.Architecture.Tests.TestRunnerPolicyArchitectureTests")
$expectedWindowsClasses = @(
    "DownKyi.Windows.Tests.AriaServerWindowsTests",
    "DownKyi.ProcessSupervision.Tests.TransitionBudgetTests",
    "DownKyi.ProcessSupervision.Tests.DiagnosticCollectorWindowTests")
if ([int]$topology.schemaVersion -ne 1 -or
    $topology.profile -ne "Rehearsal" -or
    [int]$topology.totalIterations -ne 100 -or
    $preflightProjects.Count -ne 2 -or
    $preflightProjects[0].assembly -ne "DownKyi.Architecture.Tests" -or
    $preflightProjects[1].assembly -ne "DownKyi.Windows.Tests" -or
    [string]::Join("|", @($preflightProjects[0].classes)) -ne
        [string]::Join("|", $expectedArchitectureClasses) -or
    [string]::Join("|", @($preflightProjects[1].classes)) -ne
        [string]::Join("|", $expectedWindowsClasses)) {
    throw "Lifecycle lock preflight topology is invalid."
}
$assemblies = @($preflightProjects | ForEach-Object assembly)

$env:MSBUILDDISABLENODEREUSE = "1"
function Invoke-DotNetChecked {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$projects = @(
    "tools/DownKyi.AssemblyLifecycleProbe/DownKyi.AssemblyLifecycleProbe.csproj",
    "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj") +
    @($assemblies | ForEach-Object { "tests/$_/$_.csproj" })
foreach ($project in $projects) {
    $projectPath = Join-Path $repositoryRoot $project
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Lifecycle lock preflight project is missing: $project"
    }
    Invoke-DotNetChecked -Arguments @("restore", $projectPath)
}

$strictBuildArguments = @(
    "-c", "Release",
    "--no-restore",
    "--no-incremental",
    "-p:TreatWarningsAsErrors=true",
    "-p:CodeAnalysisTreatWarningsAsErrors=true",
    "-p:EnableNETAnalyzers=true",
    "-p:AnalysisMode=All",
    "-p:EnforceCodeStyleInBuild=true",
    "-p:UseSharedCompilation=false")
foreach ($project in $projects) {
    $buildArguments = @("build", (Join-Path $repositoryRoot $project)) +
        $strictBuildArguments
    Invoke-DotNetChecked -Arguments $buildArguments
}

$resultRoot = [IO.Path]::GetFullPath($ResultsDirectory, $repositoryRoot)
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$verified = [Collections.Generic.List[object]]::new()
foreach ($preflightProject in $preflightProjects) {
    $assembly = [string]$preflightProject.assembly
    $classes = @($preflightProject.classes | ForEach-Object { [string]$_ })
    $projectResultRoot = Join-Path $resultRoot $assembly
    $trxName = "$assembly.lock-preflight.trx"
    $result = & (Join-Path $PSScriptRoot "test-project.ps1") `
        -ProjectPath "tests/$assembly/$assembly.csproj" `
        -RepositoryRoot $repositoryRoot `
        -Configuration Release `
        -NoRestore `
        -NoBuild `
        -ResultsDirectory $projectResultRoot `
        -TrxName $trxName `
        -ClassNames $classes `
        -ExecutionTimeoutSeconds 300
    if ($result.ExitCode -ne 0 -or
        $result.Report.Failed -ne 0 -or
        $result.Report.ExecutedExpectedClasses -ne $classes.Count -or
        $result.Report.PassedExpectedClasses -ne $classes.Count -or
        -not (Test-Path -LiteralPath $result.TrxPath -PathType Leaf)) {
        throw "Lifecycle lock preflight failed for $assembly."
    }

    $verified.Add([ordered]@{
        assembly = $assembly
        classes = $classes
        executed = $result.Report.Executed
        passed = $result.Report.PassedExpected
        trxRelativePath = [IO.Path]::GetRelativePath(
            $resultRoot,
            $result.TrxPath).Replace('\', '/')
        trxSha256 = (Get-FileHash -LiteralPath $result.TrxPath -Algorithm SHA256).
            Hash.ToLowerInvariant()
    })
}

$manifest = [ordered]@{
    schemaVersion = 1
    kind = "assembly-lifecycle-lock-preflight"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    commitSha = $expectedCommit
    configuration = "Release"
    projects = @($verified)
    successful = $true
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content `
    -LiteralPath (Join-Path $resultRoot "preflight-manifest.json") `
    -Encoding utf8
Write-Host "Lifecycle lock preflight passed for $($assemblies -join ', ') at $expectedCommit."
