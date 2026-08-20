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
