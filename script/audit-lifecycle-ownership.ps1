[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/assembly-lifecycle/ownership"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$policyPath = Join-Path $repositoryRoot "docs/testing/assembly-lifecycle-owners.json"
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory, (Get-Location).Path)
$sourceRoots = @(
    "DownKyi",
    "DownKyi.Core",
    "src",
    "tests",
    "benchmarks",
    "tools"
)
$rules = [ordered]@{
    module_initializer = '\[ModuleInitializer\]'
    process_exit = '\bProcessExit\b'
    static_initialization = '(?:^\s*static\s+[A-Za-z_][A-Za-z0-9_]*\s*\(|^\s*(?:(?:public|internal|private|protected)\s+)?static\s+(?:readonly\s+)?[^();]+\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:=|;))'
    external_process = '\b(?:Process\.Start\s*\(|new\s+Process\s*(?:\(\s*\)|\{))'
    new_thread = '\bnew\s+Thread\s*\('
    task_run = '\bTask\.Run\s*\('
    dispatcher = '\bDispatcher(?:\.UIThread|Timer)?\b'
    timer = '\b(?:PeriodicTimer|DispatcherTimer|System\.Threading\.Timer|new\s+Timer)\b'
    global_event = '\b(?:AppDomain\.CurrentDomain|TaskScheduler|Console|desktop)\b.*\+='
    host_lifecycle = '\b(?:IHostedService|StartAsync\s*\(|StopAsync\s*\(|RequestShutdownAsync\s*\()'
    sync_wait = '\.Wait\s*\(|GetAwaiter\(\)\.GetResult\(\)'
    bounded_join = '\b[A-Za-z_][A-Za-z0-9_]*thread[A-Za-z0-9_]*\.Join\s*\([^)]'
    unbounded_join = '\b[A-Za-z_][A-Za-z0-9_]*thread[A-Za-z0-9_]*\.Join\s*(?:\(\s*\)|\))'
    bounded_sleep = '\bThread\.Sleep\s*\('
    synchronous_cleanup = '\b(?:Directory|File)\.Delete\s*\('
}

if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) {
    throw "Lifecycle ownership policy was not found: $policyPath"
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$owners = @($policy.owners)
if ($owners.Count -eq 0) {
    throw "Lifecycle ownership policy does not define any owners."
}

function Test-PathPattern {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Pattern
    )

    $wildcard = $Pattern.Replace("**", "*")
    return $Path -like $wildcard
}

function Find-Owner {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    foreach ($owner in $owners) {
        foreach ($pattern in @($owner.paths)) {
            if (Test-PathPattern -Path $RelativePath -Pattern $pattern) {
                return $owner
            }
        }
    }

    return $null
}

$sourceFiles = @(
    foreach ($sourceRoot in $sourceRoots) {
        $fullRoot = Join-Path $repositoryRoot $sourceRoot
        if (Test-Path -LiteralPath $fullRoot -PathType Container) {
            Get-ChildItem -LiteralPath $fullRoot -Filter "*.cs" -File -Recurse |
                Where-Object {
                    $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]'
                }
        }
    }
) | Sort-Object FullName -Unique

$findings = @()
foreach ($file in $sourceFiles) {
    $relativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $file.FullName).
        Replace([System.IO.Path]::DirectorySeparatorChar, '/')
    $owner = Find-Owner -RelativePath $relativePath
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($lineIndex = 0; $lineIndex -lt $lines.Length; $lineIndex++) {
        foreach ($rule in $rules.GetEnumerator()) {
            if ($lines[$lineIndex] -match $rule.Value) {
                $allowed = $null -ne $owner -and
                    @($owner.allowedMechanisms) -contains $rule.Key
                $findings += [pscustomobject]@{
                    mechanism = $rule.Key
                    path = $relativePath
                    line = $lineIndex + 1
                    ownerId = if ($null -eq $owner) { $null } else { $owner.id }
                    owner = if ($null -eq $owner) { $null } else { $owner.owner }
                    allowed = $allowed
                    source = $lines[$lineIndex].Trim()
                }
            }
        }
    }
}

$violations = @($findings | Where-Object { -not $_.allowed })
$summary = @(
    $findings |
        Group-Object mechanism |
        Sort-Object Name |
        ForEach-Object {
            [pscustomobject]@{
                mechanism = $_.Name
                count = $_.Count
                violations = @($_.Group | Where-Object { -not $_.allowed }).Count
            }
        }
)

New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$jsonPath = Join-Path $resolvedOutput "lifecycle-ownership-report.json"
$markdownPath = Join-Path $resolvedOutput "lifecycle-ownership-report.md"
$report = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    commitSha = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    workingTreeDirty = @(& git -C $repositoryRoot status --porcelain).Count -gt 0
    sourceFileCount = $sourceFiles.Count
    ownerCount = $owners.Count
    matchCount = $findings.Count
    violationCount = $violations.Count
    summary = $summary
    owners = $owners
    matches = @($findings)
    violations = $violations
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding utf8

$markdown = [System.Collections.Generic.List[string]]::new()
$markdown.Add("# Assembly Lifecycle Ownership Audit")
$markdown.Add("")
$markdown.Add("- Commit: ``$($report.commitSha)``")
$markdown.Add("- Working tree dirty: ``$($report.workingTreeDirty)``")
$markdown.Add("- Source files: $($report.sourceFileCount)")
$markdown.Add("- Declared owners: $($report.ownerCount)")
$markdown.Add("- Lifecycle matches: $($report.matchCount)")
$markdown.Add("- Policy violations: $($report.violationCount)")
$markdown.Add("")
$markdown.Add("| Mechanism | Matches | Violations |")
$markdown.Add("| --- | ---: | ---: |")
foreach ($item in $summary) {
    $markdown.Add("| $($item.mechanism) | $($item.count) | $($item.violations) |")
}

$markdown.Add("")
$markdown.Add("## Violations")
$markdown.Add("")
if ($violations.Count -eq 0) {
    $markdown.Add("None.")
}
else {
    foreach ($violation in $violations) {
        $markdown.Add(
            "- ``$($violation.path):$($violation.line)`` " +
            "``$($violation.mechanism)`` owner=``$($violation.ownerId ?? 'unassigned')``")
    }
}

$markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8
Write-Host "Lifecycle ownership report: $markdownPath"
Write-Host "Lifecycle ownership matches: $($findings.Count); violations: $($violations.Count)"

if ($violations.Count -gt 0) {
    throw "Lifecycle ownership policy found $($violations.Count) violation(s)."
}
