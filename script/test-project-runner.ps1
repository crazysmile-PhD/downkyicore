function Get-DownKyiTestRunnerTrustInputs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$ProjectPath
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $rootPrefix = $root.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $pathComparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    $pathComparer = if ($IsWindows) {
        [StringComparer]::OrdinalIgnoreCase
    }
    else {
        [StringComparer]::Ordinal
    }
    $tracked = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in @(& git -C $root ls-files)) {
        if ($LASTEXITCODE -ne 0) {
            throw "Could not enumerate tracked recovery trust inputs."
        }
        $null = $tracked.Add(([string]$path).Replace("\", "/"))
    }

    function ConvertTo-DownKyiTrackedRepositoryPath {
        param(
            [Parameter(Mandatory)]
            [string]$FullPath,
            [Parameter(Mandatory)]
            [string]$InputKind
        )

        $canonical = [IO.Path]::GetFullPath($FullPath)
        if (-not $canonical.StartsWith($rootPrefix, $pathComparison)) {
            throw "Recovery $InputKind escapes the repository trust root."
        }
        $relative = [IO.Path]::GetRelativePath($root, $canonical).Replace("\", "/")
        if (-not $tracked.Contains($relative)) {
            throw "Recovery $InputKind is not tracked by the validated repository head: $relative"
        }
        return $relative
    }

    $inputs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $projects = [Collections.Generic.Queue[string]]::new()
    $visitedProjects = [Collections.Generic.HashSet[string]]::new($pathComparer)
    $projects.Enqueue([IO.Path]::GetFullPath(
        "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj",
        $root))
    $projects.Enqueue([IO.Path]::GetFullPath($ProjectPath, $root))

    while ($projects.Count -gt 0) {
        $project = $projects.Dequeue()
        if (-not $visitedProjects.Add($project)) {
            continue
        }
        $null = $inputs.Add((ConvertTo-DownKyiTrackedRepositoryPath `
            -FullPath $project `
            -InputKind "project"))

        $evaluation = & dotnet msbuild $project -nologo `
            "-getItem:Compile,ProjectReference,AdditionalFiles,EmbeddedResource" 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Could not derive the compiled recovery trust closure for $($inputs | Sort-Object | Select-Object -Last 1)."
        }
        try {
            $items = ($evaluation | Out-String | ConvertFrom-Json -ErrorAction Stop).Items
        }
        catch {
            throw "MSBuild did not return an authoritative recovery trust closure."
        }

        foreach ($itemKind in @("Compile", "AdditionalFiles", "EmbeddedResource")) {
            foreach ($item in @($items.$itemKind)) {
                $null = $inputs.Add((ConvertTo-DownKyiTrackedRepositoryPath `
                    -FullPath $item.FullPath `
                    -InputKind $itemKind))
                if ($null -ne $item.DefiningProjectFullPath -and
                    ([IO.Path]::GetFullPath([string]$item.DefiningProjectFullPath)).StartsWith(
                        $rootPrefix,
                        $pathComparison)) {
                    $null = $inputs.Add((ConvertTo-DownKyiTrackedRepositoryPath `
                        -FullPath $item.DefiningProjectFullPath `
                        -InputKind "$itemKind defining project"))
                }
            }
        }
        foreach ($reference in @($items.ProjectReference)) {
            $referencePath = [IO.Path]::GetFullPath([string]$reference.FullPath)
            $null = ConvertTo-DownKyiTrackedRepositoryPath `
                -FullPath $referencePath `
                -InputKind "project reference"
            $projects.Enqueue($referencePath)
        }
    }

    foreach ($controlInput in @(
        ".editorconfig"
        ".github/actions/test-project/action.yml"
        ".github/actions/test-solution/action.yml"
        "Directory.Build.props"
        "Directory.Build.targets"
        "Directory.Packages.props"
        "docs/testing/test-runner-policy.json"
        "global.json"
        "script/invoke-ci-test-action.ps1"
        "script/test-project-runner.ps1"
    )) {
        if (-not $tracked.Contains($controlInput)) {
            throw "Recovery control-plane trust input is not tracked: $controlInput"
        }
        $null = $inputs.Add($controlInput)
    }

    return @($inputs | Sort-Object)
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
        [Parameter(Mandatory)]
        [Threading.CancellationToken]$TargetExitedToken,
        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    $Authorization.CompleteAsync($Budget, $TargetExitedToken, $CancellationToken).
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
