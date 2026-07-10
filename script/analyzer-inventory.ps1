param(
    [Parameter(Mandatory = $true)]
    [string]$BuildLog,

    [Parameter(Mandatory = $true)]
    [string]$SarifDirectory,

    [Parameter(Mandatory = $true)]
    [string]$SummaryPath,

    [Parameter(Mandatory = $true)]
    [string]$DetailCsvPath,

    [string]$RepositoryRoot = (Get-Location).Path,

    [string]$Title = '.NET Analyzer Diagnostic Inventory'
)

$ErrorActionPreference = 'Stop'

function Get-RelativePath {
    param([string]$Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetRelativePath($RepositoryRoot, $Path)
    }

    return $Path
}

function Test-PathPattern {
    param(
        [string[]]$Paths,
        [string]$Pattern
    )

    return [bool]($Paths | Where-Object { $_ -match $Pattern } | Select-Object -First 1)
}

if (-not (Test-Path -LiteralPath $BuildLog -PathType Leaf)) {
    throw "Build log not found: $BuildLog"
}

if (-not (Test-Path -LiteralPath $SarifDirectory -PathType Container)) {
    throw "SARIF directory not found: $SarifDirectory"
}

$ruleMetadata = @{}
Get-ChildItem -LiteralPath $SarifDirectory -Filter '*.sarif' -File | ForEach-Object {
    $sarif = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -Depth 100
    foreach ($run in @($sarif.runs)) {
        foreach ($property in @($run.rules.PSObject.Properties)) {
            $rule = $property.Value
            if ($rule.id -like 'CA*') {
                $ruleMetadata[$rule.id] = [pscustomobject]@{
                    Category = $rule.properties.category
                    HelpUri = $rule.helpUri
                    Description = $rule.shortDescription
                }
            }
        }
    }
}

$diagnosticPattern = '^\s*(?:\d+>)?(?<file>.+?)\((?<line>\d+),(?<column>\d+)\): warning (?<id>CA\d+): (?<message>.*?) \(https?://.*?\) \[(?<project>.+?\.csproj)\]$'
$diagnosticsByKey = @{}

foreach ($line in Get-Content -LiteralPath $BuildLog) {
    if ($line -notmatch $diagnosticPattern) {
        continue
    }

    $file = Get-RelativePath $matches.file
    $projectPath = Get-RelativePath $matches.project
    $key = @(
        $matches.id,
        $file,
        $matches.line,
        $matches.column,
        $matches.message,
        $projectPath
    ) -join '|'

    if (-not $diagnosticsByKey.ContainsKey($key)) {
        $metadata = $ruleMetadata[$matches.id]
        $diagnosticsByKey[$key] = [pscustomobject]@{
            Rule = $matches.id
            Category = if ($metadata) { $metadata.Category } else { 'Unknown' }
            Project = [IO.Path]::GetFileNameWithoutExtension($projectPath)
            ProjectPath = $projectPath
            File = $file
            Line = [int]$matches.line
            Column = [int]$matches.column
            Message = $matches.message
        }
    }
}

$diagnostics = @($diagnosticsByKey.Values | Sort-Object Rule, Project, File, Line, Column, Message)
if ($diagnostics.Count -eq 0) {
    throw 'No CA diagnostics were parsed from the build log.'
}

$details = foreach ($diagnostic in $diagnostics) {
    $paths = @($diagnostic.File)
    $category = $diagnostic.Category
    [pscustomobject]@{
        Rule = $diagnostic.Rule
        Category = $category
        Project = $diagnostic.Project
        ProjectPath = $diagnostic.ProjectPath
        File = $diagnostic.File
        Line = $diagnostic.Line
        Column = $diagnostic.Column
        Message = $diagnostic.Message
        PublicApiRisk = $category -in @('Design', 'Naming', 'Usage')
        DataFormatRisk = Test-PathPattern $paths '(?i)(Models|Settings|Storage|Json|Nfo|Cookie|DownloadStorage)'
        DatabaseRisk = Test-PathPattern $paths '(?i)(Storage[\\/]Database|DownloadStorage)'
        SerializationRisk = Test-PathPattern $paths '(?i)(Models|Settings|Json|Nfo|Cookie|Serialization|DownloadStorage)'
        UiBindingRisk = Test-PathPattern $paths '(?i)(ViewModels|Views|CustomControl|Converter|App\.axaml)'
        ExternalProtocolRisk = Test-PathPattern $paths '(?i)(BiliApi|Aria2cNet|FFMpeg)'
    }
}

$detailDirectory = Split-Path -Parent $DetailCsvPath
if ($detailDirectory) {
    New-Item -ItemType Directory -Path $detailDirectory -Force | Out-Null
}
$details | Export-Csv -LiteralPath $DetailCsvPath -NoTypeInformation -Encoding utf8

$categoryCounts = $details |
    Group-Object Category |
    Sort-Object Name

$ruleRows = foreach ($ruleGroup in $details | Group-Object Rule | Sort-Object Name) {
    $items = @($ruleGroup.Group)
    $metadata = $ruleMetadata[$ruleGroup.Name]
    $projects = @($items.Project | Sort-Object -Unique)
    $files = @($items.File | Sort-Object -Unique)
    $riskNames = [System.Collections.Generic.List[string]]::new()
    if ($items.PublicApiRisk -contains $true) { $riskNames.Add('public API') }
    if ($items.DataFormatRisk -contains $true) { $riskNames.Add('data format') }
    if ($items.DatabaseRisk -contains $true) { $riskNames.Add('database') }
    if ($items.SerializationRisk -contains $true) { $riskNames.Add('serialization') }
    if ($items.UiBindingRisk -contains $true) { $riskNames.Add('UI binding') }
    if ($items.ExternalProtocolRisk -contains $true) { $riskNames.Add('external protocol') }

    $fileSample = $files | Select-Object -First 3
    [pscustomobject]@{
        Rule = $ruleGroup.Name
        Count = $items.Count
        Category = $items[0].Category
        Projects = $projects -join ', '
        FileCount = $files.Count
        FileSample = ($fileSample -join '<br>')
        CompatibilityRisk = if ($riskNames.Count -gt 0) { $riskNames -join ', ' } else { 'none detected by path heuristic' }
        HelpUri = if ($metadata) { $metadata.HelpUri } else { "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/$($ruleGroup.Name.ToLowerInvariant())" }
    }
}

$summaryDirectory = Split-Path -Parent $SummaryPath
if ($summaryDirectory) {
    New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
}

$markdown = [System.Collections.Generic.List[string]]::new()
$detailFileName = [IO.Path]::GetFileName($DetailCsvPath)
$markdown.Add("# $Title")
$markdown.Add('')
$markdown.Add("Generated from a clean Release build with `.NET` analyzers enabled, `AnalysisMode=All`, and code style enforcement. Duplicate MSBuild summary lines are removed by rule, location, message, and project.")
$markdown.Add('')
$markdown.Add("- Unique diagnostics: **$($details.Count)**")
$markdown.Add("- Rules: **$($ruleRows.Count)**")
$markdown.Add("- Projects: **$(@($details.Project | Sort-Object -Unique).Count)**")
$markdown.Add("- Full file and line inventory: [$detailFileName]($detailFileName)")
$markdown.Add('')
$markdown.Add('Compatibility flags are conservative review hints derived from the affected paths and rule category. They do not authorize mechanical API, schema, database, serialization, binding, or protocol changes.')
$markdown.Add('')
$markdown.Add('## Categories')
$markdown.Add('')
$markdown.Add('| Category | Count |')
$markdown.Add('| --- | ---: |')
foreach ($category in $categoryCounts) {
    $markdown.Add("| $($category.Name) | $($category.Count) |")
}
$markdown.Add('')
$markdown.Add('## Rules')
$markdown.Add('')
$markdown.Add('| Rule | Count | Category | Projects | Files | Sample files | Compatibility review |')
$markdown.Add('| --- | ---: | --- | --- | ---: | --- | --- |')
foreach ($row in $ruleRows) {
    $ruleLink = "[$($row.Rule)]($($row.HelpUri))"
    $markdown.Add("| $ruleLink | $($row.Count) | $($row.Category) | $($row.Projects) | $($row.FileCount) | $($row.FileSample) | $($row.CompatibilityRisk) |")
}
$markdown.Add('')
$markdown.Add('## Required Fix Order')
$markdown.Add('')
$markdown.Add('1. Security, data corruption, deadlock, resource leak, and incorrect-result findings.')
$markdown.Add('2. Async, cancellation, disposal, exception handling, and thread-safety findings.')
$markdown.Add('3. Performance and unnecessary-allocation findings.')
$markdown.Add('4. Public API and collection-design findings after compatibility review.')
$markdown.Add('5. Naming, globalization, maintainability, and style findings.')

$markdown | Set-Content -LiteralPath $SummaryPath -Encoding utf8

Write-Output "Diagnostics: $($details.Count)"
Write-Output "Rules: $($ruleRows.Count)"
Write-Output "Summary: $SummaryPath"
Write-Output "Details: $DetailCsvPath"
