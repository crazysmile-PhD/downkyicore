function Resolve-DiagnosticsTool {
    if (-not [string]::IsNullOrWhiteSpace($DiagnosticsToolPath)) {
        $resolved = [System.IO.Path]::GetFullPath($DiagnosticsToolPath, (Get-Location).Path)
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }

        throw "Diagnostics tool was not found: $resolved"
    }

    $localNames = if ($IsWindows) {
        @("dotnet-stack.exe", "dotnet-stack")
    }
    else {
        @("dotnet-stack")
    }
    foreach ($name in $localNames) {
        $candidate = Join-Path $repositoryRoot ".tools/$name"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    $command = Get-Command "dotnet-stack" -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }

    return $command.Source
}

function Protect-ProcessDiagnosticText {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $protected = $Value
    $pathAliases = @(
        [pscustomobject]@{ path = $repositoryRoot; alias = "<repository>" }
        [pscustomobject]@{
            path = [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::UserProfile)
            alias = "<user-profile>"
        }
        [pscustomobject]@{
            path = [System.IO.Path]::GetTempPath().TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            alias = "<temp>"
        }
    )
    foreach ($pathAlias in $pathAliases) {
        if (-not [string]::IsNullOrWhiteSpace($pathAlias.path)) {
            $protected = $protected.Replace(
                $pathAlias.path,
                $pathAlias.alias,
                [StringComparison]::OrdinalIgnoreCase)
        }
    }

    $protected = $protected -replace '(?i)https?://\S+', '<url>'
    $protected = $protected -replace (
        '(?i)(SESSDATA|bili_jct|DedeUserID|cookie|token|secret)' +
        '\s*[:=]\s*(?:"[^"]*"|''[^'']*''|[^\s;]+)'),
        '$1=<redacted>'
    $protected = $protected -replace (
        '(?i)(--(?:rpc-)?secret|--?cookie|--?token|SESSDATA|bili_jct|' +
        'DedeUserID)\s+(?:"[^"]*"|''[^'']*''|\S+)'),
        '$1 <redacted>'
    return $protected
}

function New-OwnedProcessProof {
    param(
        [Parameter(Mandatory)]
        [DownKyi.ProcessSupervision.OwnedProcessOutcome]$Outcome
    )

    return [pscustomobject]@{
        formalGatePassed = $Outcome.FormalGatePassed
        invariants = @(
            $Outcome.Invariants | ForEach-Object {
                [pscustomobject]@{
                    kind = $_.Kind.ToString()
                    state = $_.State.ToString()
                }
            }
        )
        failures = @(
            $Outcome.Failures | ForEach-Object {
                [pscustomobject]@{
                    kind = $_.Kind.ToString()
                    phase = $_.Phase.ToString()
                    channel = $_.Channel.ToString()
                    errorType = $_.ErrorType
                    message = $_.Message
                }
            }
        )
        facts = @(
            $Outcome.Facts | ForEach-Object {
                [pscustomobject]@{
                    kind = $_.Kind.ToString()
                    phase = $_.Phase.ToString()
                    detail = $_.Detail
                }
            }
        )
        ownership = [pscustomobject]@{
            identityAuthority = $Outcome.Ownership.IdentityAuthority.ToString()
            containmentKind = $Outcome.Ownership.ContainmentKind.ToString()
            containmentStrength = $Outcome.Ownership.ContainmentStrength.ToString()
            membershipAuthority = $Outcome.Ownership.MembershipAuthority.ToString()
            containmentId = $Outcome.Ownership.ContainmentId
            membershipId = $Outcome.Ownership.MembershipId
            ownerLifetimeId = $Outcome.Ownership.OwnerLifetimeId
            ownershipEstablished = $Outcome.Ownership.OwnershipEstablished
        }
    }
}

function Invoke-OwnedDiagnosticCollector {
    param(
        [Parameter(Mandatory)]
        [string]$FileName,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$Destination,
        [Parameter(Mandatory)]
        [string]$ProofDestination
    )

    $environment = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $launchSpec = [DownKyi.ProcessSupervision.LaunchSpec]::new(
        $FileName,
        $Arguments,
        $repositoryRoot,
        $environment,
        $true)
    $budget = [DownKyi.ProcessSupervision.TransitionBudget]::Start(
        [TimeSpan]::FromSeconds(15),
        [TimeSpan]::FromSeconds($processCleanupGraceSeconds))
    $containmentRequirement = [Enum]::Parse(
        [DownKyi.ProcessSupervision.ProcessContainmentRequirement],
        $processContainmentRequirement)
    $lease = $null
    $ownedOutcome = $null
    $collectorErrorType = $null
    try {
        try {
            $lease = [DownKyi.ProcessSupervision.OwnedProcessLease]::StartAsync(
                $launchSpec,
                $budget,
                $containmentRequirement).GetAwaiter().GetResult()
            try {
                $ownedOutcome = $lease.WaitAsync().GetAwaiter().GetResult()
            }
            catch [DownKyi.ProcessSupervision.OwnedProcessExecutionException] {
                $ownedOutcome = $_.Exception.Outcome
            }
        }
        catch [DownKyi.ProcessSupervision.OwnedProcessExecutionException] {
            $ownedOutcome = $_.Exception.Outcome
        }
        catch {
            $collectorErrorType = $_.Exception.GetType().Name
        }
    }
    finally {
        if ($null -ne $lease) {
            try {
                [void]($lease.DisposeAsync().AsTask().GetAwaiter().GetResult())
            }
            catch [DownKyi.ProcessSupervision.OwnedProcessExecutionException] {
                if ($null -eq $ownedOutcome) {
                    $ownedOutcome = $_.Exception.Outcome
                }
            }
            catch {
                if ($null -eq $collectorErrorType) {
                    $collectorErrorType = $_.Exception.GetType().Name
                }
            }
        }
    }

    if ($null -eq $ownedOutcome) {
        Set-Content -LiteralPath $Destination -Encoding utf8 -Value (
            "Diagnostic collector failed: $collectorErrorType")
        return [pscustomobject]@{
            available = $true
            captured = $false
            exitCode = $null
            formalGatePassed = $false
            errorType = $collectorErrorType
            proofPath = $null
        }
    }

    [System.IO.File]::WriteAllText(
        $Destination,
        $ownedOutcome.StandardOutput + $ownedOutcome.StandardError,
        [System.Text.UTF8Encoding]::new($false))
    $proof = New-OwnedProcessProof -Outcome $ownedOutcome
    [System.IO.File]::WriteAllText(
        $ProofDestination,
        ($proof | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    if ($null -eq $collectorErrorType -and -not $ownedOutcome.FormalGatePassed) {
        $collectorErrorType = "OwnedProcessInvariantFailure"
    }
    elseif ($null -eq $collectorErrorType -and $ownedOutcome.ExitCode -ne 0) {
        $collectorErrorType = "DiagnosticCollectorExitCode"
    }
    elseif ($null -eq $collectorErrorType -and
        [string]::IsNullOrWhiteSpace($ownedOutcome.StandardOutput)) {
        $collectorErrorType = "DiagnosticCollectorOutputMissing"
    }
    return [pscustomobject]@{
        available = $true
        captured = $ownedOutcome.FormalGatePassed -and
            $ownedOutcome.ExitCode -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($ownedOutcome.StandardOutput)
        exitCode = $ownedOutcome.ExitCode
        formalGatePassed = $ownedOutcome.FormalGatePassed
        errorType = $collectorErrorType
        proofPath = [System.IO.Path]::GetFileName($ProofDestination)
    }
}

function Save-ProcessEvidence {
    param(
        [Parameter(Mandatory)]
        [int]$TargetProcessId,
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string]$Reason
    )

    $safeReason = $Reason -replace '[^A-Za-z0-9_.-]', '-'
    $directory = Join-Path $evidenceRoot (
        "$AssemblyName/iteration-{0:D4}/{1}-{2}" -f $Iteration, $Phase, $safeReason)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $stackResult = if ([string]::IsNullOrWhiteSpace($script:diagnosticsTool)) {
        Set-Content -LiteralPath (Join-Path $directory "managed-stack.txt") `
            -Encoding utf8 `
            -Value "dotnet-stack is unavailable. Install it in .tools to capture managed stacks."
        [pscustomobject]@{
            available = $false
            captured = $false
            exitCode = $null
            formalGatePassed = $null
            errorType = "DiagnosticToolUnavailable"
            proofPath = $null
        }
    }
    else {
        Invoke-OwnedDiagnosticCollector `
            -FileName $script:diagnosticsTool `
            -Arguments @(
                "report"
                "--process-id"
                $TargetProcessId.ToString(
                    [System.Globalization.CultureInfo]::InvariantCulture)
            ) `
            -Destination (Join-Path $directory "managed-stack.txt") `
            -ProofDestination (Join-Path $directory "managed-stack.owned-process.json")
    }
    $evidence = [ordered]@{
        schemaVersion = 2
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        reason = $Reason
        targetProcessId = $TargetProcessId
        captureMode = "owned-diagnostic-collector"
        diagnosticOnly = $true
        lifecycleAuthority = "OwnedProcessLease"
        managedStack = $stackResult
    }
    $evidence |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $directory "process-evidence.json") -Encoding utf8
    return [System.IO.Path]::GetRelativePath($runRoot, $directory).
        Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

function Get-LifecycleMarkerReadFailureCategory {
    param(
        [Parameter(Mandatory)]
        [System.Exception]$Exception
    )

    if ($IsWindows -and $Exception -is [System.IO.IOException]) {
        $nativeErrorCode = $Exception.HResult -band 0xFFFF
        if ($nativeErrorCode -in @(32, 33)) {
            return "contention"
        }
    }

    return "error"
}

function Read-TeardownMarker {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [ValidateRange(1, 20)]
        [int]$Attempts = 4,
        [ValidateRange(0, 1000)]
        [int]$RetryDelayMilliseconds = 5
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $lines = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
            $stream = [System.IO.FileStream]::new(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                $share)
            try {
                $reader = [System.IO.StreamReader]::new($stream)
                try {
                    $lines = @($reader.ReadToEnd() -split '\r?\n')
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }

            break
        }
        catch [System.IO.IOException] {
            if ((Get-LifecycleMarkerReadFailureCategory -Exception $_.Exception) -eq
                "contention") {
                $script:markerReadContentionCount++
            }
            else {
                $script:markerReadErrorCount++
                $script:markerReadErrorType = $_.Exception.GetType().Name
            }
        }
        catch [System.UnauthorizedAccessException] {
            $script:markerReadErrorCount++
            $script:markerReadErrorType = $_.Exception.GetType().Name
        }

        if ($attempt -lt $Attempts -and $RetryDelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $RetryDelayMilliseconds
        }
    }

    if ($null -eq $lines) {
        $script:markerReadRetriesExhaustedCount++
        return $null
    }

    $states = @()
    foreach ($line in $lines) {
        if ($line -match '^(started|disposing|disposed)\|(\d+)\|(\d+)$') {
            $states += [pscustomobject]@{
                state = $Matches[1]
                processId = [int]$Matches[2]
                timestamp = [long]$Matches[3]
            }
        }
    }

    $started = @($states | Where-Object state -eq "started" | Select-Object -Last 1)
    $disposing = @($states | Where-Object state -eq "disposing" | Select-Object -Last 1)
    $disposed = @($states | Where-Object state -eq "disposed" | Select-Object -Last 1)
    return [pscustomobject]@{
        states = $states
        started = if ($started.Count -eq 0) { $null } else { $started[0] }
        disposing = if ($disposing.Count -eq 0) { $null } else { $disposing[0] }
        disposed = if ($disposed.Count -eq 0) { $null } else { $disposed[0] }
    }
}

function Invoke-AssemblyLifecycleForensicsSelfTests {
    [CmdletBinding()]
    param(
        [switch]$ValidateForensics,
        [Parameter(Mandatory)]
        [object[]]$TestProjects,
        [Parameter(Mandatory)]
        [string]$Configuration,
        [Parameter(Mandatory)]
        [string]$ProbeAssembly,
        [Parameter(Mandatory)]
        [string]$RawRoot,
        [Parameter(Mandatory)]
        [string]$RunRoot,
        [Parameter(Mandatory)]
        [string]$EvidenceRoot,
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [int]$PhaseTimeoutSeconds,
        [Parameter(Mandatory)]
        [double]$SlowPhaseThresholdSeconds,
        [Parameter(Mandatory)]
        [double]$ExitThresholdSeconds,
        [Parameter(Mandatory)]
        [int]$SlowEvidenceCaptureLeadMilliseconds,
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$MarkerReaderSelfTest
    )

    $phaseResults = @()
    $forensicsSelfTestCaptureLeadValidated = $false
    $markerReaderSelfTestComplete = $false
    if (-not $ValidateForensics) {
        return [pscustomobject]@{
            phaseResults = @()
            forensicsSelfTestCaptureLeadValidated = $false
            markerReaderSelfTestComplete = $false
        }
    }

    $selfTestAssembly = Join-Path $testProjects[0].DirectoryName (
        "bin/$Configuration/net10.0/$($testProjects[0].BaseName).dll")
    $selfTestMarker = Join-Path $rawRoot "Gate.Forensics/iteration-0001/execution.lifecycle"
    $selfTest = Invoke-IsolatedProcess `
        -AssemblyName "Gate.Forensics" `
        -Iteration 1 `
        -Phase "execution" `
        -FileName "dotnet" `
        -Arguments @(
            $probeAssembly,
            "--assembly",
            $selfTestAssembly,
            "--hold-after-unload-ms",
            "5000"
        ) `
        -LifecycleMarkerPath $selfTestMarker `
        -EvidenceThresholdSeconds 1.25
    $selfTestPhase = New-ProcessPhaseResult -ProcessResult $selfTest
    $evidenceReports = @(
        foreach ($relativeEvidence in $selfTest.evidence) {
            $evidencePath = Join-Path $runRoot $relativeEvidence "process-evidence.json"
            if (Test-Path -LiteralPath $evidencePath -PathType Leaf) {
                Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
            }
        }
    )
    $forensicsClassification = New-ForensicsSelfTestPhaseResult `
        -SelfTest $selfTest `
        -SelfTestPhase $selfTestPhase `
        -EvidenceReports $evidenceReports
    $forensicsSelfTestCaptureLeadValidated =
        $forensicsClassification.captureLeadValidated
    $phaseResults += $forensicsClassification.phaseResult

    if ($IsWindows) {
        $markerReaderSelfTestStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $markerReaderSelfTest.executed = $true
        $markerReaderTestPath = Join-Path $rawRoot "Gate.MarkerReader/read-race.lifecycle"
        $contentionBaseline = $script:markerReadContentionCount
        $lockedMarker = $null
        $exclusiveStream = $null
        try {
            New-Item -ItemType Directory -Force `
                -Path ([System.IO.Path]::GetDirectoryName($markerReaderTestPath)) |
                Out-Null
            @(
                "started|123|1000"
                "disposing|123|1001"
                "disposed|123|1002"
            ) | Set-Content -LiteralPath $markerReaderTestPath -Encoding utf8
            $exclusiveStream = [System.IO.FileStream]::new(
                $markerReaderTestPath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            try {
                $lockedMarker = Read-TeardownMarker `
                    -Path $markerReaderTestPath `
                    -Attempts 2 `
                    -RetryDelayMilliseconds 1
            }
            finally {
                $exclusiveStream.Dispose()
                $exclusiveStream = $null
            }

            $contentionCount =
                $script:markerReadContentionCount - $contentionBaseline
            $unlockedMarker = Read-TeardownMarker -Path $markerReaderTestPath
            Set-MarkerReaderSelfTestObservations `
                -SelfTest $markerReaderSelfTest `
                -LockedMarker $lockedMarker `
                -UnlockedMarker $unlockedMarker `
                -ContentionCount $contentionCount
        }
        catch {
            $markerReaderSelfTest.errorType = $_.Exception.GetType().Name
        }
        finally {
            if ($null -ne $exclusiveStream) {
                $exclusiveStream.Dispose()
            }

            $markerReaderSelfTestStopwatch.Stop()
        }

        $markerReaderClassification =
            Complete-MarkerReaderSelfTestClassification `
                -SelfTest $markerReaderSelfTest
        $markerReaderSelfTestComplete = $markerReaderClassification.complete
        $phaseResults += New-MarkerReaderSelfTestPhaseResult `
            -SelfTest $markerReaderSelfTest `
            -Complete $markerReaderSelfTestComplete `
            -ErrorType $markerReaderClassification.errorType `
            -Stopwatch $markerReaderSelfTestStopwatch

        $script:markerReadContentionCount = 0
        $script:markerReadRetriesExhaustedCount = 0
        $script:markerReadErrorCount = 0
        $script:markerReadErrorType = $null
    }

    return [pscustomobject]@{
        phaseResults = @($phaseResults)
        forensicsSelfTestCaptureLeadValidated =
            $forensicsSelfTestCaptureLeadValidated
        markerReaderSelfTestComplete = $markerReaderSelfTestComplete
    }
}
