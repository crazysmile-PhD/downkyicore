function Write-AssemblyLifecycleReport {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Report,
        [Parameter(Mandatory)]
        [string]$RunRoot,
        [Parameter(Mandatory)]
        [string]$OutputRoot,
        [Parameter(Mandatory)]
        [string]$RunId
    )

    $statistics = @($Report.statistics)
    $failedResults = @($Report.results | Where-Object { -not $_.success })
    $slowResults = @($Report.results | Where-Object slowThresholdExceeded)
    $profile = $Report.profile
    $resolvedIterations = $Report.iterations
    $runtime = $Report.runtime
    $commitSha = $Report.commitSha
    $workingTreeDirty = $Report.workingTreeDirty
    $testAssemblyCount = $Report.testAssemblyCount
    $ownershipPassed = $Report.ownershipAuditPassed
    $slowEvidenceCapturedCount = $Report.slowEvidenceCapturedCount
    $slowEvidenceMissingCount = $Report.slowEvidenceMissingCount
    $diagnosticCaptureTotalMs = $Report.diagnosticCaptureTotalMs
    $forensicsSelfTestCaptureLeadValidated = $Report.forensicsSelfTestCaptureLeadValidated
    $reporterContractSelfTestPassed = $Report.reporterContractSelfTestPassed
    $markerReadContentionCount = $Report.markerReadContentionCount
    $markerReadRetriesExhaustedCount = $Report.markerReadRetriesExhaustedCount
    $markerReadErrorCount = $Report.markerReadErrorCount
    $markerReadErrorType = $Report.markerReadErrorType
    $markerReaderSelfTest = $Report.markerReaderSelfTest
    $phaseResultCount = $Report.results.Count

    $jsonPath = Join-Path $runRoot "assembly-lifecycle-report.json"
    $markdownPath = Join-Path $runRoot "assembly-lifecycle-report.md"
    $report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $jsonPath -Encoding utf8

    $markdown = [System.Collections.Generic.List[string]]::new()
    $markdown.Add("# Assembly Lifecycle Stability Report")
    $markdown.Add("")
    $markdown.Add("- Profile: ``$Profile``")
    $markdown.Add("- Iterations per assembly: $resolvedIterations")
    $markdown.Add("- Runtime: ``$runtime``")
    $markdown.Add("- OS: ``$($report.operatingSystem)``")
    $markdown.Add("- Architecture: ``$($report.architecture)``")
    $markdown.Add("- Commit: ``$commitSha``")
    $markdown.Add("- Working tree dirty: ``$workingTreeDirty``")
    $markdown.Add("- Assemblies: $($testAssemblyCount)")
    $markdown.Add("- Ownership audit: $(if ($ownershipPassed) { 'passed' } else { 'failed' })")
    $markdown.Add("- Failed phases: $($failedResults.Count)")
    $markdown.Add("- Slow phases: $($slowResults.Count)")
    $markdown.Add(
        "- Slow phase evidence: $slowEvidenceCapturedCount captured, " +
        "$slowEvidenceMissingCount missing")
    $markdown.Add("- Diagnostic capture wall time: $diagnosticCaptureTotalMs ms")
    $markdown.Add(
        "- Forensics pre-threshold capture self-test: " +
        "$forensicsSelfTestCaptureLeadValidated")
    $markdown.Add("- Reporter contract mutation self-test: $reporterContractSelfTestPassed")
    $markdown.Add("- Marker read contentions: $markerReadContentionCount")
    $markdown.Add("- Marker read retry exhaustion: $markerReadRetriesExhaustedCount")
    $markdown.Add(
        "- Marker read errors: $markerReadErrorCount; " +
        "last type=$markerReadErrorType")
    $markdown.Add(
        "- Marker reader self-test: executed=$($markerReaderSelfTest.executed), " +
        "passed=$($markerReaderSelfTest.passed), " +
        "contentionObserved=$($markerReaderSelfTest.contentionObserved), " +
        "contentionCount=$($markerReaderSelfTest.contentionCount), " +
        "recovered=$($markerReaderSelfTest.recoveredAfterLockRelease), " +
        "parsed=$($markerReaderSelfTest.markerParsedAfterRecovery), " +
        "error=$($markerReaderSelfTest.errorType), " +
        "contractChecks=$($markerReaderSelfTest.contractChecks.passed)")
    $markdown.Add("")
    $markdown.Add("| Assembly | Phase | Pass / Runs | Slow / captured | Success | P50 ms | P95 ms | P99 ms | Max ms |")
    $markdown.Add("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
    foreach ($item in $statistics) {
        $markdown.Add(
            "| $($item.assembly) | $($item.phase) | $($item.passed) / $($item.runs) | " +
            "$($item.slowRuns) / $($item.slowEvidenceCaptured) | " +
            "$([Math]::Round($item.successRate * 100, 2))% | $($item.p50Ms) | " +
            "$($item.p95Ms) | $($item.p99Ms) | $($item.maxMs) |")
    }
    $markdown.Add("")
    $markdown.Add("## Slow Phases")
    $markdown.Add("")
    if ($slowResults.Count -eq 0) {
        $markdown.Add("None.")
    }
    else {
        $markdown.Add("| Assembly | Iteration | Phase | Duration ms | Capture ms | Evidence status | Evidence |")
        $markdown.Add("| --- | ---: | --- | ---: | ---: | --- | --- |")
        foreach ($slow in $slowResults) {
            $evidenceText = if ($slow.slowEvidence.Count -eq 0) {
                ""
            }
            else {
                $slow.slowEvidence -join "<br>"
            }
            $markdown.Add(
                "| $($slow.assembly) | $($slow.iteration) | $($slow.phase) | " +
                "$($slow.durationMs) | $($slow.diagnosticCaptureDurationMs) | " +
                "$($slow.slowEvidenceStatus) | $evidenceText |")
        }
    }
    $markdown.Add("")
    $markdown.Add("## Failures")
    $markdown.Add("")
    if ($failedResults.Count -eq 0) {
        $markdown.Add("None.")
    }
    else {
        foreach ($failure in $failedResults) {
            $markdown.Add(
                "- ``$($failure.assembly)`` iteration $($failure.iteration), " +
                "``$($failure.phase)``: exit=$($failure.exitCode), " +
                "formalGate=$($failure.ownedProcessFormalGatePassed), " +
                "stdoutPolluted=$($failure.stdoutPolluted), " +
                "stderrPolluted=$($failure.stderrPolluted), " +
                "failureType=$($failure.failureType), errorType=$($failure.errorType), " +
                "slowEvidence=$($failure.slowEvidenceStatus), " +
                "proof=``$($failure.ownedProcessProofPath)``")
            foreach ($typedFailure in @($failure.ownedProcessFailures)) {
                $markdown.Add(
                    "  - typed failure: kind=``$($typedFailure.kind)``, " +
                    "phase=``$($typedFailure.phase)``, " +
                    "channel=``$($typedFailure.channel)``")
            }
        }
    }
    $markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8

    $latestPath = Join-Path $outputRoot "latest-run.txt"
    Set-Content -LiteralPath $latestPath -Encoding ascii -Value $runId
    Write-Host "Assembly lifecycle report: $markdownPath"
    Write-Host "Assemblies: $($testAssemblyCount); phase results: $($phaseResultCount); failures: $($failedResults.Count)"
}
