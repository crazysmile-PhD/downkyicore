function Build-DownKyiCentralTestRunner {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [ValidateSet("Debug", "Release")]
        [string]$Configuration = "Release",
        [switch]$NoRestore
    )

    $runnerProject = Join-Path $RepositoryRoot (
        "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj")
    $arguments = @(
        "build", $runnerProject, "-c", $Configuration,
        "-nodeReuse:false", "-p:UseSharedCompilation=false"
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }

    & dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "The compiled CentralTestRunner build failed with exit code $LASTEXITCODE."
    }

    $targetPathOutput = @(& dotnet msbuild $runnerProject `
        -nologo `
        -verbosity:quiet `
        -getProperty:TargetPath `
        "-p:Configuration=$Configuration")
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve the compiled CentralTestRunner output path."
    }
    $runnerPaths = @($targetPathOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($runnerPaths.Count -ne 1) {
        throw "Expected one compiled CentralTestRunner output path, found $($runnerPaths.Count)."
    }

    $runnerAssembly = $runnerPaths[0]
    if (-not (Test-Path -LiteralPath $runnerAssembly -PathType Leaf)) {
        throw "The compiled CentralTestRunner output is missing: $runnerAssembly"
    }

    return $runnerAssembly
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
        [string]$EvidenceDirectory
    )

    $runnerAssembly = Build-DownKyiCentralTestRunner `
        -RepositoryRoot $RepositoryRoot `
        -Configuration $Configuration `
        -NoRestore:$NoRestore
    $arguments = @(
        $runnerAssembly, "run-project",
        "--repository-root", $RepositoryRoot,
        "--project", $ProjectPath,
        "--configuration", $Configuration
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }
    if ($NoBuild) {
        $arguments += "--no-build"
    }
    if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
        $arguments += @("--results-directory", $ResultsDirectory)
    }
    if (-not [string]::IsNullOrWhiteSpace($TrxName)) {
        $arguments += @("--trx-name", $TrxName)
    }
    foreach ($className in @($ClassNames | Sort-Object -Unique)) {
        $arguments += @("--class", $className)
    }
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $arguments += @("--filter", $Filter)
    }
    if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
        $arguments += @("--evidence-directory", $EvidenceDirectory)
    }

    & dotnet @arguments | Out-Host
    $runnerExitCode = $LASTEXITCODE
    $trxPath = if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
        $null
    }
    else {
        $resolvedTrxName = if ([string]::IsNullOrWhiteSpace($TrxName)) {
            "$([IO.Path]::GetFileNameWithoutExtension($ProjectPath)).trx"
        }
        else {
            $TrxName
        }
        $absoluteTrxPath = Join-Path $ResultsDirectory $resolvedTrxName
        [IO.Path]::GetRelativePath($RepositoryRoot, $absoluteTrxPath).Replace("\\", "/")
    }

    return [pscustomobject]@{
        ExitCode = $runnerExitCode
        Runner = "central-test-runner"
        TrxPath = $trxPath
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
        [string[]]$ExcludedClassNames = @(),
        [string]$EvidenceDirectory
    )

    $runnerAssembly = Build-DownKyiCentralTestRunner `
        -RepositoryRoot $RepositoryRoot `
        -Configuration $Configuration `
        -NoRestore:$NoRestore
    $arguments = @(
        $runnerAssembly, "run-solution",
        "--repository-root", $RepositoryRoot,
        "--configuration", $Configuration
    )
    if ($NoRestore) {
        $arguments += "--no-restore"
    }
    if ($NoBuild) {
        $arguments += "--no-build"
    }
    if (-not [string]::IsNullOrWhiteSpace($ResultsDirectory)) {
        $arguments += @("--results-directory", $ResultsDirectory)
    }
    foreach ($className in @($ExcludedClassNames | Sort-Object -Unique)) {
        $arguments += @("--exclude-class", $className)
    }
    if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
        $arguments += @("--evidence-directory", $EvidenceDirectory)
    }

    & dotnet @arguments | Out-Host
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Runner = "central-test-runner"
    }
}
