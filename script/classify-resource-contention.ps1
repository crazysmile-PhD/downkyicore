[CmdletBinding()]
param(
    [string]$ResultsDirectory = "./TestResults",
    [string]$OutputPath = "./artifacts/test-flight-recorder/resource-contention-classification.json"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory, $repositoryRoot)
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath, $repositoryRoot)

if (-not (Test-Path -LiteralPath $resolvedResultsDirectory -PathType Container)) {
    Write-Host "Targeted Resource Forensics: no test-results directory to classify."
    return
}

$signatures = [ordered]@{
    SharingViolation = '(?i)sharing\s*violation|ERROR_SHARING_VIOLATION|0x80070020'
    AccessDenied = '(?i)access (?:is )?denied|unauthorizedaccessexception|EACCES'
    ResourceBusy = '(?i)resource busy|device or resource busy|text file busy|EBUSY|ETXTBSY'
    InUse = '(?i)being used by another process|file .* is in use|database is locked|cannot delete|could not delete'
    RenameMoveOverwrite = '(?i)(?:rename|move|overwrite).*(?:failed|denied|in use|busy|locked)'
}

$matchedSignatures = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$candidates = Get-ChildItem -LiteralPath $resolvedResultsDirectory -Recurse -File |
    Where-Object { $_.Extension -in '.trx', '.log', '.txt' }
foreach ($candidate in $candidates) {
    try {
        $content = Get-Content -LiteralPath $candidate.FullName -Raw
        foreach ($entry in $signatures.GetEnumerator()) {
            if ($content -match $entry.Value) {
                [void]$matchedSignatures.Add([string]$entry.Key)
                [void]$files.Add(
                    [IO.Path]::GetRelativePath($repositoryRoot, $candidate.FullName).Replace('\', '/'))
            }
        }
    }
    catch {
        Write-Host "Targeted Resource Forensics: skipped unreadable result file '$($candidate.Name)'."
    }
}

if ($matchedSignatures.Count -eq 0) {
    Write-Host "Targeted Resource Forensics: failure was not classified as resource contention."
    return
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$artifact = [pscustomobject][ordered]@{
    schemaVersion = 1
    classification = 'TargetedResourceContention'
    observedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT
    job = $env:GITHUB_JOB
    signatures = [string[]]@($matchedSignatures | Sort-Object)
    resultFiles = [string[]]@($files | Sort-Object)
    nextAction = 'Identify the exact resource and failed operation, then enable the narrow Targeted Resource Forensics probe for that resource. Do not blanket-enable ETW or rerun unchanged evidence.'
}
$artifact | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resolvedOutputPath -Encoding utf8
Write-Warning (
    "Targeted Resource Forensics: resource-contention signature detected. " +
    "Inspect $([IO.Path]::GetRelativePath($repositoryRoot, $resolvedOutputPath).Replace('\', '/')); " +
    "target the exact resource and operation before enabling a recorder.")
