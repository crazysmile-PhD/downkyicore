[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/code-metrics",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot "DownKyi.sln"
$targetsPath = Join-Path $PSScriptRoot "code-metrics/ca1506.targets"
$classificationPath = Join-Path $PSScriptRoot "code-metrics/ca1506-classifications.json"
$toolProject = Join-Path $repositoryRoot "tools/DownKyi.CodeMetricsAudit/DownKyi.CodeMetricsAudit.csproj"
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
Import-Module (Join-Path $PSScriptRoot "code-metrics/Ca1506TemporaryWorkspace.psm1") -Force
try {
    $workspace = New-Ca1506TemporaryWorkspace -TemporaryBasePath $tempBase
}
catch {
    [Console]::Error.WriteLine("CA1506 audit temporary workspace initialization failed.")
    exit 1
}

$auditRoot = $workspace.OperationDirectory
$sarifDirectory = Join-Path $auditRoot "sarif"
[IO.Directory]::CreateDirectory($sarifDirectory) | Out-Null
$previousUiLanguage = $env:DOTNET_CLI_UI_LANGUAGE
try {
    $env:DOTNET_CLI_UI_LANGUAGE = "en-US"
    $buildArguments = @(
        "build", $solutionPath,
        "-c", $Configuration,
        "--no-incremental",
        "-p:EnableNETAnalyzers=true",
        "-p:AnalysisMode=All",
        "-p:EnforceCodeStyleInBuild=true",
        "-p:TreatWarningsAsErrors=false",
        "-p:CodeAnalysisTreatWarningsAsErrors=false",
        "-p:UseSharedCompilation=false",
        "-p:CustomAfterMicrosoftCommonTargets=$targetsPath",
        "-p:CodeMetricsAuditSarifDirectory=$sarifDirectory"
    )
    if ($NoRestore) {
        $buildArguments += "--no-restore"
    }

    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "CA1506 audit build failed with exit code $LASTEXITCODE."
    }

    & dotnet run `
        --project $toolProject `
        -c $Configuration `
        --no-build `
        --no-restore `
        -- `
        --repository-root $repositoryRoot `
        --sarif-directory $sarifDirectory `
        --classification-file $classificationPath `
        --output-directory $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "CA1506 report generation failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:DOTNET_CLI_UI_LANGUAGE = $previousUiLanguage
    $removed = Remove-Ca1506TemporaryWorkspace `
        -OwnershipRoot $workspace.OwnershipRoot `
        -OperationDirectory $workspace.OperationDirectory
    if (-not $removed) {
        [Console]::Error.WriteLine("CA1506 audit temporary workspace cleanup failed.")
        exit 1
    }
}
