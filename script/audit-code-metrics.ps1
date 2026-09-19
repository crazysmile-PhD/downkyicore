param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoRestore,

    [string]$OutputDirectory = './artifacts/code-metrics'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'DownKyi.sln'
$resolvedOutputDirectory = if ([System.IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$sarifDirectory = [System.IO.Path]::GetFullPath((Join-Path $resolvedOutputDirectory 'raw-sarif'))
$outputPrefix = $resolvedOutputDirectory.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $sarifDirectory.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "SARIF output escaped the report directory: $sarifDirectory"
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $sarifDirectory) {
    Remove-Item -LiteralPath $sarifDirectory -Recurse
}
New-Item -ItemType Directory -Path $sarifDirectory -Force | Out-Null

$buildArguments = @(
    'build'
    $solutionPath
    '-c', $Configuration
    '--no-incremental'
    '-p:DownKyiLegacyCaAudit=true'
    "-p:DownKyiLegacyCaAuditSarifDirectory=$sarifDirectory"
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

$buildOutput = @(& dotnet @buildArguments 2>&1)
$buildExitCode = $LASTEXITCODE
$buildLines = @($buildOutput | ForEach-Object { $_.ToString() })
$buildLines | ForEach-Object { Write-Host $_ }
$checkoutPathVariants = @(
    $repositoryRoot
    $repositoryRoot.Replace('\', '/')
) | Sort-Object -Unique
$redactedBuildLines = @($buildLines | ForEach-Object {
    $redactedLine = $_
    foreach ($checkoutPath in $checkoutPathVariants) {
        $redactedLine = [regex]::Replace(
            $redactedLine,
            [regex]::Escape($checkoutPath),
            '<repo>',
            [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    }
    $redactedLine
})
Set-Content -LiteralPath (Join-Path $resolvedOutputDirectory 'raw-build.log') `
    -Value $redactedBuildLines `
    -Encoding utf8NoBOM
if ($buildExitCode -ne 0) {
    $global:LASTEXITCODE = $buildExitCode
    exit $LASTEXITCODE
}

$reportScript = Join-Path $PSScriptRoot 'code-metrics/report-legacy-ca.ps1'
$baselinePath = Join-Path $PSScriptRoot 'code-metrics/legacy-ca-baseline.json'
& pwsh -NoProfile -File $reportScript `
    -SarifDirectory $sarifDirectory `
    -OutputDirectory $resolvedOutputDirectory `
    -BaselinePath $baselinePath `
    -RepositoryRoot $repositoryRoot
$reportExitCode = $LASTEXITCODE
if ($reportExitCode -ne 0) {
    exit $reportExitCode
}

exit 0
