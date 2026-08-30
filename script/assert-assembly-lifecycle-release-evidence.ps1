[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,
    [Parameter(Mandatory)]
    [ValidateSet(
        "DownKyi.Application.Tests",
        "DownKyi.Architecture.Tests",
        "DownKyi.Core.Tests",
        "DownKyi.Desktop.Tests",
        "DownKyi.Domain.Tests",
        "DownKyi.Infrastructure.Tests",
        "DownKyi.Tests",
        "DownKyi.Windows.Tests")]
    [string]$ExpectedAssembly,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$ExpectedCommitSha,
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [switch]$ValidateMutationSelfTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$topologyPath = Join-Path $repositoryRoot "docs/testing/assembly-lifecycle-release-topology.json"
$topology = Get-Content -LiteralPath $topologyPath -Raw | ConvertFrom-Json -Depth 10
$assemblyTopology = @($topology.assemblies | Where-Object name -eq $ExpectedAssembly)
if ([int]$topology.schemaVersion -ne 1 -or
    $topology.profile -ne "Rehearsal" -or
    [int]$topology.totalIterations -ne 100 -or
    $assemblyTopology.Count -ne 1) {
    throw "Lifecycle release topology is missing or invalid for $ExpectedAssembly."
}
$expectedShardCount = [int]$assemblyTopology[0].shardCount
$expectedCommit = $ExpectedCommitSha.ToLowerInvariant()
$expectedTotalIterations = [int]$topology.totalIterations
$requiredFormalPhases = @(
    "load",
    "assembly-info",
    "discovery",
    "execution",
    "assembly-teardown",
    "process-exit")
$requiredForensicsFlags = @(
    "forensicsSelfTestCaptureLeadValidated",
    "forensicsSelfTestPositiveCaptureThresholdValidated",
    "forensicsSelfTestCaptureCompletedBeforeTargetExitValidated",
    "forensicsSelfTestEvidenceHoldValidated",
    "forensicsSelfTestReleaseOrderingMutationValidated",
    "slowEvidenceOrderingSelfTestPassed",
    "reporterContractSelfTestPassed",
    "forensicsCollectorCaptureWindowSelfTestPassed",
    "forensicsCollectorCleanupReportSelfTestPassed",
    "forensicsEvidencePersistenceSelfTestPassed",
    "forensicsCollectorInterruptedStackSelfTestPassed",
    "dotnetStackAttachStallSelfTestPassed",
    "ownershipAuditPassed",
    "markerReaderSelfTestPassed",
    "processLeaseSelfTestPassed")

function Get-ExpectedShardIterations {
    param(
        [int]$ShardIndex,
        [int]$ShardCount
    )

    $iterations = [int][Math]::Floor($expectedTotalIterations / $ShardCount)
    if ($ShardIndex -lt ($expectedTotalIterations % $ShardCount)) {
        $iterations++
    }
    return $iterations
}

function Read-ShardEvidence {
    param([string]$Mutation)

    $root = [IO.Path]::GetFullPath($EvidenceRoot)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Lifecycle evidence root does not exist: $root"
    }

    $items = @(
        Get-ChildItem -LiteralPath $root -Recurse -File |
            Where-Object Name -eq "shard-manifest.json" |
            Sort-Object FullName |
            ForEach-Object {
                [pscustomobject]@{
                    manifestPath = $_.FullName
                    manifestRoot = $_.DirectoryName
                    manifest = Get-Content -LiteralPath $_.FullName -Raw |
                        ConvertFrom-Json -Depth 20
                }
            }
    )

    switch ($Mutation) {
        "missing-shard" {
            if ($items.Count -gt 0) {
                $items = @($items | Select-Object -First ($items.Count - 1))
            }
        }
        "duplicate-shard" {
            if ($items.Count -lt 2) {
                throw "Duplicate-shard mutation requires at least two shards."
            }
            $items[1].manifest.shardIndex = $items[0].manifest.shardIndex
        }
        "stale-commit" {
            if ($items.Count -gt 0) {
                $items[0].manifest.commitSha = "0000000000000000000000000000000000000000"
            }
        }
        "wrong-report-hash" {
            if ($items.Count -gt 0) {
                $items[0].manifest.reportSha256 = "0" * 64
            }
        }
        "" { }
        default { throw "Unknown lifecycle evidence mutation: $Mutation" }
    }

    return $items
}

function Assert-ShardEvidence {
    param([string]$Mutation = "")

    $items = @(Read-ShardEvidence -Mutation $Mutation)
    if ($items.Count -ne $expectedShardCount) {
        throw "Expected $expectedShardCount lifecycle shard manifests; observed $($items.Count)."
    }

    $seenIndices = [Collections.Generic.HashSet[int]]::new()
    $seenReportHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $validatedShards = [Collections.Generic.List[object]]::new()
    $observedIterationTotal = 0
    foreach ($item in $items) {
        $manifest = $item.manifest
        $index = [int]$manifest.shardIndex
        if ([int]$manifest.schemaVersion -ne 1 -or
            $manifest.kind -ne "assembly-lifecycle-release-shard" -or
            $manifest.assembly -ne $ExpectedAssembly -or
            $manifest.profile -ne "Rehearsal" -or
            -not [bool]$manifest.validateForensics -or
            [int]$manifest.shardCount -ne $expectedShardCount -or
            [int]$manifest.totalIterations -ne $expectedTotalIterations -or
            -not [bool]$manifest.successful -or
            $manifest.commitSha.ToLowerInvariant() -ne $expectedCommit) {
            throw "Lifecycle shard manifest is stale, incomplete or owned by another authority: $($item.manifestPath)"
        }
        if ($index -lt 0 -or $index -ge $expectedShardCount -or -not $seenIndices.Add($index)) {
            throw "Lifecycle shard indices must cover one unique range: index=$index."
        }

        $expectedIterations = Get-ExpectedShardIterations `
            -ShardIndex $index `
            -ShardCount $expectedShardCount
        if ([int]$manifest.shardIterations -ne $expectedIterations) {
            throw "Lifecycle shard $index owns an invalid iteration count."
        }
        $observedIterationTotal += $expectedIterations

        $manifestRoot = [IO.Path]::GetFullPath($item.manifestRoot)
        $reportPath = [IO.Path]::GetFullPath(
            [string]$manifest.reportRelativePath,
            $manifestRoot)
        $ownedPrefix = $manifestRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $reportPath.StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "Lifecycle shard report escaped or is missing from its artifact owner."
        }

        $actualHash = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedHash = $manifest.reportSha256.ToLowerInvariant()
        if ($actualHash -ne $expectedHash -or -not $seenReportHashes.Add($actualHash)) {
            throw "Lifecycle shard report is stale, copied or does not match its manifest."
        }

        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 30
        if ([int]$report.schemaVersion -ne 4 -or
            $report.profile -ne "Rehearsal" -or
            [int]$report.iterations -ne $expectedIterations -or
            $report.commitSha.ToLowerInvariant() -ne $expectedCommit -or
            [bool]$report.workingTreeDirty -or
            [int]$report.testAssemblyCount -ne 1 -or
            -not [bool]$report.successful -or
            [int]$report.failedPhaseCount -ne 0 -or
            [int]$report.slowEvidenceMissingCount -ne 0 -or
            [int]$report.residualChildPhaseCount -ne 0 -or
            [int]$report.residualChildObservedCount -ne 0 -or
            [int]$report.residualChildEvidenceMissingCount -ne 0) {
            throw "Lifecycle shard report failed its exact Rehearsal contract."
        }
        foreach ($flag in $requiredForensicsFlags) {
            if (-not [bool]$report.$flag) {
                throw "Lifecycle shard report did not preserve required proof '$flag'."
            }
        }

        $formalRows = @($report.results | Where-Object assembly -eq $ExpectedAssembly)
        if ($formalRows.Count -ne ($expectedIterations * $requiredFormalPhases.Count)) {
            throw "Lifecycle shard report is missing formal phase rows."
        }
        foreach ($iteration in 1..$expectedIterations) {
            $rows = @($formalRows | Where-Object { [int]$_.iteration -eq $iteration })
            $phases = @($rows | ForEach-Object { [string]$_.phase } | Sort-Object)
            $expectedPhases = @($requiredFormalPhases | Sort-Object)
            if ($rows.Count -ne $requiredFormalPhases.Count -or
                [string]::Join("|", $phases) -ne [string]::Join("|", $expectedPhases) -or
                @($rows | Where-Object { -not [bool]$_.success }).Count -ne 0 -or
                @($rows | Where-Object { [int]$_.residualChildCount -ne 0 }).Count -ne 0) {
                throw "Lifecycle shard $index iteration $iteration is incomplete or failed."
            }
        }

        $validatedShards.Add([ordered]@{
            shardIndex = $index
            iterations = $expectedIterations
            reportSha256 = $actualHash
        })
    }

    if ($seenIndices.Count -ne $expectedShardCount -or
        $observedIterationTotal -ne $expectedTotalIterations) {
        throw "Lifecycle shards do not form one complete 100-iteration Rehearsal."
    }

    return @($validatedShards | Sort-Object shardIndex)
}

$validated = @(Assert-ShardEvidence)
$mutationResults = [Collections.Generic.List[string]]::new()
if ($ValidateMutationSelfTests) {
    foreach ($mutation in @(
            "missing-shard",
            "duplicate-shard",
            "stale-commit",
            "wrong-report-hash")) {
        $accepted = $false
        try {
            $null = Assert-ShardEvidence -Mutation $mutation
            $accepted = $true
        }
        catch {
            Write-Host "Lifecycle evidence mutation rejected: $mutation ($($_.Exception.Message))"
        }
        if ($accepted) {
            throw "Lifecycle evidence validator accepted deliberate mutation '$mutation'."
        }
        $mutationResults.Add($mutation)
    }
}

$aggregate = [ordered]@{
    schemaVersion = 1
    kind = "assembly-lifecycle-release-aggregate"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    commitSha = $expectedCommit
    assembly = $ExpectedAssembly
    profile = "Rehearsal"
    validateForensics = $true
    shardCount = $expectedShardCount
    totalIterations = $expectedTotalIterations
    mutationSelfTests = @($mutationResults)
    shards = $validated
    successful = $true
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$aggregate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8
Write-Host (
    "Lifecycle release evidence passed: assembly=$ExpectedAssembly, " +
    "shards=$expectedShardCount, iterations=$expectedTotalIterations, commit=$expectedCommit.")
