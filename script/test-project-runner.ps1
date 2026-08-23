function Get-DownKyiCurrentTestPlatform {
    [CmdletBinding()]
    param()

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        return "Windows"
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::Linux)) {
        return "Linux"
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [System.Runtime.InteropServices.OSPlatform]::OSX)) {
        return "macOS"
    }

    throw "The current operating system has no declared DownKyi test platform."
}

function Get-DownKyiTestProjectPlatforms {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath
    )

    $project = Get-Item -LiteralPath $ProjectPath
    [xml]$projectXml = Get-Content -LiteralPath $project.FullName -Raw
    $declarations = @($projectXml.SelectNodes(
            "/Project/PropertyGroup[not(@Condition)]/DownKyiTestPlatforms[not(@Condition)]"))
    if ($declarations.Count -ne 1) {
        throw (
            "Test project must declare exactly one unconditional " +
            "DownKyiTestPlatforms value: $($project.FullName)")
    }

    $platformTokens = @($declarations[0].InnerText.Split(';'))
    if ($platformTokens.Count -eq 0 -or
        @($platformTokens | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "DownKyiTestPlatforms contains an empty platform in $($project.FullName)."
    }

    $allowedPlatforms = @("Windows", "Linux", "macOS")
    $platforms = @($platformTokens | ForEach-Object { $_.Trim() })
    $seenPlatforms = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($platform in $platforms) {
        if (-not $allowedPlatforms.Contains($platform)) {
            throw (
                "Unsupported DownKyiTestPlatforms value '$platform' in " +
                "$($project.FullName). Allowed values: $($allowedPlatforms -join ', ').")
        }

        if (-not $seenPlatforms.Add($platform)) {
            throw "Duplicate DownKyiTestPlatforms value '$platform' in $($project.FullName)."
        }
    }

    return $platforms
}

function Test-DownKyiTestProjectSupportsPlatform {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [string]$Platform
    )

    $allowedPlatforms = @("Windows", "Linux", "macOS")
    if (-not $allowedPlatforms.Contains($Platform)) {
        throw "Unsupported current test platform '$Platform'."
    }

    $projectPlatforms = @(Get-DownKyiTestProjectPlatforms -ProjectPath $ProjectPath)
    return $projectPlatforms.Contains($Platform)
}

function Select-DownKyiTestProjectsForCurrentPlatform {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object[]]$Projects,

        [string]$CurrentPlatform = (Get-DownKyiCurrentTestPlatform)
    )

    $allowedCurrentPlatforms = @("Windows", "Linux", "macOS")
    if (-not $allowedCurrentPlatforms.Contains($CurrentPlatform)) {
        throw "Unsupported current test platform '$CurrentPlatform'."
    }

    foreach ($project in @($Projects)) {
        $projectPath = if ($project -is [System.IO.FileSystemInfo]) {
            $project.FullName
        }
        else {
            [string]$project
        }
        $projectItem = Get-Item -LiteralPath $projectPath
        if (Test-DownKyiTestProjectSupportsPlatform `
                -ProjectPath $projectItem.FullName `
                -Platform $CurrentPlatform) {
            $projectItem
        }
    }
}

function Get-DownKyiTestRunnerPolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$ProjectPath
    )

    $policyPath = Join-Path $RepositoryRoot "docs/testing/test-runner-policy.json"
    if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) {
        throw "Test runner policy is missing: $policyPath"
    }

    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    if ($policy.schemaVersion -ne 1) {
        throw "Unsupported test runner policy schema: $($policy.schemaVersion)"
    }

    $relativeProject = [IO.Path]::GetRelativePath($RepositoryRoot, $ProjectPath).Replace('\', '/')
    $matches = @($policy.projects | Where-Object { $_.project -eq $relativeProject })
    if ($matches.Count -gt 1) {
        throw "Test runner policy contains duplicate entries for $relativeProject."
    }

    if ($matches.Count -eq 0) {
        return $null
    }

    $entry = $matches[0]
    if ($entry.runner -ne "xunit-in-process" -or
        [string]::IsNullOrWhiteSpace($entry.targetFramework) -or
        $entry.parallel -ne "none" -or
        [string]::IsNullOrWhiteSpace($entry.reason)) {
        throw "Test runner policy is incomplete for $relativeProject."
    }

    return $entry
}

function Assert-DownKyiExpectedTestExecution {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [int]$RunnerExitCode,

        [Parameter(Mandatory)]
        [string]$TrxPath,

        [Parameter(Mandatory)]
        [string[]]$ExpectedClassNames
    )

    if ($RunnerExitCode -ne 0) {
        throw "The test runner failed with exit code $RunnerExitCode."
    }

    if ($ExpectedClassNames.Count -eq 0 -or
        @($ExpectedClassNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "At least one non-empty expected test class is required."
    }

    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw "The expected test report is missing: $TrxPath"
    }

    $report = Get-Item -LiteralPath $TrxPath
    $reports = @(Get-ChildItem -LiteralPath $report.DirectoryName -Filter *.trx -File)
    if ($reports.Count -ne 1 -or
        -not [IO.Path]::GetFullPath($reports[0].FullName).Equals(
            [IO.Path]::GetFullPath($report.FullName),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The test result directory must contain exactly the expected TRX report."
    }

    try {
        [xml]$trx = Get-Content -LiteralPath $report.FullName -Raw -ErrorAction Stop
    }
    catch {
        throw "The expected test report is malformed: $($report.FullName)"
    }

    $counters = $trx.SelectSingleNode(
        "/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    $results = @($trx.SelectNodes(
            "/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']"))
    $definitions = @($trx.SelectNodes(
            "/*[local-name()='TestRun']/*[local-name()='TestDefinitions']/*[local-name()='UnitTest']"))
    if ($null -eq $counters -or $results.Count -eq 0 -or $definitions.Count -eq 0) {
        throw "The expected test report has an incomplete result structure."
    }

    $counterValues = @{}
    foreach ($counterName in @("total", "executed", "passed", "failed")) {
        $counterValue = 0
        if (-not [int]::TryParse(
                $counters.GetAttribute($counterName),
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$counterValue) -or $counterValue -lt 0) {
            throw "The expected test report has invalid execution counters."
        }

        $counterValues.Add($counterName, $counterValue)
    }

    $executed = [int]$counterValues.executed
    if ($executed -lt 1) {
        throw "The expected test selection executed no tests."
    }
    if ($executed -gt [int]$counterValues.total -or
        [int]$counterValues.passed + [int]$counterValues.failed -gt $executed) {
        throw "The expected test report has inconsistent execution counters."
    }

    $definitionsById = @{}
    foreach ($definition in $definitions) {
        $testId = $definition.GetAttribute("id")
        $testMethod = $definition.SelectSingleNode("./*[local-name()='TestMethod']")
        if ([string]::IsNullOrWhiteSpace($testId) -or $null -eq $testMethod) {
            throw "The expected test report contains an invalid test definition."
        }

        $className = $testMethod.GetAttribute("className")
        if ([string]::IsNullOrWhiteSpace($className) -or $definitionsById.ContainsKey($testId)) {
            throw "The expected test report contains an ambiguous test definition."
        }

        $definitionsById.Add($testId, $className)
    }

    foreach ($result in $results) {
        $testId = $result.GetAttribute("testId")
        if ([string]::IsNullOrWhiteSpace($testId) -or
            -not $definitionsById.ContainsKey($testId) -or
            [string]::IsNullOrWhiteSpace($result.GetAttribute("outcome"))) {
            throw "The expected test report contains an invalid execution result."
        }
    }

    $executedExpectedTests = @($results | Where-Object {
            $testId = $_.GetAttribute("testId")
            -not [string]::IsNullOrWhiteSpace($testId) -and
            $definitionsById.ContainsKey($testId) -and
            $ExpectedClassNames.Contains([string]$definitionsById[$testId]) -and
            $_.GetAttribute("outcome") -eq "Passed"
        })
    if ($executedExpectedTests.Count -lt 1) {
        throw "The report contains no executed result for an expected test class."
    }

    return [pscustomobject]@{
        Executed = $executed
        ExecutedExpected = $executedExpectedTests.Count
        ReportPath = $report.FullName
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

        [string]$Filter
    )

    $project = Get-Item -LiteralPath $ProjectPath
    $currentPlatform = Get-DownKyiCurrentTestPlatform
    if (-not (Test-DownKyiTestProjectSupportsPlatform `
                -ProjectPath $project.FullName `
                -Platform $currentPlatform)) {
        $projectPlatforms = @(Get-DownKyiTestProjectPlatforms -ProjectPath $project.FullName)
        throw (
            "Test project $($project.FullName) supports " +
            "[$($projectPlatforms -join ', ')] and cannot run on '$currentPlatform'.")
    }

    $runnerPolicy = Get-DownKyiTestRunnerPolicy `
        -RepositoryRoot $RepositoryRoot `
        -ProjectPath $project.FullName

    if ($null -eq $runnerPolicy) {
        $arguments = @(
            "test",
            $project.FullName,
            "-c",
            $Configuration,
            "-p:DownKyiCentralTestRunner=true"
        )
        if ($NoRestore) {
            $arguments += "--no-restore"
        }
        if ($NoBuild) {
            $arguments += "--no-build"
        }

        $effectiveFilter = $Filter
        if ([string]::IsNullOrWhiteSpace($effectiveFilter) -and $ClassNames.Count -gt 0) {
            $effectiveFilter = ($ClassNames | Sort-Object -Unique | ForEach-Object {
                    "FullyQualifiedName~$_"
                }) -join "|"
        }
        if (-not [string]::IsNullOrWhiteSpace($effectiveFilter)) {
            $arguments += @("--filter", $effectiveFilter)
        }

        $trxPath = $null
        if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
            New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
            $resolvedTrxName = if ([string]::IsNullOrWhiteSpace($TrxName)) {
                "$($project.BaseName).trx"
            }
            else {
                $TrxName
            }
            $trxPath = Join-Path $ResultsDirectory $resolvedTrxName
            $arguments += @(
                "--logger", "trx;LogFileName=$resolvedTrxName",
                "--results-directory", $ResultsDirectory
            )
        }

        & dotnet @arguments | Out-Host
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Runner = "vstest-adapter"
            TrxPath = $trxPath
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        throw "The xUnit in-process runner requires class locators instead of a VSTest filter: $($project.FullName)"
    }

    if (-not $NoBuild) {
        $buildArguments = @("build", $project.FullName, "-c", $Configuration)
        if ($NoRestore) {
            $buildArguments += "--no-restore"
        }
        & dotnet @buildArguments | Out-Host
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{
                ExitCode = $LASTEXITCODE
                Runner = $runnerPolicy.runner
                TrxPath = $null
            }
        }
    }

    $assemblyPath = Join-Path $project.Directory.FullName (
        "bin/{0}/{1}/{2}.dll" -f $Configuration, $runnerPolicy.targetFramework, $project.BaseName)
    if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
        throw "The xUnit in-process test assembly is missing: $assemblyPath"
    }

    $arguments = @(
        $assemblyPath,
        "-noLogo",
        "-noColor",
        "-noAutoReporters",
        "-reporter", "quiet",
        "-parallel", $runnerPolicy.parallel
    )
    foreach ($className in @($ClassNames | Sort-Object -Unique)) {
        $arguments += @("-class", $className)
    }

    $trxPath = $null
    if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
        New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
        $resolvedTrxName = if ([string]::IsNullOrWhiteSpace($TrxName)) {
            "$($project.BaseName).trx"
        }
        else {
            $TrxName
        }
        $trxPath = Join-Path $ResultsDirectory $resolvedTrxName
        $arguments += @("-trx", $trxPath)
    }

    & dotnet @arguments | Out-Host
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Runner = $runnerPolicy.runner
        TrxPath = $trxPath
    }
}
