Set-StrictMode -Version Latest

function Assert-DownKyiCiEvidenceAuthority {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$ExpectedCommitSha
    )

    if ([string]::IsNullOrWhiteSpace($ExpectedCommitSha)) {
        throw "CI evidence requires an expected authoritative commit SHA."
    }
    $head = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or
        -not [string]::Equals($head, $ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw "CI evidence HEAD '$head' does not match expected SHA '$ExpectedCommitSha'."
    }
    $dirty = @(& git -C $RepositoryRoot status --short --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) {
        throw "CI evidence cannot be issued from a dirty worktree."
    }
    return $head.ToLowerInvariant()
}

function Write-DownKyiJsonEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$EvidencePath,
        [Parameter(Mandatory)]
        [object]$Evidence
    )

    $canonicalPath = [IO.Path]::GetFullPath($EvidencePath)
    $directory = Split-Path -Parent $canonicalPath
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporaryPath = "$canonicalPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $Evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporaryPath -Encoding utf8
        Move-Item -LiteralPath $temporaryPath -Destination $canonicalPath
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Write-DownKyiBuildEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$ExpectedCommitSha,
        [Parameter(Mandatory)]
        [ValidateSet("Windows", "Linux", "macOS")]
        [string]$Platform,
        [Parameter(Mandatory)]
        [ValidateSet("Debug", "Release")]
        [string]$Configuration,
        [Parameter(Mandatory)]
        [string]$EvidencePath
    )

    $head = Assert-DownKyiCiEvidenceAuthority `
        -RepositoryRoot $RepositoryRoot `
        -ExpectedCommitSha $ExpectedCommitSha
    Write-DownKyiJsonEvidence -EvidencePath $EvidencePath -Evidence ([ordered]@{
        schemaVersion = 1
        kind = "build"
        identity = "build/$Platform/$Configuration"
        commitSha = $head
        platform = $Platform
        configuration = $Configuration
        successful = $true
    })
}

function Write-DownKyiRepositorySuiteEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$ExpectedCommitSha,
        [Parameter(Mandatory)]
        [string]$EvidencePath,
        [Parameter(Mandatory)]
        [object]$SolutionResult
    )

    $head = Assert-DownKyiCiEvidenceAuthority `
        -RepositoryRoot $RepositoryRoot `
        -ExpectedCommitSha $ExpectedCommitSha
    $evidenceDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($EvidencePath))
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    $projects = @(
        foreach ($projectRun in @($SolutionResult.ProjectResults)) {
            $result = $projectRun.Result
            if ($result.ExitCode -ne 0 -or $result.Report.Executed -lt 1 -or $result.Report.Failed -ne 0) {
                throw "Repository project evidence is not successful: $($projectRun.ProjectPath)"
            }
            $sourceTrx = [IO.Path]::GetFullPath([string]$result.Report.ReportPath)
            $trxName = [IO.Path]::GetFileName($sourceTrx)
            $targetTrx = Join-Path $evidenceDirectory $trxName
            if (-not [string]::Equals($sourceTrx, $targetTrx, [StringComparison]::OrdinalIgnoreCase)) {
                Copy-Item -LiteralPath $sourceTrx -Destination $targetTrx
            }
            [ordered]@{
                project = ([string]$projectRun.ProjectPath).Replace("\", "/")
                trxFile = $trxName
                executed = [int]$result.Report.Executed
                failed = [int]$result.Report.Failed
                exitCode = [int]$result.ExitCode
                ownershipEstablished = [bool]$result.Ownership.OwnershipEstablished
            }
        }
    )
    Write-DownKyiJsonEvidence -EvidencePath $EvidencePath -Evidence ([ordered]@{
        schemaVersion = 1
        kind = "repository-suite"
        identity = "repository/$($SolutionResult.Platform)/$($SolutionResult.ShardIndex)-of-$($SolutionResult.ShardCount)"
        commitSha = $head
        platform = [string]$SolutionResult.Platform
        shardIndex = [int]$SolutionResult.ShardIndex
        shardCount = [int]$SolutionResult.ShardCount
        projects = $projects
        successful = $true
    })
}
