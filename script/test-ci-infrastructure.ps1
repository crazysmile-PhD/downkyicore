[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoRestore,
    [switch]$NoBuild,
    [string]$ResultsDirectory = './TestResults',
    [string]$EvidenceDirectory = './artifacts/test-flight-recorder'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'test-project-runner.ps1')
. (Join-Path $PSScriptRoot 'ci-infrastructure-test-slices.ps1')

$platform = if ($IsWindows) {
    'Windows'
}
elseif ($IsLinux) {
    'Linux'
}
elseif ($IsMacOS) {
    'macOS'
}
else {
    throw 'The current operating system has no CI infrastructure test platform.'
}
if ($platform -eq 'Windows') {
    $env:DOWNKYI_TARGETED_RESOURCE_FORENSICS = '1'
}

$selectedSlices = @(
    Get-DownKyiCiInfrastructureTestSlices |
        Where-Object { $_.Platforms -contains $platform }
)
if ($selectedSlices.Count -eq 0) {
    throw "No CI infrastructure test slices support '$platform'."
}

foreach ($slice in $selectedSlices) {
    $projectName = [IO.Path]::GetFileNameWithoutExtension($slice.ProjectPath)
    $result = Invoke-DownKyiTestProject `
        -RepositoryRoot $repositoryRoot `
        -ProjectPath $slice.ProjectPath `
        -Configuration $Configuration `
        -NoRestore:$NoRestore `
        -NoBuild:$NoBuild `
        -ResultsDirectory $ResultsDirectory `
        -TrxName "ci-infrastructure-$projectName.trx" `
        -ClassNames $slice.ClassNames `
        -EvidenceDirectory $EvidenceDirectory
    if ($result.ExitCode -ne 0) {
        exit $result.ExitCode
    }
}

exit 0
