function Get-DownKyiTestRunnerTrustInputs {
    [CmdletBinding()]
    param()

    return @(
        ".github/actions/test-project/action.yml"
        ".github/actions/test-solution/action.yml"
        "script/invoke-ci-test-action.ps1"
        "Directory.Build.props"
        "Directory.Build.targets"
        "Directory.Packages.props"
        "global.json"
        "docs/testing/test-runner-policy.json"
        "tests/CentralTestExecutionGuard.cs"
        "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj"
        "tools/DownKyi.CentralTestRunner/CentralTestAuthorization.cs"
        "tools/DownKyi.CentralTestRunner/CentralTestContracts.cs"
        "tools/DownKyi.CentralTestRunner/CentralTestExecutionValidator.cs"
        "tools/DownKyi.CentralTestRunner/CentralTestPolicy.cs"
        "tools/DownKyi.CentralTestRunner/CentralTestRunner.cs"
        "tools/DownKyi.ProcessSupervision/DownKyi.ProcessSupervision.csproj"
        "tools/DownKyi.ProcessSupervision/OwnedProcessLease.cs"
        "tools/DownKyi.ProcessSupervision/ProcessSupervisionContracts.cs"
        "tools/DownKyi.ProcessSupervision/SupervisorHost.cs"
    )
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
        $buildArguments = @("build", $projectPath, "-c", $Configuration)
        if ($NoRestore) {
            $buildArguments += "--no-restore"
        }
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

function Get-DownKyiTestRunnerPolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$ProjectPath,
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    return [DownKyi.CentralTestRunner.CentralTestPolicy]::ReadRunnerPolicy(
        $RepositoryRoot,
        $ProjectPath)
}

function New-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    return [DownKyi.CentralTestRunner.CentralTestAuthorization]::Issue(
        $Arguments,
        $RepositoryRoot)
}

function Set-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [DownKyi.CentralTestRunner.CentralTestAuthorization]$Authorization,
        [Parameter(Mandatory)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo
    )

    $Authorization.ApplyTo($StartInfo)
}

function Complete-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [DownKyi.CentralTestRunner.CentralTestAuthorization]$Authorization,
        [Parameter(Mandatory)]
        [DownKyi.ProcessSupervision.TransitionBudget]$Budget,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    $Authorization.CompleteAsync($Budget, $CancellationToken).
        GetAwaiter().GetResult()
}

function Close-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [DownKyi.CentralTestRunner.CentralTestAuthorization]$Authorization
    )

    if ($null -ne $Authorization) {
        $Authorization.Dispose()
    }
}

function Assert-DownKyiTestExecutionReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TrxPath,
        [string[]]$ExpectedClassNames = @(),
        [switch]$RequireUniqueReport,
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release"
    )

    Import-DownKyiCentralTestRunner -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    try {
        return [DownKyi.CentralTestRunner.CentralTestExecutionValidator]::ValidateReport(
            $TrxPath,
            [string[]]$ExpectedClassNames,
            [bool]$RequireUniqueReport)
    }
    catch {
        throw
    }
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
        -BuildIfMissing:(-not $NoBuild) `
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
        -BuildIfMissing:(-not $NoBuild) `
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
