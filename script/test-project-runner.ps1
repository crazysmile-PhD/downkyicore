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
    $runnerPolicy = Get-DownKyiTestRunnerPolicy `
        -RepositoryRoot $RepositoryRoot `
        -ProjectPath $project.FullName

    if ($null -eq $runnerPolicy) {
        $arguments = @("test", $project.FullName, "-c", $Configuration)
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
