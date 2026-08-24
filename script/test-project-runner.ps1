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
        "script/invoke-ci-test-action.ps1"
        "Directory.Build.props"
        "Directory.Build.targets"
        "Directory.Packages.props"
        "global.json"
        "docs/testing/test-runner-policy.json"
        "tests/CentralTestExecutionGuard.cs"
    )
}

function Get-DownKyiTestInvocationHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new(
        $stream,
        [Text.UTF8Encoding]::new($false),
        $true)
    try {
        $writer.Write([int]$Arguments.Count)
        foreach ($argument in $Arguments) {
            $bytes = [Text.Encoding]::UTF8.GetBytes($argument)
            $writer.Write([int]$bytes.Length)
            $writer.Write($bytes)
        }
        $writer.Flush()
        return [Security.Cryptography.SHA256]::HashData($stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function New-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    if ($Arguments.Count -lt 1) {
        throw "Authorized repository test execution requires dotnet with a test assembly as its first argument."
    }

    $requestedAssembly = [IO.Path]::GetFullPath(
        $Arguments[0],
        $RepositoryRoot)
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
    $expectedArguments = [Collections.ObjectModel.ReadOnlyCollection[string]]::new(
        [string[]]@($Arguments))
    $contract = [Tuple]::Create(
        $expectedArguments,
        [Convert]::ToBase64String(
            (Get-DownKyiTestInvocationHash -Arguments $Arguments)),
        [Convert]::ToBase64String($token))
    $state = [pscustomobject]@{
        Pipe = $pipe
        ChildProcessId = $null
        Completed = $false
    }
    return [Tuple]::Create($contract, $state)
}

function Set-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Authorization,

        [Parameter(Mandatory)]
        [System.Diagnostics.ProcessStartInfo]$StartInfo
    )

    $executableName = [IO.Path]::GetFileName($StartInfo.FileName)
    $contract = $Authorization.Item1
    $state = $Authorization.Item2
    $expectedArguments = $contract.Item1
    $actualArguments = @($StartInfo.ArgumentList | ForEach-Object { [string]$_ })
    $argumentMismatch = $actualArguments.Count -ne $expectedArguments.Count
    if (-not $argumentMismatch) {
        for ($index = 0; $index -lt $actualArguments.Count; $index++) {
            if (-not [string]::Equals(
                    $actualArguments[$index],
                    $expectedArguments[$index],
                    [StringComparison]::Ordinal)) {
                $argumentMismatch = $true
                break
            }
        }
    }

    if ($executableName -notin @("dotnet", "dotnet.exe") -or $argumentMismatch) {
        throw "Repository test authorization does not match the complete invocation contract."
    }

    $StartInfo.Environment["DOWNKYI_CENTRAL_TEST_PIPE"] =
        $state.Pipe.GetClientHandleAsString()
    $StartInfo.Environment["DOWNKYI_CENTRAL_TEST_TOKEN"] =
        $contract.Item3
}

function Complete-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Authorization
    )

    $contract = $Authorization.Item1
    $state = $Authorization.Item2
    if ($state.Completed) {
        throw "Repository test process authorization was already completed."
    }

    $token = [Convert]::FromBase64String($contract.Item3)
    $invocationHash = [Convert]::FromBase64String($contract.Item2)
    $state.Pipe.DisposeLocalCopyOfClientHandle()
    try {
        $payload = [byte[]]::new(
            $token.Length + $invocationHash.Length)
        [Array]::Copy(
            $token,
            0,
            $payload,
            0,
            $token.Length)
        [Array]::Copy(
            $invocationHash,
            0,
            $payload,
            $token.Length,
            $invocationHash.Length)
        $state.Pipe.Write($payload, 0, $payload.Length)
        $state.Pipe.Flush()
        $state.Completed = $true
    }
    finally {
        $state.Pipe.Dispose()
    }
}

function Close-DownKyiTestProcessAuthorization {
    [CmdletBinding()]
    param(
        [object]$Authorization
    )

    if ($null -ne $Authorization -and -not $Authorization.Item2.Completed) {
        $Authorization.Item2.Pipe.Dispose()
    }
}

function Stop-DownKyiOwnedProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [bool]$Started,

        [ValidateRange(1, 30000)]
        [int]$TimeoutMilliseconds = 5000
    )

    if (-not $Started) {
        return
    }

    try {
        if ($Process.HasExited) {
            return
        }

        $Process.Kill($true)
    }
    catch {
        if ($Process.HasExited) {
            return
        }

        throw
    }
    if (-not $Process.WaitForExit($TimeoutMilliseconds)) {
        throw "Owned child process $($Process.Id) did not terminate within its deadline."
    }
}

function Wait-DownKyiOwnedProcessExit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory)]
        [bool]$Started,

        [Parameter(Mandatory)]
        [ValidateRange(1, 3600000)]
        [int]$TimeoutMilliseconds,

        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
    )

    if (-not $Started) {
        throw "Cannot wait for a repository test process that did not start."
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while (-not $Process.WaitForExit(50)) {
        if ($CancellationToken.IsCancellationRequested) {
            throw [OperationCanceledException]::new(
                "Repository test process execution was canceled.",
                $CancellationToken)
        }
        if ($stopwatch.ElapsedMilliseconds -ge $TimeoutMilliseconds) {
            throw [TimeoutException]::new(
                "Repository test process $($Process.Id) exceeded its " +
                "$TimeoutMilliseconds ms execution deadline.")
        }
    }

    if ($CancellationToken.IsCancellationRequested) {
        throw [OperationCanceledException]::new(
            "Repository test process execution was canceled.",
            $CancellationToken)
    }
    return $Process.ExitCode
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

    $expectedClassResults = [ordered]@{}
    foreach ($expectedClassName in @($ExpectedClassNames | Sort-Object -Unique)) {
        $classResults = @($results | Where-Object {
                $testId = $_.GetAttribute("testId")
                -not [string]::IsNullOrWhiteSpace($testId) -and
                $definitionsById.ContainsKey($testId) -and
                [string]$definitionsById[$testId] -eq $expectedClassName -and
                $_.GetAttribute("outcome") -ne "NotExecuted"
            })
        if ($classResults.Count -lt 1) {
            throw "The report contains no executed result for expected test class '$expectedClassName'."
        }

        $expectedClassResults.Add($expectedClassName, $classResults)
    }

    $executedExpectedTests = @(
        foreach ($classResults in $expectedClassResults.Values) {
            $classResults
        }
    )
    $passedExpectedTests = @($executedExpectedTests | Where-Object {
            $_.GetAttribute("outcome") -eq "Passed"
        })
    $passedExpectedClasses = @($expectedClassResults.Keys | Where-Object {
            @($expectedClassResults[$_] | Where-Object {
                    $_.GetAttribute("outcome") -eq "Passed"
                }).Count -gt 0
        })

    return [pscustomobject]@{
        Executed = $executed
        ExecutedExpected = $executedExpectedTests.Count
        ExecutedExpectedClasses = $expectedClassResults.Count
        PassedExpected = $passedExpectedTests.Count
        PassedExpectedClasses = $passedExpectedClasses.Count
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
    $expectedClassCount = @($ExpectedClassNames | Sort-Object -Unique).Count
    if ($report.PassedExpectedClasses -ne $expectedClassCount) {
        throw "Every expected test class must contain at least one passed result."
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

        [string]$Filter,

        [ValidateRange(1, 3600)]
        [int]$ExecutionTimeoutSeconds = 300,

        [Threading.CancellationToken]$CancellationToken =
            [Threading.CancellationToken]::None
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
        $authorization = New-DownKyiTestProcessAuthorization `
            -RepositoryRoot $RepositoryRoot `
            -Arguments $arguments
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = "dotnet"
        $startInfo.WorkingDirectory = $RepositoryRoot
        $startInfo.UseShellExecute = $false
        $null = $startInfo.Environment.Remove("DOWNKYI_LIFECYCLE_MARKER")
        foreach ($argument in $arguments) {
            $startInfo.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        $started = $false
        $operationFailure = $null
        $cleanupFailures = [Collections.Generic.List[Exception]]::new()
        $exitCode = $null
        try {
            Set-DownKyiTestProcessAuthorization `
                -Authorization $authorization `
                -StartInfo $startInfo
            if (-not $process.Start()) {
                throw "The authorized repository test process did not start."
            }
            $started = $true
            $authorization.Item2.ChildProcessId = $process.Id

            Complete-DownKyiTestProcessAuthorization -Authorization $authorization
            $exitCode = Wait-DownKyiOwnedProcessExit `
                -Process $process `
                -Started $started `
                -TimeoutMilliseconds ($ExecutionTimeoutSeconds * 1000) `
                -CancellationToken $CancellationToken
        }
        catch {
            $operationFailure = $_.Exception
        }
        finally {
            try {
                Stop-DownKyiOwnedProcess -Process $process -Started $started
            }
            catch {
                $cleanupFailures.Add($_.Exception)
            }
            try {
                Close-DownKyiTestProcessAuthorization -Authorization $authorization
            }
            catch {
                $cleanupFailures.Add($_.Exception)
            }
            try {
                $process.Dispose()
            }
            catch {
                $cleanupFailures.Add($_.Exception)
            }
        }

        if ($null -ne $operationFailure) {
            if ($cleanupFailures.Count -gt 0) {
                throw [AggregateException]::new(
                    "Repository test execution and child-process cleanup both failed.",
                    @($operationFailure) + $cleanupFailures.ToArray())
            }

            throw $operationFailure
        }
        if ($cleanupFailures.Count -gt 0) {
            throw [AggregateException]::new(
                "Repository test child-process cleanup failed.",
                $cleanupFailures.ToArray())
        }

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
