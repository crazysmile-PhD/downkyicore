[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,
    [Parameter(Mandatory)]
    [string]$ExpectedCommitSha,
    [Parameter(Mandatory)]
    [ValidateSet("PR", "Main")]
    [string]$ExpectedLifecycleProfile,
    [Parameter(Mandatory)]
    [string]$ReleaseBuildResult,
    [Parameter(Mandatory)]
    [string]$DebugBuildResult,
    [Parameter(Mandatory)]
    [string]$RepositorySuiteResult,
    [Parameter(Mandatory)]
    [string]$ReviewMutationsResult,
    [Parameter(Mandatory)]
    [string]$AssemblyLifecycleResult,
    [Parameter(Mandatory)]
    [string]$FormatResult,
    [Parameter(Mandatory)]
    [string]$Aria2TlsSecurityResult,
    [Parameter(Mandatory)]
    [string]$PackageAuditResult
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")
Import-DownKyiCentralTestRunner `
    -RepositoryRoot $repositoryRoot `
    -Configuration Release `
    -BuildIfMissing `
    -NoRestore

$upstreamResults = [Collections.Generic.Dictionary[string, string]]::new(
    [StringComparer]::Ordinal)
$upstreamResults.Add("release-build", $ReleaseBuildResult)
$upstreamResults.Add("debug-build", $DebugBuildResult)
$upstreamResults.Add("repository-suite", $RepositorySuiteResult)
$upstreamResults.Add("review-mutations", $ReviewMutationsResult)
$upstreamResults.Add("assembly-lifecycle", $AssemblyLifecycleResult)
$upstreamResults.Add("format", $FormatResult)
$upstreamResults.Add("aria2-tls-security", $Aria2TlsSecurityResult)
$upstreamResults.Add("package-audit", $PackageAuditResult)
$options = [DownKyi.CentralTestRunner.CiEvidenceAggregationOptions]::new(
    $repositoryRoot,
    $EvidenceRoot,
    $ExpectedCommitSha,
    $ExpectedLifecycleProfile,
    $upstreamResults)
$result = [DownKyi.CentralTestRunner.CiEvidenceAggregator]::Validate($options)
Write-Host (
    "Required CI evidence passed: builds=$($result.BuildCount), " +
    "repositoryShards=$($result.RepositoryShardCount), " +
    "repositoryProjects=$($result.RepositoryProjectCount), " +
    "reviewShards=$($result.ReviewShardCount), " +
    "reviewProofs=$($result.ReviewProofCount), " +
    "lifecycleAssemblies=$($result.LifecycleAssemblyCount).")
