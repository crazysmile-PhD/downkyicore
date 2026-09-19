param(
    [Parameter(Mandatory = $true)]
    [string]$SarifDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$BaselinePath,

    [string]$RepositoryRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),

    [switch]$InitializeBaseline
)

$ErrorActionPreference = 'Stop'

$legacyRules = @(
    'CA1005', 'CA1017', 'CA1021', 'CA1045', 'CA1060',
    'CA1501', 'CA1502', 'CA1505', 'CA1506', 'CA1509'
)
$baselineRules = @('CA1501', 'CA1506')
$resolvedRepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
$resolvedSarifDirectory = [System.IO.Path]::GetFullPath($SarifDirectory)
$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$resolvedBaselinePath = [System.IO.Path]::GetFullPath($BaselinePath)
$repositoryRootWithSeparator = $resolvedRepositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$repositoryUriPrefix = [UriBuilder]::new(
    [Uri]::UriSchemeFile,
    '',
    -1,
    $repositoryRootWithSeparator).Uri.AbsoluteUri

function Get-StableId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prefix,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return "$Prefix-$([Convert]::ToHexString($hash).Substring(0, 16).ToLowerInvariant())"
}

function Normalize-Quotes {
    param([string]$Value)

    if ($null -eq $Value) {
        return ''
    }

    return $Value.Replace([char]0x2018, "'").Replace([char]0x2019, "'")
}

function Get-ResultMessage {
    param($Result)

    if ($Result.message -is [string]) {
        return [string]$Result.message
    }

    if ($null -ne $Result.message.text) {
        return [string]$Result.message.text
    }

    return [string]$Result.message
}

function Get-ResultLocation {
    param($Result)

    $location = @($Result.locations)[0]
    if ($null -eq $location) {
        return [ordered]@{ Uri = ''; Line = 0; Column = 0 }
    }

    if ($null -ne $location.resultFile) {
        $artifact = $location.resultFile
        $region = $location.resultFile.region
    }
    else {
        $artifact = $location.physicalLocation.artifactLocation
        $region = $location.physicalLocation.region
    }

    return [ordered]@{
        Uri = [string]$artifact.uri
        Line = if ($null -ne $region.startLine) { [int]$region.startLine } else { 0 }
        Column = if ($null -ne $region.startColumn) { [int]$region.startColumn } else { 0 }
    }
}

function Get-RepositoryRelativePath {
    param([string]$Uri)

    if ([string]::IsNullOrWhiteSpace($Uri)) {
        return '<unknown>'
    }

    try {
        $parsed = [Uri]$Uri
        $localPath = if ($parsed.IsFile) { $parsed.LocalPath } else { $Uri }
    }
    catch {
        $localPath = $Uri
    }

    if (-not [System.IO.Path]::IsPathFullyQualified($localPath)) {
        return $localPath.Replace('\', '/')
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($localPath)
    $relative = [System.IO.Path]::GetRelativePath($resolvedRepositoryRoot, $resolvedPath)
    return $relative.Replace('\', '/')
}

function Get-QuotedValues {
    param([string]$Message)

    $normalized = Normalize-Quotes $Message
    return @([regex]::Matches($normalized, "'(?<value>[^']+)'") | ForEach-Object {
        $_.Groups['value'].Value
    })
}

function Get-Symbol {
    param([string]$Message)

    $quoted = @(Get-QuotedValues $Message)
    foreach ($value in $quoted) {
        if ($value -notmatch '^\d+$') {
            return $value
        }
    }

    return '<unknown>'
}

function Get-ObservedMetric {
    param([string]$Message)

    $numbers = @(Get-QuotedValues $Message | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ })
    if ($numbers.Count -eq 0) {
        return $null
    }

    return [int](($numbers | Measure-Object -Maximum).Maximum)
}

function Get-HierarchyChain {
    param([string]$Message)

    $chains = @(Get-QuotedValues $Message | Where-Object { $_ -like '*,*' })
    if ($chains.Count -eq 0) {
        return $null
    }

    return [string]$chains[-1]
}

function Get-DefaultMetadata {
    param(
        [string]$Rule,
        [string]$Path,
        [string]$Symbol,
        [string]$Message,
        [string]$Identity
    )

    if ($Rule -eq 'CA1501') {
        $chain = Get-HierarchyChain $Message
        if ($chain -and $chain.Contains('AvaloniaObject', [StringComparison]::Ordinal)) {
            $root = $chain.Split(',')[0].Trim()
            return [ordered]@{
                GroupId = Get-StableId 'ca1501-framework' $chain
                GroupTitle = "Avalonia framework inheritance rooted at $root"
                Classification = 'framework-inheritance'
                ReviewStatus = 'reviewed-reasonable'
                Rationale = 'The reported depth comes from a shared Avalonia framework inheritance chain, not a DownKyi-owned hierarchy.'
            }
        }

        return [ordered]@{
            GroupId = Get-StableId 'ca1501-review' $Identity
            GroupTitle = "CA1501 needs review: $Path::$Symbol"
            Classification = 'needs-review'
            ReviewStatus = 'needs-review'
            Rationale = 'The inheritance chain could not be safely classified as framework-owned.'
        }
    }

    if ($Rule -eq 'CA1506') {
        $known = @{
            'DownKyi.Core/FFmpeg/FfmpegConcatRuntime.cs|ConcatAsync' = @('operation-owner', 'reviewed-reasonable', 'The method owns one complete FFmpeg concat operation and its validation and cleanup invariants.')
            'DownKyi.Core/Settings/SettingsManager.About.cs|SettingsManager' = @('settings-aggregation', 'reviewed-reasonable', 'The metric aggregates a partial settings owner across settings categories.')
            'src/DownKyi.Desktop/Services/Download/Aria2TransferBackend.cs|TransferAsync' = @('operation-owner', 'reviewed-reasonable', 'The method owns one Aria transfer attempt, including pause, progress, and result translation.')
            'src/DownKyi.Desktop/Services/Download/BuiltinTransferBackend.cs|TransferAsync' = @('operation-owner', 'reviewed-reasonable', 'The method owns one built-in transfer attempt and its lifecycle invariants.')
            'src/DownKyi.Desktop/Services/Download/DownloadRuntimeFactory.cs|Create' = @('composition-root', 'reviewed-reasonable', 'The coupling belongs to the explicit download runtime composition root.')
        }
        $knownKey = "$Path|$Symbol"
        if ($known.ContainsKey($knownKey)) {
            $entry = $known[$knownKey]
            return [ordered]@{
                GroupId = Get-StableId 'ca1506-reviewed' $Identity
                GroupTitle = "$($entry[0]): $Path::$Symbol"
                Classification = $entry[0]
                ReviewStatus = $entry[1]
                Rationale = $entry[2]
            }
        }

        if ($Path.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase)) {
            return [ordered]@{
                GroupId = Get-StableId 'ca1506-test' $Identity
                GroupTitle = "Integration-test coupling: $Path::$Symbol"
                Classification = 'integration-test'
                ReviewStatus = 'reviewed-reasonable'
                Rationale = 'The finding is confined to test or platform-fixture integration coverage; it remains individually traceable.'
            }
        }

        return [ordered]@{
            GroupId = Get-StableId 'ca1506-review' $Identity
            GroupTitle = "CA1506 needs review: $Path::$Symbol"
            Classification = 'needs-review'
            ReviewStatus = 'needs-review'
            Rationale = 'No reviewed responsibility-level root cause is recorded for this product finding.'
        }
    }

    return [ordered]@{
        GroupId = Get-StableId 'legacy-ca' $Identity
        GroupTitle = "$($Rule): $Path::$Symbol"
        Classification = 'advisory'
        ReviewStatus = 'observed'
        Rationale = 'This rule is retained in the complete advisory inventory.'
    }
}

if (-not (Test-Path -LiteralPath $resolvedSarifDirectory -PathType Container)) {
    throw "SARIF directory was not produced: $resolvedSarifDirectory"
}

$sarifFiles = @(Get-ChildItem -LiteralPath $resolvedSarifDirectory -Filter '*.sarif' -File | Sort-Object Name)
if ($sarifFiles.Count -eq 0) {
    throw "No SARIF files were produced in $resolvedSarifDirectory"
}

$rawDrafts = [System.Collections.Generic.List[object]]::new()
foreach ($sarifFile in $sarifFiles) {
    $sarifText = Get-Content -LiteralPath $sarifFile.FullName -Raw
    $sanitizedSarifText = $sarifText.Replace(
        $repositoryUriPrefix,
        '',
        [StringComparison]::OrdinalIgnoreCase)
    if (-not [string]::Equals($sarifText, $sanitizedSarifText, [StringComparison]::Ordinal)) {
        Set-Content -LiteralPath $sarifFile.FullName -Value $sanitizedSarifText -Encoding utf8NoBOM
    }
    $sarif = $sanitizedSarifText | ConvertFrom-Json -Depth 100
    foreach ($run in @($sarif.runs)) {
        foreach ($result in @($run.results)) {
            $rule = [string]$result.ruleId
            $isSuppressed = $null -ne $result.suppressionStates -and @($result.suppressionStates).Count -gt 0
            if ($rule -notin $legacyRules -or $isSuppressed) {
                continue
            }

            $message = Get-ResultMessage $result
            $location = Get-ResultLocation $result
            $path = Get-RepositoryRelativePath $location.Uri
            $rawDrafts.Add([pscustomobject][ordered]@{
                rule = $rule
                project = $sarifFile.BaseName
                path = $path
                line = $location.Line
                column = $location.Column
                symbol = Get-Symbol $message
                message = $message
                observedMetric = Get-ObservedMetric $message
                sourceSarif = $sarifFile.Name
                sourceResult = $result
            })
        }
    }
}

$orderedRawDrafts = @($rawDrafts | Sort-Object rule, path, line, column, project, message)
$rawFindings = [System.Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $orderedRawDrafts.Count; $index++) {
    $draft = $orderedRawDrafts[$index]
    $rawFindings.Add([pscustomobject][ordered]@{
        id = 'raw-{0:D4}' -f ($index + 1)
        rule = $draft.rule
        project = $draft.project
        path = $draft.path
        line = $draft.line
        column = $draft.column
        symbol = $draft.symbol
        message = $draft.message
        observedMetric = $draft.observedMetric
        sourceSarif = $draft.sourceSarif
        sourceResult = $draft.sourceResult
    })
}

$logicalDrafts = [System.Collections.Generic.List[object]]::new()
$baseIdentityGroups = @($rawFindings | Group-Object { "$($_.rule)|$($_.path)|$($_.symbol)" })
foreach ($baseIdentityGroup in $baseIdentityGroups) {
    $locationGroups = @($baseIdentityGroup.Group | Group-Object { "$($_.line):$($_.column)" } | Sort-Object Name)
    $requiresLocationDiscriminator = $locationGroups.Count -gt 1
    foreach ($locationGroup in $locationGroups) {
        $first = $locationGroup.Group[0]
        $identity = if ($requiresLocationDiscriminator) {
            "$($baseIdentityGroup.Name)|location:$($locationGroup.Name)"
        }
        else {
            $baseIdentityGroup.Name
        }
        $metadata = Get-DefaultMetadata $first.rule $first.path $first.symbol $first.message $identity
        $metrics = @($locationGroup.Group | Where-Object { $null -ne $_.observedMetric } | ForEach-Object { [int]$_.observedMetric })
        $metric = if ($metrics.Count -gt 0) { [int](($metrics | Measure-Object -Maximum).Maximum) } else { $null }
        $logicalDrafts.Add([pscustomobject][ordered]@{
            identity = $identity
            id = Get-StableId 'finding' $identity
            rule = $first.rule
            path = $first.path
            line = $first.line
            column = $first.column
            symbol = $first.symbol
            observedMetric = $metric
            projects = @($locationGroup.Group.project | Sort-Object -Unique)
            messageVariants = @($locationGroup.Group.message | Sort-Object -Unique)
            rawFindingIds = @($locationGroup.Group.id)
            defaultMetadata = $metadata
        })
    }
}
$logicalDrafts = @($logicalDrafts | Sort-Object rule, path, line, symbol)

if ($InitializeBaseline) {
    $baselineDirectory = Split-Path -Parent $resolvedBaselinePath
    if ($baselineDirectory) {
        New-Item -ItemType Directory -Path $baselineDirectory -Force | Out-Null
    }

    $baselineDocument = [ordered]@{
        schemaVersion = 1
        description = 'Reviewed CA1501 and CA1506 findings present when the advisory code-metrics baseline was established.'
        findings = @($logicalDrafts | Where-Object { $_.rule -in $baselineRules } | ForEach-Object {
            [ordered]@{
                identity = $_.identity
                rule = $_.rule
                path = $_.path
                symbol = $_.symbol
                observedMetric = $_.observedMetric
                groupId = $_.defaultMetadata.GroupId
                groupTitle = $_.defaultMetadata.GroupTitle
                classification = $_.defaultMetadata.Classification
                reviewStatus = $_.defaultMetadata.ReviewStatus
                rationale = $_.defaultMetadata.Rationale
            }
        })
    }
    Set-Content -LiteralPath $resolvedBaselinePath -Value ($baselineDocument | ConvertTo-Json -Depth 20) -Encoding utf8NoBOM
}

if (-not (Test-Path -LiteralPath $resolvedBaselinePath -PathType Leaf)) {
    throw "Code-metrics baseline is missing: $resolvedBaselinePath"
}
$baseline = Get-Content -LiteralPath $resolvedBaselinePath -Raw | ConvertFrom-Json -Depth 50
if ([int]$baseline.schemaVersion -ne 1) {
    throw "Unsupported code-metrics baseline schema version: $($baseline.schemaVersion)"
}
$baselineByIdentity = @{}
foreach ($entry in @($baseline.findings)) {
    if ($baselineByIdentity.ContainsKey([string]$entry.identity)) {
        throw "Duplicate baseline identity: $($entry.identity)"
    }
    $baselineByIdentity[[string]$entry.identity] = $entry
}

$findings = [System.Collections.Generic.List[object]]::new()
foreach ($draft in $logicalDrafts) {
    $baselineEntry = if ($baselineByIdentity.ContainsKey($draft.identity)) { $baselineByIdentity[$draft.identity] } else { $null }
    if ($draft.rule -notin $baselineRules) {
        $status = 'observed'
        $metricDelta = $null
        $metadata = $draft.defaultMetadata
    }
    elseif ($null -eq $baselineEntry) {
        $status = 'new'
        $metricDelta = $null
        $metadata = $draft.defaultMetadata
    }
    else {
        $metricDelta = if ($null -ne $draft.observedMetric -and $null -ne $baselineEntry.observedMetric) {
            [int]$draft.observedMetric - [int]$baselineEntry.observedMetric
        } else {
            $null
        }
        $status = if ($null -ne $metricDelta -and $metricDelta -gt 0) { 'metric-worsened' } else { 'baseline' }
        $metadata = [ordered]@{
            GroupId = [string]$baselineEntry.groupId
            GroupTitle = [string]$baselineEntry.groupTitle
            Classification = [string]$baselineEntry.classification
            ReviewStatus = [string]$baselineEntry.reviewStatus
            Rationale = [string]$baselineEntry.rationale
        }
    }

    $findings.Add([pscustomobject][ordered]@{
        id = $draft.id
        identity = $draft.identity
        rule = $draft.rule
        path = $draft.path
        line = $draft.line
        column = $draft.column
        symbol = $draft.symbol
        observedMetric = $draft.observedMetric
        baselineMetric = if ($null -ne $baselineEntry) { $baselineEntry.observedMetric } else { $null }
        metricDelta = $metricDelta
        status = $status
        groupId = $metadata.GroupId
        classification = $metadata.Classification
        reviewStatus = $metadata.ReviewStatus
        rationale = $metadata.Rationale
        projects = $draft.projects
        messageVariants = $draft.messageVariants
        rawFindingIds = $draft.rawFindingIds
    })
}
$findings = @($findings)

$groups = [System.Collections.Generic.List[object]]::new()
foreach ($group in @($findings | Group-Object groupId | Sort-Object Name)) {
    $members = @($group.Group | Sort-Object rule, path, line, symbol)
    $statuses = @($members.status | Sort-Object -Unique)
    $groupStatus = if ($statuses -contains 'new') {
        'new'
    } elseif ($statuses -contains 'metric-worsened') {
        'metric-worsened'
    } elseif ($statuses -contains 'baseline') {
        'baseline'
    } else {
        'observed'
    }
    $first = $members[0]
    $baselineEntry = if ($baselineByIdentity.ContainsKey($first.identity)) { $baselineByIdentity[$first.identity] } else { $null }
    $title = if ($null -ne $baselineEntry) { [string]$baselineEntry.groupTitle } else { [string]$first.identity }
    if ($null -eq $baselineEntry) {
        $title = [string]$logicalDrafts.Where({ $_.identity -eq $first.identity }, 'First').defaultMetadata.GroupTitle
    }
    $groups.Add([pscustomobject][ordered]@{
        id = $group.Name
        rule = [string]$first.rule
        title = $title
        classification = [string]$first.classification
        reviewStatus = [string]$first.reviewStatus
        status = $groupStatus
        findingIds = @($members.id)
        rawFindingIds = @($members | ForEach-Object { $_.rawFindingIds } | Sort-Object -Unique)
        members = @($members | ForEach-Object {
            [ordered]@{
                findingId = $_.id
                rule = $_.rule
                file = $_.path
                path = $_.path
                symbol = $_.symbol
                rawFindingIds = $_.rawFindingIds
            }
        })
    })
}
$groups = @($groups)

$currentIdentities = @{}
foreach ($finding in $findings) { $currentIdentities[$finding.identity] = $true }
$baselineMissing = @($baseline.findings | Where-Object { -not $currentIdentities.ContainsKey([string]$_.identity) })
$newFindings = @($findings | Where-Object status -eq 'new')
$worsenedFindings = @($findings | Where-Object status -eq 'metric-worsened')

$result = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    baselinePath = [System.IO.Path]::GetRelativePath($resolvedRepositoryRoot, $resolvedBaselinePath).Replace('\', '/')
    counts = [ordered]@{
        rawFindingCount = $rawFindings.Count
        logicalFindingCount = $findings.Count
        groupCount = $groups.Count
        baselineCount = @($baseline.findings).Count
        newFindingCount = $newFindings.Count
        metricWorsenedCount = $worsenedFindings.Count
        baselineMissingCount = $baselineMissing.Count
    }
    newFindings = @($newFindings.id)
    metricWorsenedFindings = @($worsenedFindings.id)
    baselineMissing = $baselineMissing
    groups = $groups
    findings = $findings
    rawFindings = $rawFindings
}

New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
$jsonPath = Join-Path $resolvedOutputDirectory 'legacy-ca-report.json'
$markdownPath = Join-Path $resolvedOutputDirectory 'legacy-ca-summary.md'
Set-Content -LiteralPath $jsonPath -Value ($result | ConvertTo-Json -Depth 100) -Encoding utf8NoBOM

$markdown = [System.Collections.Generic.List[string]]::new()
$markdown.Add('# Legacy CA advisory report')
$markdown.Add('')
$markdown.Add('The JSON report is authoritative. Every summary group lists finding IDs, every finding lists all raw finding IDs, and every raw finding embeds its complete SARIF result.')
$markdown.Add('')
$markdown.Add("- Raw findings: $($rawFindings.Count)")
$markdown.Add("- Logical findings: $($findings.Count)")
$markdown.Add("- Summary groups: $($groups.Count)")
$markdown.Add("- New CA1501/CA1506 findings: $($newFindings.Count)")
$markdown.Add("- Increased CA1501/CA1506 metrics: $($worsenedFindings.Count)")
$markdown.Add("- Baseline findings not observed: $($baselineMissing.Count)")
$markdown.Add('')
$markdown.Add('Any numeric increase is reported with its exact delta; the report does not guess whether a CA1506 increase justifies refactoring.')
$markdown.Add('')
$markdown.Add('## Findings requiring attention')
$markdown.Add('')
if ($newFindings.Count -eq 0 -and $worsenedFindings.Count -eq 0) {
    $markdown.Add('No new or worsened CA1501/CA1506 findings.')
}
else {
    $markdown.Add('| Status | Rule | File | Symbol | Metric | Delta | Finding |')
    $markdown.Add('| --- | --- | --- | --- | ---: | ---: | --- |')
    foreach ($finding in @($newFindings + $worsenedFindings | Sort-Object status, rule, path, symbol)) {
        $markdown.Add("| $($finding.status) | $($finding.rule) | ``$($finding.path)`` | ``$($finding.symbol)`` | $($finding.observedMetric) | $($finding.metricDelta) | ``$($finding.id)`` |")
    }
}
$markdown.Add('')
$markdown.Add('## Group overview')
$markdown.Add('')
$markdown.Add('| Group | Rule | Classification | Status | Findings | Raw |')
$markdown.Add('| --- | --- | --- | --- | ---: | ---: |')
foreach ($group in $groups) {
    $markdown.Add("| ``$($group.id)`` | $($group.rule) | $($group.classification) | $($group.status) | $(@($group.findingIds).Count) | $(@($group.rawFindingIds).Count) |")
}
$markdown.Add('')
$markdown.Add('## Traceability')
$markdown.Add('')
foreach ($group in $groups) {
    $markdown.Add("<details><summary><code>$($group.id)</code> - $($group.title)</summary>")
    $markdown.Add('')
    foreach ($member in @($group.members)) {
        $rawIds = @($member.rawFindingIds) -join ', '
        $markdown.Add("- ``$($member.findingId)`` - $($member.rule) - ``$($member.path)`` - ``$($member.symbol)`` - raw: ``$rawIds``")
    }
    $markdown.Add('')
    $markdown.Add('</details>')
    $markdown.Add('')
}
Set-Content -LiteralPath $markdownPath -Value $markdown -Encoding utf8NoBOM

Write-Host "Legacy CA report: $jsonPath"
Write-Host "Legacy CA summary: $markdownPath"
Write-Host "raw=$($rawFindings.Count) logical=$($findings.Count) groups=$($groups.Count) new=$($newFindings.Count) worsened=$($worsenedFindings.Count)"
