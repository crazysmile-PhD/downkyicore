function Get-DownKyiCentralTestRunnerBuildArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release",

        [switch]$NoRestore
    )

    $buildArguments = @(
        "build"
        $ProjectPath
        "-c"
        $Configuration
        "-nodeReuse:false"
        "-p:UseSharedCompilation=false"
    )
    if ($NoRestore) {
        $buildArguments += "--no-restore"
    }

    return $buildArguments
}
function Import-DownKyiCentralTestRunner {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release",

        [switch]$BuildIfMissing,

        [switch]$NoRestore
    )

    if ($null -ne ("DownKyi.CentralTestRunner.CentralTestOrchestrator" -as [type])) {
        return
    }

    $projectPath = Join-Path $RepositoryRoot (
        "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj")
    $assemblyPath = Join-Path $RepositoryRoot (
        "tools/DownKyi.CentralTestRunner/bin/$Configuration/net10.0/" +
        "DownKyi.CentralTestRunner.dll")
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf) -and $BuildIfMissing) {
        $buildArguments = Get-DownKyiCentralTestRunnerBuildArguments `
            -ProjectPath $projectPath `
            -Configuration $Configuration `
            -NoRestore:$NoRestore
        & dotnet @buildArguments | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "The compiled central test runner build failed."
        }
    }
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "The compiled central test runner is missing: $assemblyPath"
    }

    [Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
}

function Get-DownKyiCurrentTestPlatform {
    [CmdletBinding()]
    param(
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    return [DownKyi.CentralTestRunner.CentralTestPolicy]::GetCurrentPlatform()
}

function Get-DownKyiTestProjectPlatforms {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    return [DownKyi.CentralTestRunner.CentralTestPolicy]::ReadProjectPlatforms($ProjectPath)
}

function Test-DownKyiTestProjectSupportsPlatform {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,
        [Parameter(Mandatory)]
        [string]$Platform,
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    return [DownKyi.CentralTestRunner.CentralTestPolicy]::SupportsPlatform(
        $ProjectPath,
        $Platform)
}

function Select-DownKyiTestProjectsForCurrentPlatform {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Projects,
        [string]$CurrentPlatform,
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    if ([string]::IsNullOrWhiteSpace($CurrentPlatform)) {
        $CurrentPlatform = [DownKyi.CentralTestRunner.CentralTestPolicy]::GetCurrentPlatform()
    }
    $paths = [string[]]@(
        $Projects | ForEach-Object {
            if ($_ -is [System.IO.FileSystemInfo]) { $_.FullName } else { [string]$_ }
        }
    )
    return @(
        [DownKyi.CentralTestRunner.CentralTestPolicy]::SelectProjects(
            $paths,
            $CurrentPlatform) |
            ForEach-Object { Get-Item -LiteralPath $_ }
    )
}

function Assert-DownKyiExpectedTestExecution {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$RunnerExitCode,
        [Parameter(Mandatory)]
        [string]$TrxPath,
        [Parameter(Mandatory)]
        [string[]]$ExpectedClassNames,
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    try {
        return [DownKyi.CentralTestRunner.CentralTestExecutionValidator]::ValidateExpectedExecution(
            $RunnerExitCode,
            $TrxPath,
            [string[]]$ExpectedClassNames)
    }
    catch {
        throw
    }
}
function Assert-DownKyiExpectedTestExecutionReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$RunnerExitCode,
        [Parameter(Mandatory)]
        [object]$Report,
        [Parameter(Mandatory)]
        [string[]]$ExpectedClassNames,
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    try {
        return [DownKyi.CentralTestRunner.CentralTestExecutionValidator]::
            ValidateExpectedExecutionReport(
                $RunnerExitCode,
                $Report,
                [string[]]$ExpectedClassNames)
    }
    catch {
        throw
    }
}

function Invoke-DownKyiTestProject {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$ProjectPath,
        [Parameter(Mandatory)]
        [ValidateSet("Debug", "Release")]
        [string]$Configuration,
        [switch]$NoRestore,
        [switch]$NoBuild,
        [string]$ResultsDirectory,
        [string]$TrxName,
        [string[]]$ClassNames = @(),
        [string]$Filter,
        [ValidateRange(1, 3600)]
        [int]$ExecutionTimeoutSeconds = 300,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    Import-DownKyiCentralTestRunner `
        -RepositoryRoot $RepositoryRoot `
        -Configuration $Configuration `
        -BuildIfMissing `
        -NoRestore:$NoRestore
    $options = [DownKyi.CentralTestRunner.CentralTestProjectOptions]::new(
        $RepositoryRoot,
        $ProjectPath,
        $Configuration,
        [bool]$NoRestore,
        [bool]$NoBuild,
        $ResultsDirectory,
        $TrxName,
        [string[]]$ClassNames,
        $Filter,
        $ExecutionTimeoutSeconds)
    try {
        return [DownKyi.CentralTestRunner.CentralTestOrchestrator]::RunProjectAsync(
            $options,
            $CancellationToken).GetAwaiter().GetResult()
    }
    catch {
        throw
    }
}

function Invoke-DownKyiTestSolution {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release",
        [switch]$NoRestore,
        [switch]$NoBuild,
        [string]$ResultsDirectory,
        [ValidateRange(1, 3600)]
        [int]$ExecutionTimeoutSeconds = 300,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    Import-DownKyiCentralTestRunner `
        -RepositoryRoot $RepositoryRoot `
        -Configuration $Configuration `
        -BuildIfMissing `
        -NoRestore:$NoRestore
    $options = [DownKyi.CentralTestRunner.CentralTestSolutionOptions]::new(
        $RepositoryRoot,
        $Configuration,
        [bool]$NoRestore,
        [bool]$NoBuild,
        $ResultsDirectory,
        $ExecutionTimeoutSeconds)
    try {
        return [DownKyi.CentralTestRunner.CentralTestOrchestrator]::RunSolutionAsync(
            $options,
            $CancellationToken).GetAwaiter().GetResult()
    }
    catch {
        throw
    }
}
