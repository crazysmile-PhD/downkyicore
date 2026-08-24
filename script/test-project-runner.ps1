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
        throw "Test runner policy has no entry for $relativeProject."
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

function Get-DownKyiTestRunnerTrustInputs {
    [CmdletBinding()]
    param()

    return @(
        ".github/actions/test-project/action.yml"
        ".github/actions/test-solution/action.yml"
        "Directory.Build.props"
        "Directory.Build.targets"
        "Directory.Packages.props"
        "global.json"
        "docs/testing/test-runner-policy.json"
        "tests/CentralTestExecutionGuard.cs"
    )
}

function New-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $executableName = [IO.Path]::GetFileName($StartInfo.FileName)
    if ($executableName -notin @("dotnet", "dotnet.exe") -or
        $StartInfo.ArgumentList.Count -lt 1) {
        throw "Authorized repository test execution requires dotnet with a test assembly as its first argument."
    }

    $requestedAssembly = [IO.Path]::GetFullPath(
        [string]$StartInfo.ArgumentList[0],
        $StartInfo.WorkingDirectory)
    $policyPath = Join-Path $RepositoryRoot "docs/testing/test-runner-policy.json"
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $ownedAssemblies = @(
        foreach ($entry in $policy.projects) {
            $projectPath = [IO.Path]::GetFullPath(
                [string]$entry.project,
                $RepositoryRoot)
            $projectDirectory = Split-Path -Parent $projectPath
            $assemblyName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
            foreach ($configuration in @("Debug", "Release")) {
                [IO.Path]::GetFullPath(
                    "bin/$configuration/$($entry.targetFramework)/$assemblyName.dll",
                    $projectDirectory)
            }
        }
    )
    if (-not $ownedAssemblies.Contains($requestedAssembly)) {
        throw "The requested process is not a policy-owned repository test assembly: $requestedAssembly"
    }

    $pipe = [IO.Pipes.AnonymousPipeServerStream]::new(
        [IO.Pipes.PipeDirection]::Out,
        [IO.HandleInheritability]::Inheritable)
    $token = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $StartInfo.Environment["DOWNKYI_CENTRAL_TEST_PIPE"] = $pipe.GetClientHandleAsString()
    $StartInfo.Environment["DOWNKYI_CENTRAL_TEST_TOKEN"] = [Convert]::ToBase64String($token)
    return [pscustomobject]@{
        Pipe = $pipe
        Token = $token
        Completed = $false
    }
}

function Complete-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Authorization
    )

    if ($Authorization.Completed) {
        throw "Repository test process authorization was already completed."
    }

    $Authorization.Pipe.DisposeLocalCopyOfClientHandle()
    try {
        $Authorization.Pipe.Write($Authorization.Token, 0, $Authorization.Token.Length)
        $Authorization.Pipe.Flush()
        $Authorization.Completed = $true
    }
    finally {
        $Authorization.Pipe.Dispose()
    }
}

function Close-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [object]$Authorization
    )

    if ($null -ne $Authorization -and -not $Authorization.Completed) {
        $Authorization.Pipe.Dispose()
    }
}

function Invoke-DownKyiAuthorizedTestAssembly {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.WorkingDirectory = $RepositoryRoot
    $startInfo.UseShellExecute = $false
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $authorization = New-DownKyiTestProcessAuthorization `
        -StartInfo $startInfo `
        -RepositoryRoot $RepositoryRoot
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "The authorized repository test process did not start."
        }

        Complete-DownKyiTestProcessAuthorization -Authorization $authorization
        $process.WaitForExit()
        return $process.ExitCode
    }
    finally {
        Close-DownKyiTestProcessAuthorization -Authorization $authorization
        $process.Dispose()
    }
}

function Assert-DownKyiTestExecutionReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TrxPath,

        [string[]]$ExpectedClassNames = @(),

        [switch]$RequireUniqueReport
    )

    if (@($ExpectedClassNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "Expected test class names cannot be empty."
    }

    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw "The expected test report is missing: $TrxPath"
    }

    $report = Get-Item -LiteralPath $TrxPath
    if ($RequireUniqueReport) {
        $reports = @(Get-ChildItem -LiteralPath $report.DirectoryName -Filter *.trx -File)
        if ($reports.Count -ne 1 -or
            -not [IO.Path]::GetFullPath($reports[0].FullName).Equals(
                [IO.Path]::GetFullPath($report.FullName),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The test result directory must contain exactly the expected TRX report."
        }
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
        $outcome = $result.GetAttribute("outcome")
        if ([string]::IsNullOrWhiteSpace($testId) -or
            -not $definitionsById.ContainsKey($testId) -or
            -not @("Passed", "Failed", "NotExecuted").Contains($outcome)) {
            throw "The expected test report contains an invalid execution result."
        }
    }

    $passedResults = @($results | Where-Object { $_.GetAttribute("outcome") -eq "Passed" })
    $failedResults = @($results | Where-Object { $_.GetAttribute("outcome") -eq "Failed" })
    $executedResults = @($results | Where-Object { $_.GetAttribute("outcome") -ne "NotExecuted" })
    if ($results.Count -ne [int]$counterValues.total -or
        $executedResults.Count -ne $executed -or
        $passedResults.Count -ne [int]$counterValues.passed -or
        $failedResults.Count -ne [int]$counterValues.failed) {
        throw "The expected test report counters do not match its execution results."
    }

    $executedExpectedTests = @($results | Where-Object {
            $testId = $_.GetAttribute("testId")
            -not [string]::IsNullOrWhiteSpace($testId) -and
            $definitionsById.ContainsKey($testId) -and
            $ExpectedClassNames.Contains([string]$definitionsById[$testId]) -and
            $_.GetAttribute("outcome") -ne "NotExecuted"
        })
    $passedExpectedTests = @($executedExpectedTests | Where-Object {
            $_.GetAttribute("outcome") -eq "Passed"
        })
    if ($ExpectedClassNames.Count -gt 0 -and $executedExpectedTests.Count -lt 1) {
        throw "The report contains no executed result for an expected test class."
    }

    return [pscustomobject]@{
        Executed = $executed
        ExecutedExpected = $executedExpectedTests.Count
        PassedExpected = $passedExpectedTests.Count
        Failed = [int]$counterValues.failed
        ReportPath = $report.FullName
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
        [string[]]$ExpectedClassNames
    )

    if ($ExpectedClassNames.Count -eq 0) {
        throw "At least one expected test class is required."
    }
    if ($RunnerExitCode -ne 0) {
        throw "The test runner failed with exit code $RunnerExitCode."
    }

    $report = Assert-DownKyiTestExecutionReport `
        -TrxPath $TrxPath `
        -ExpectedClassNames $ExpectedClassNames `
        -RequireUniqueReport
    if ($report.Failed -gt 0) {
        throw "A successful runner report cannot contain failed test results."
    }
    if ($report.PassedExpected -lt 1) {
        throw "The report contains no passed result for an expected test class."
    }

    return $report
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

    $temporaryResultsDirectory = $null
    $reportedTrxPath = $null
    $validationResultsDirectory = $ResultsDirectory
    if ([string]::IsNullOrWhiteSpace($validationResultsDirectory)) {
        $temporaryResultsDirectory = Join-Path (
            [IO.Path]::GetTempPath()) "downkyi-test-$([Guid]::NewGuid().ToString('N'))"
        $validationResultsDirectory = $temporaryResultsDirectory
    }
    else {
        $resolvedTrxName = if ([string]::IsNullOrWhiteSpace($TrxName)) {
            "$($project.BaseName).trx"
        }
        else {
            $TrxName
        }
        $reportedTrxPath = Join-Path $validationResultsDirectory $resolvedTrxName
    }

    New-Item -ItemType Directory -Force -Path $validationResultsDirectory | Out-Null
    $validationTrxPath = if ($null -ne $reportedTrxPath) {
        $reportedTrxPath
    }
    else {
        Join-Path $validationResultsDirectory "$($project.BaseName).trx"
    }
    if (Test-Path -LiteralPath $validationTrxPath) {
        Remove-Item -LiteralPath $validationTrxPath -Force
    }
    $arguments += @("-trx", $validationTrxPath)

    try {
        $exitCode = Invoke-DownKyiAuthorizedTestAssembly `
            -RepositoryRoot $RepositoryRoot `
            -Arguments $arguments
        $report = Assert-DownKyiTestExecutionReport `
            -TrxPath $validationTrxPath `
            -ExpectedClassNames $ClassNames
        if ($exitCode -eq 0 -and $report.Failed -gt 0) {
            throw "A successful runner report cannot contain failed test results."
        }
        return [pscustomobject]@{
            ExitCode = $exitCode
            Runner = $runnerPolicy.runner
            TrxPath = $reportedTrxPath
        }
    }
    finally {
        if ($null -ne $temporaryResultsDirectory -and
            (Test-Path -LiteralPath $temporaryResultsDirectory)) {
            Remove-Item -LiteralPath $temporaryResultsDirectory -Recurse -Force
        }
    }
}
