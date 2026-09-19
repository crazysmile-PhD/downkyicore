param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'DownKyi.sln'

$buildArguments = @(
    'build'
    $solutionPath
    '-c', $Configuration
    '--no-incremental'
    '-p:DownKyiLegacyCaAudit=true'
    '-p:TreatWarningsAsErrors=false'
    '-p:CodeAnalysisTreatWarningsAsErrors=false'
    '-p:EnableNETAnalyzers=true'
    '-p:AnalysisMode=All'
    '-p:EnforceCodeStyleInBuild=true'
    '-p:UseSharedCompilation=false'
)

if ($NoRestore) {
    $buildArguments += '--no-restore'
}

& dotnet @buildArguments
exit $LASTEXITCODE
