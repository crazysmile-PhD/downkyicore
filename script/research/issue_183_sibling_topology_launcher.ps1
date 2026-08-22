param(
    [Parameter(Mandatory = $true)]
    [string]$Repository,
    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath,
    [Parameter(Mandatory = $true)]
    [string]$ObserverScript,
    [Parameter(Mandatory = $true)]
    [string]$DumpTool,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [ValidateRange(1, 10000)]
    [int]$MaxIterations = 2000,
    [ValidateRange(1, 240)]
    [int]$DurationMinutes = 70,
    [ValidateRange(5, 300)]
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$allowedEnvironment = @(
    'APPDATA','COMSPEC','DOTNET_CLI_HOME','DOTNET_MULTILEVEL_LOOKUP','DOTNET_ROOT',
    'DOTNET_ROOT_X64','DOTNET_ROOT_X86','HOME','LOCALAPPDATA','NUMBER_OF_PROCESSORS',
    'PATH','PATHEXT','PROCESSOR_ARCHITECTURE','PROCESSOR_IDENTIFIER','PROGRAMDATA',
    'PROGRAMFILES','PROGRAMFILES(X86)','SYSTEMDRIVE','SYSTEMROOT','TEMP','TMP',
    'USERPROFILE','WINDIR','CI','GITHUB_ACTIONS','RUNNER_ARCH','RUNNER_ENVIRONMENT','RUNNER_OS',
    'ImageOS','ImageVersion'
)
$ambientPrefixes = @('DOTNET_','COMPLUS_','CORECLR_','COREHOST_')
$forbiddenTokens = @('AUTH','BEARER','COOKIE','CREDENTIAL','KEY','PASSWORD','SECRET','TOKEN')

function Set-SafeHistoricalEnvironment {
    param([Parameter(Mandatory)][System.Diagnostics.ProcessStartInfo]$StartInfo)

    $StartInfo.Environment.Clear()
    foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
        $name = [string]$entry.Key
        $upper = $name.ToUpperInvariant()
        $selected = $allowedEnvironment -contains $upper
        if (-not $selected) {
            foreach ($prefix in $ambientPrefixes) {
                if ($upper.StartsWith($prefix, [StringComparison]::Ordinal)) {
                    $selected = $true
                    break
                }
            }
        }
        if (-not $selected) { continue }
        if ($forbiddenTokens | Where-Object { $upper.Contains($_, [StringComparison]::Ordinal) }) {
            continue
        }
        $StartInfo.Environment[$name] = [string]$entry.Value
    }
    $StartInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $StartInfo.Environment['DOTNET_NOLOGO'] = '1'
}

function Append-Line {
    param([string]$Path, [string]$Line)
    [IO.File]::AppendAllText($Path, $Line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $outputRoot | Out-Null
$deadline = [DateTimeOffset]::UtcNow.AddMinutes($DurationMinutes)
$results = [Collections.Generic.List[object]]::new()

for ($iteration = 1; $iteration -le $MaxIterations; $iteration++) {
    if ([DateTimeOffset]::UtcNow -ge $deadline) { break }

    $iterationRoot = Join-Path $outputRoot ('iterations/iteration-{0:D6}' -f $iteration)
    New-Item -ItemType Directory -Force $iterationRoot | Out-Null
    $stdoutPath = Join-Path $iterationRoot 'stdout.txt'
    $stderrPath = Join-Path $iterationRoot 'stderr.txt'
    $pidPath = Join-Path $iterationRoot 'target-pid.txt'
    $exitPath = Join-Path $iterationRoot 'target-exit.json'
    $observerStdout = Join-Path $iterationRoot 'observer.stdout.txt'
    $observerStderr = Join-Path $iterationRoot 'observer.stderr.txt'
    Set-Content -LiteralPath $stdoutPath -Value '' -Encoding utf8NoBOM
    Set-Content -LiteralPath $stderrPath -Value '' -Encoding utf8NoBOM

    $observerInfo = [Diagnostics.ProcessStartInfo]::new()
    $observerInfo.FileName = 'python'
    $observerInfo.WorkingDirectory = $Repository
    $observerInfo.UseShellExecute = $false
    $observerInfo.CreateNoWindow = $true
    $observerInfo.RedirectStandardInput = $true
    $observerInfo.RedirectStandardOutput = $true
    $observerInfo.RedirectStandardError = $true
    $observerInfo.ArgumentList.Add($ObserverScript)
    $observerInfo.ArgumentList.Add('--control-dir')
    $observerInfo.ArgumentList.Add($iterationRoot)
    $observerInfo.ArgumentList.Add('--dump-tool')
    $observerInfo.ArgumentList.Add($DumpTool)
    $observerInfo.ArgumentList.Add('--timeout-seconds')
    $observerInfo.ArgumentList.Add($TimeoutSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    Set-SafeHistoricalEnvironment -StartInfo $observerInfo

    $observer = [Diagnostics.Process]::new()
    $observer.StartInfo = $observerInfo

    $targetInfo = [Diagnostics.ProcessStartInfo]::new()
    $targetInfo.FileName = 'dotnet'
    $targetInfo.WorkingDirectory = $Repository
    $targetInfo.UseShellExecute = $false
    $targetInfo.CreateNoWindow = $true
    $targetInfo.RedirectStandardOutput = $true
    $targetInfo.RedirectStandardError = $true
    $targetInfo.ArgumentList.Add($AssemblyPath)
    $targetInfo.ArgumentList.Add('-assemblyInfo')
    Set-SafeHistoricalEnvironment -StartInfo $targetInfo

    $target = [Diagnostics.Process]::new()
    $target.StartInfo = $targetInfo
    try {
        if (-not $observer.Start()) { throw 'Sibling observer did not start.' }
        $observer.StandardInput.Close()
        $observerOutTask = $observer.StandardOutput.ReadToEndAsync()
        $observerErrTask = $observer.StandardError.ReadToEndAsync()

        if (-not $target.Start()) { throw 'Historical direct target did not start.' }
        [IO.File]::WriteAllText($pidPath, [string]$target.Id, [Text.UTF8Encoding]::new($false))

        $stdoutDone = $false
        $stderrDone = $false
        $stdoutRead = $target.StandardOutput.ReadLineAsync()
        $stderrRead = $target.StandardError.ReadLineAsync()
        while (-not $stdoutDone -or -not $stderrDone -or -not $target.HasExited) {
            if (-not $stdoutDone -and $stdoutRead.IsCompleted) {
                $line = $stdoutRead.GetAwaiter().GetResult()
                if ($null -eq $line) {
                    $stdoutDone = $true
                }
                else {
                    Append-Line -Path $stdoutPath -Line $line
                    $stdoutRead = $target.StandardOutput.ReadLineAsync()
                }
            }
            if (-not $stderrDone -and $stderrRead.IsCompleted) {
                $line = $stderrRead.GetAwaiter().GetResult()
                if ($null -eq $line) {
                    $stderrDone = $true
                }
                else {
                    Append-Line -Path $stderrPath -Line $line
                    $stderrRead = $target.StandardError.ReadLineAsync()
                }
            }
            if (-not $target.HasExited) {
                $null = $target.WaitForExit(2)
            }
            elseif (-not $stdoutDone -or -not $stderrDone) {
                Start-Sleep -Milliseconds 1
            }
        }
        $target.WaitForExit()
        @{ exitCode = $target.ExitCode } | ConvertTo-Json -Compress |
            Set-Content -LiteralPath $exitPath -Encoding utf8NoBOM

        if (-not $observer.WaitForExit(($TimeoutSeconds + 150) * 1000)) {
            $observer.Kill($true)
            $observer.WaitForExit()
            throw 'Sibling observer exceeded its fail-closed completion window.'
        }
        [IO.File]::WriteAllText($observerStdout, $observerOutTask.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($observerStderr, $observerErrTask.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))

        $resultPath = Join-Path $iterationRoot 'observer-result.json'
        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            throw 'Sibling observer did not publish observer-result.json.'
        }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $results.Add($result)
        if ($observer.ExitCode -ne 0 -or -not $result.executionValid -or $result.watchdogSeen) {
            break
        }
    }
    finally {
        if (-not $target.HasExited) {
            try { $target.Kill($true); $target.WaitForExit() } catch {}
        }
        if (-not $observer.HasExited) {
            try { $observer.Kill($true); $observer.WaitForExit() } catch {}
        }
        $target.Dispose()
        $observer.Dispose()
    }
}

$summary = [ordered]@{
    schemaVersion = 1
    targetSha = '75bff22801972ada7cfd24be595b0753780a7592'
    assembly = 'DownKyi.Desktop.Tests'
    phase = 'assembly-info'
    launchTopology = 'workflow-step pwsh -> direct System.Diagnostics.Process target; Python observer is sibling'
    ambientEnvironment = 'sanitized baseline plus non-secret DOTNET/CLR/host/CI variables'
    targetInvocation = 'dotnet DownKyi.Desktop.Tests.dll -assemblyInfo'
    iterations = $results.Count
    jsonDetections = @($results | Where-Object { $null -ne $_.assemblyInfoJsonSeenAtUtc }).Count
    invalidExecutions = @($results | Where-Object { -not $_.executionValid }).Count
    nonzeroTargetExits = @($results | Where-Object { $_.targetExitCode -ne 0 }).Count
    observerFailures = @($results | Where-Object { @($_.observerErrors).Count -gt 0 }).Count
    alive500msAfterJson = @($results | Where-Object { $_.alive500msAfterJson }).Count
    watchdogs = @($results | Where-Object { $_.watchdogSeen }).Count
    confirmedWatchdogDumps = @($results | Where-Object { $_.confirmedWatchdogDump }).Count
    timeouts = @($results | Where-Object { $_.timedOut }).Count
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json') -Encoding utf8NoBOM
$summary | ConvertTo-Json -Depth 6

if ($summary.invalidExecutions -gt 0) { exit 4 }
if ($summary.watchdogs -gt 0 -and $summary.confirmedWatchdogDumps -eq 0) { exit 3 }
exit 0
