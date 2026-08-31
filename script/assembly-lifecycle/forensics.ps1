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

function Get-ProcessTree {
    param(
        [Parameter(Mandatory)]
        [int]$RootProcessId,
        [DateTimeOffset]$NotBeforeUtc = [DateTimeOffset]::MinValue
    )

    if ($IsWindows) {
        $pending = [System.Collections.Generic.Queue[object]]::new()
        $pending.Enqueue([pscustomobject]@{
            processId = $RootProcessId
            depth = 0
        })
        $visited = [System.Collections.Generic.HashSet[int]]::new()
        $result = @()
        while ($pending.Count -gt 0) {
            $parent = $pending.Dequeue()
            if (-not $visited.Add([int]$parent.processId)) {
                continue
            }

            $children = @(
                Get-CimInstance `
                    -ClassName Win32_Process `
                    -Filter "ParentProcessId = $($parent.processId)" `
                    -ErrorAction SilentlyContinue
            )
            foreach ($child in $children) {
                $creationTime = [DateTimeOffset]$child.CreationDate
                if ($creationTime -lt $NotBeforeUtc) {
                    continue
                }

                $result += [pscustomobject]@{
                    processId = [int]$child.ProcessId
                    parentProcessId = [int]$child.ParentProcessId
                    name = [string]$child.Name
                    createdAtUtc = $creationTime.ToUniversalTime().ToString("O")
                    depth = [int]$parent.depth + 1
                    executableName = if (
                        [string]::IsNullOrWhiteSpace([string]$child.ExecutablePath)
                    ) {
                        $null
                    }
                    else {
                        [System.IO.Path]::GetFileName([string]$child.ExecutablePath)
                    }
                    commandLine = Protect-ProcessDiagnosticText `
                        -Value ([string]$child.CommandLine)
                }
                $pending.Enqueue([pscustomobject]@{
                    processId = [int]$child.ProcessId
                    depth = [int]$parent.depth + 1
                })
            }
        }

        return $result
    }

    $rows = @(& ps -eo pid=,ppid=,comm= 2>$null)
    $processes = @(
        foreach ($row in $rows) {
            if ($row -match '^\s*(\d+)\s+(\d+)\s+(.+?)\s*$') {
                $observedProcess = Get-Process `
                    -Id ([int]$Matches[1]) `
                    -ErrorAction SilentlyContinue
                $createdAtUtc = $null
                if ($null -ne $observedProcess) {
                    try {
                        $createdAtUtc = (
                            [DateTimeOffset]$observedProcess.StartTime.ToUniversalTime()
                        ).ToString("O")
                    }
                    catch [System.InvalidOperationException] {
                        $createdAtUtc = $null
                    }
                    finally {
                        $observedProcess.Dispose()
                    }
                }

                [pscustomobject]@{
                    processId = [int]$Matches[1]
                    parentProcessId = [int]$Matches[2]
                    name = $Matches[3]
                    createdAtUtc = $createdAtUtc
                    depth = 0
                    executableName = $Matches[3]
                    commandLine = $null
                }
            }
        }
    )
    $pending = [System.Collections.Generic.Queue[int]]::new()
    $pending.Enqueue($RootProcessId)
    $result = @()
    while ($pending.Count -gt 0) {
        $parent = $pending.Dequeue()
        foreach ($child in @($processes | Where-Object { $_.parentProcessId -eq $parent })) {
            $result += $child
            $pending.Enqueue($child.processId)
        }
    }

    return $result
}

function Get-ProcessIdentityKey {
    param(
        [Parameter(Mandatory)]
        [object]$Process
    )

    return "{0}|{1}" -f $Process.processId, $Process.createdAtUtc
}

function Wait-ResidualProcessTree {
    param(
        [Parameter(Mandatory)]
        [int]$RootProcessId,
        [Parameter(Mandatory)]
        [DateTimeOffset]$NotBeforeUtc,
        [Parameter(Mandatory)]
        [ValidateRange(1, 5000)]
        [int]$QuiescenceMilliseconds,
        [Parameter(Mandatory)]
        [ValidateRange(1, 1000)]
        [int]$PollMilliseconds
    )

    $observed = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::Ordinal)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $sampleCount = 0
    $children = @()
    do {
        $children = @(
            Get-ProcessTree `
                -RootProcessId $RootProcessId `
                -NotBeforeUtc $NotBeforeUtc
        )
        $sampleCount++
        foreach ($child in $children) {
            $observed[(Get-ProcessIdentityKey -Process $child)] = $child
        }

        if ($children.Count -eq 0 -and $sampleCount -ge 2) {
            break
        }

        if ($stopwatch.ElapsedMilliseconds -ge $QuiescenceMilliseconds) {
            break
        }

        Start-Sleep -Milliseconds $PollMilliseconds
    }
    while ($true)
    $stopwatch.Stop()

    $residualKeys = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($child in $children) {
        $null = $residualKeys.Add((Get-ProcessIdentityKey -Process $child))
    }
    $transientChildren = @(
        foreach ($entry in $observed.GetEnumerator()) {
            if (-not $residualKeys.Contains($entry.Key)) {
                $entry.Value
            }
        }
    )

    return [pscustomobject]@{
        observedChildren = @($observed.Values)
        transientChildren = $transientChildren
        residualChildren = $children
        sampleCount = $sampleCount
        elapsedMilliseconds = [Math]::Round(
            $stopwatch.Elapsed.TotalMilliseconds,
            3)
    }
}

function Save-ManagedStack {
    param(
        [Parameter(Mandatory)]
        [int]$TargetProcessId,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    if ([string]::IsNullOrWhiteSpace($script:diagnosticsTool)) {
        Set-Content -LiteralPath $Destination -Encoding utf8 `
            -Value "dotnet-stack is unavailable. Install it in .tools to capture managed stacks."
        return [pscustomobject]@{
            available = $false
            captured = $false
            exitCode = $null
            timedOut = $false
        }
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:diagnosticsTool
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add("report")
    $startInfo.ArgumentList.Add("--process-id")
    $startInfo.ArgumentList.Add(
        $TargetProcessId.ToString([System.Globalization.CultureInfo]::InvariantCulture))
    $stackProcess = [System.Diagnostics.Process]::new()
    $stackProcess.StartInfo = $startInfo
    try {
        if (-not $stackProcess.Start()) {
            throw "dotnet-stack did not start."
        }

        $stdoutTask = $stackProcess.StandardOutput.ReadToEndAsync()
        $stderrTask = $stackProcess.StandardError.ReadToEndAsync()
        $timedOut = -not $stackProcess.WaitForExit(15000)
        if ($timedOut) {
            $stackProcess.Kill($true)
            $stackProcess.WaitForExit()
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [System.IO.File]::WriteAllText(
            $Destination,
            $stdout + $stderr,
            [System.Text.UTF8Encoding]::new($false))
        return [pscustomobject]@{
            available = $true
            captured = -not $timedOut -and
                $stackProcess.ExitCode -eq 0 -and
                -not [string]::IsNullOrWhiteSpace($stdout)
            exitCode = $stackProcess.ExitCode
            timedOut = $timedOut
        }
    }
    finally {
        $stackProcess.Dispose()
    }
}

function Save-ProcessEvidence {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [Parameter(Mandatory)]
        [string]$Phase,
        [Parameter(Mandatory)]
        [string]$Reason,
        [switch]$SkipManagedStack
    )

    $safeReason = $Reason -replace '[^A-Za-z0-9_.-]', '-'
    $directory = Join-Path $evidenceRoot (
        "$AssemblyName/iteration-{0:D4}/{1}-{2}" -f $Iteration, $Phase, $safeReason)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $threadSnapshot = @()
    if ($IsWindows -and -not $Process.HasExited) {
        try {
            $Process.Refresh()
            foreach ($thread in @($Process.Threads)) {
                $waitReason = $null
                if ($thread.ThreadState -eq [System.Diagnostics.ThreadState]::Wait) {
                    try {
                        $waitReason = $thread.WaitReason.ToString()
                    }
                    catch [System.InvalidOperationException] {
                        $waitReason = "unavailable"
                    }
                }

                $threadSnapshot += [pscustomobject]@{
                    id = $thread.Id
                    state = $thread.ThreadState.ToString()
                    waitReason = $waitReason
                    totalProcessorTimeMs = $thread.TotalProcessorTime.TotalMilliseconds
                }
            }
        }
        catch [System.InvalidOperationException] {
            $threadSnapshot = @()
        }
    }

    $processTree = @(Get-ProcessTree -RootProcessId $Process.Id)
    $stackResult = if ($Process.HasExited -or $SkipManagedStack) {
        [pscustomobject]@{
            available = $false
            captured = $false
            exitCode = $null
            timedOut = $false
        }
    }
    else {
        Save-ManagedStack `
            -TargetProcessId $Process.Id `
            -Destination (Join-Path $directory "managed-stack.txt")
    }
    $evidence = [ordered]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        reason = $Reason
        processId = $Process.Id
        processName = if ($Process.HasExited) { $null } else { $Process.ProcessName }
        hasExited = $Process.HasExited
        threads = $threadSnapshot
        processTree = $processTree
        managedStack = $stackResult
    }
    $evidence |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $directory "process-evidence.json") -Encoding utf8
    return [System.IO.Path]::GetRelativePath($runRoot, $directory).
        Replace([System.IO.Path]::DirectorySeparatorChar, '/')
}

function Save-ResidualChildEvidence {
    param(
        [Parameter(Mandatory)]
        [object[]]$Children,
        [Parameter(Mandatory)]
        [string]$AssemblyName,
        [Parameter(Mandatory)]
        [int]$Iteration,
        [Parameter(Mandatory)]
        [string]$Phase
    )

    $directory = Join-Path $evidenceRoot (
        "$AssemblyName/iteration-{0:D4}/{1}-residual-children" -f $Iteration, $Phase)
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $childEvidence = @()
    $captureErrors = @()
    foreach ($child in $Children) {
        $captureState = "exited-before-capture"
        $processEvidencePath = $null
        $captureErrorType = $null
        $childProcess = $null
        try {
            $childProcess = Get-Process `
                -Id $child.processId `
                -ErrorAction SilentlyContinue
            if ($null -eq $childProcess) {
                $captureState = "exited-before-capture"
            }
            else {
                $actualStart = [DateTimeOffset]$childProcess.StartTime.ToUniversalTime()
                $expectedStart = if (
                    [string]::IsNullOrWhiteSpace([string]$child.createdAtUtc)
                ) {
                    $actualStart
                }
                else {
                    [DateTimeOffset]::Parse(
                        $child.createdAtUtc,
                        [System.Globalization.CultureInfo]::InvariantCulture)
                }
                if ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 1) {
                    $captureState = "process-identity-changed"
                    $captureErrorType = "ProcessIdentityChanged"
                }
                else {
                    $processEvidencePath = Save-ProcessEvidence `
                        -Process $childProcess `
                        -AssemblyName $AssemblyName `
                        -Iteration $Iteration `
                        -Phase $Phase `
                        -Reason "residual-child-$($child.processId)" `
                        -SkipManagedStack:(
                            [string]$child.name -notmatch
                                '^(?:dotnet|pwsh|testhost|xunit|DownKyi).*\.exe$')
                    $captureState = "captured"
                }
            }
        }
        catch [System.InvalidOperationException] {
            $captureState = "exited-before-capture"
        }
        catch {
            $captureState = "capture-failed"
            $captureErrorType = $_.Exception.GetType().Name
        }
        finally {
            if ($null -ne $childProcess) {
                $childProcess.Dispose()
            }
        }

        if ($null -ne $captureErrorType) {
            $captureErrors += $captureErrorType
        }
        $childEvidence += [pscustomobject]@{
            processId = $child.processId
            createdAtUtc = $child.createdAtUtc
            captureState = $captureState
            processEvidencePath = $processEvidencePath
            errorType = $captureErrorType
        }
    }

    $manifest = [ordered]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        reason = "residual-child-process"
        observedChildren = $Children
        childEvidence = $childEvidence
        captureErrors = @($captureErrors | Select-Object -Unique)
    }
    $manifestPath = Join-Path $directory "residual-children.json"
    $manifest |
        ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8
    return [pscustomobject]@{
        evidencePath = [System.IO.Path]::GetRelativePath($runRoot, $directory).
            Replace([System.IO.Path]::DirectorySeparatorChar, '/')
        capturedChildCount = @(
            $childEvidence | Where-Object captureState -eq "captured"
        ).Count
        exitedBeforeCaptureCount = @(
            $childEvidence | Where-Object captureState -eq "exited-before-capture"
        ).Count
        errorType = if ($captureErrors.Count -eq 0) {
            $null
        }
        else {
            [string]$captureErrors[0]
        }
    }
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
