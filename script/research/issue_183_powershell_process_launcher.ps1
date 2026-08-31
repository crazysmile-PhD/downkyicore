param(
    [Parameter(Mandatory = $true)]
    [string]$Repository,
    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$controlPrefix = '__ISSUE183_CONTROL__:'
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = 'dotnet'
$startInfo.WorkingDirectory = $Repository
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.ArgumentList.Add($AssemblyPath)
$startInfo.ArgumentList.Add('-assemblyInfo')

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
try {
    if (-not $process.Start()) {
        throw 'Historical-topology target process did not start.'
    }

    [Console]::Error.WriteLine("${controlPrefix}PID=$($process.Id)")
    [Console]::Error.Flush()

    $stdoutDone = $false
    $stderrDone = $false
    $stdoutRead = $process.StandardOutput.ReadLineAsync()
    $stderrRead = $process.StandardError.ReadLineAsync()

    while (-not $stdoutDone -or -not $stderrDone -or -not $process.HasExited) {
        if (-not $stdoutDone -and $stdoutRead.IsCompleted) {
            $line = $stdoutRead.GetAwaiter().GetResult()
            if ($null -eq $line) {
                $stdoutDone = $true
            }
            else {
                [Console]::Out.WriteLine($line)
                [Console]::Out.Flush()
                $stdoutRead = $process.StandardOutput.ReadLineAsync()
            }
        }

        if (-not $stderrDone -and $stderrRead.IsCompleted) {
            $line = $stderrRead.GetAwaiter().GetResult()
            if ($null -eq $line) {
                $stderrDone = $true
            }
            else {
                [Console]::Error.WriteLine($line)
                [Console]::Error.Flush()
                $stderrRead = $process.StandardError.ReadLineAsync()
            }
        }

        if (-not $process.HasExited) {
            $null = $process.WaitForExit(10)
        }
        elseif (-not $stdoutDone -or -not $stderrDone) {
            Start-Sleep -Milliseconds 1
        }
    }

    $process.WaitForExit()
    [Console]::Error.WriteLine("${controlPrefix}EXIT=$($process.ExitCode)")
    [Console]::Error.Flush()
    exit $process.ExitCode
}
finally {
    $process.Dispose()
}
