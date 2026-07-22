[CmdletBinding()]
param(
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Convert-ToRelativePath {
    param([Parameter(Mandatory)][string]$Path)

    return [IO.Path]::GetRelativePath($repositoryRoot, $Path).Replace("\", "/")
}

function Test-IsBuildOutput {
    param([Parameter(Mandatory)][string]$Path)

    $segments = (Convert-ToRelativePath $Path).Split("/")
    return $segments -contains "bin" -or $segments -contains "obj"
}

function Get-ProductionFiles {
    param([Parameter(Mandatory)][string[]]$Patterns)

    $roots = @(
        (Join-Path $repositoryRoot "DownKyi"),
        (Join-Path $repositoryRoot "DownKyi.Core"),
        (Join-Path $repositoryRoot "src")
    )

    return @(
        foreach ($root in $roots) {
            foreach ($pattern in $Patterns) {
                Get-ChildItem -LiteralPath $root -Recurse -File -Filter $pattern |
                    Where-Object { -not (Test-IsBuildOutput $_.FullName) }
            }
        }
    )
}

function Get-ProjectSummary {
    param([Parameter(Mandatory)][string]$ProjectPath)

    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
    $projectDirectory = Split-Path -Parent $ProjectPath
    $references = @(
        $project.Project.ItemGroup.ProjectReference |
            ForEach-Object { $_.Include } |
            Where-Object { $_ } |
            ForEach-Object {
                Convert-ToRelativePath ([IO.Path]::GetFullPath((Join-Path $projectDirectory $_)))
            }
    )
    $packages = @(
        $project.Project.ItemGroup.PackageReference |
            ForEach-Object { $_.Include } |
            Where-Object { $_ }
    )

    return [ordered]@{
        path              = Convert-ToRelativePath $ProjectPath
        projectReferences = $references
        packageReferences = $packages
    }
}

function Get-SourceRootMetrics {
    param([Parameter(Mandatory)][string]$RelativeRoot)

    $root = Join-Path $repositoryRoot $RelativeRoot
    $files = @(
        Get-ChildItem -LiteralPath $root -Recurse -File -Include "*.cs", "*.axaml" |
            Where-Object { -not (Test-IsBuildOutput $_.FullName) }
    )
    $lineCount = 0
    foreach ($file in $files) {
        $lineCount += (Get-Content -LiteralPath $file.FullName).Count
    }

    return [ordered]@{
        root  = $RelativeRoot.Replace("\", "/")
        files = $files.Count
        lines = $lineCount
    }
}

function Get-TypeDeclarations {
    $namespacePattern = '(?m)^\s*namespace\s+([A-Za-z_][\w\.]*)\s*[;{]'
    $typePattern = '(?m)^\s*(?:public|internal|protected|private|file)?\s*' +
        '(?:sealed\s+|abstract\s+|static\s+|partial\s+)*' +
        '(?:class|record(?:\s+class|\s+struct)?|struct|interface|enum)\s+' +
        '([A-Za-z_][\w]*)'
    $declarations = @()

    foreach ($file in Get-ProductionFiles -Patterns "*.cs") {
        $source = Get-Content -LiteralPath $file.FullName -Raw
        $namespaceMatch = [regex]::Match($source, $namespacePattern)
        if (-not $namespaceMatch.Success) {
            continue
        }

        foreach ($match in [regex]::Matches($source, $typePattern)) {
            $name = $match.Groups[1].Value
            $declarations += [pscustomobject]@{
                name      = $name
                namespace = $namespaceMatch.Groups[1].Value
                fullName  = "$($namespaceMatch.Groups[1].Value).$name"
                path      = Convert-ToRelativePath $file.FullName
            }
        }
    }

    return $declarations
}

$projects = @(
    Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Filter "*.csproj" |
        Where-Object { -not (Test-IsBuildOutput $_.FullName) } |
        Where-Object { (Convert-ToRelativePath $_.FullName) -notmatch '^(tests|benchmarks)/' } |
        Sort-Object FullName |
        ForEach-Object { Get-ProjectSummary $_.FullName }
)

$sourceRoots = @(
    "DownKyi",
    "DownKyi.Core",
    "src/DownKyi.Domain",
    "src/DownKyi.Application",
    "src/DownKyi.Infrastructure",
    "src/DownKyi.Desktop"
)
$sourceMetrics = @($sourceRoots | ForEach-Object { Get-SourceRootMetrics $_ })

$coreRoot = Join-Path $repositoryRoot "DownKyi.Core"
$coreUiDependencies = @(
    Get-ChildItem -LiteralPath $coreRoot -Recurse -File |
        Where-Object { -not (Test-IsBuildOutput $_.FullName) } |
        Where-Object {
            if ($_.Extension -eq ".axaml") {
                return $true
            }

            if ($_.Extension -notin @(".cs", ".csproj")) {
                return $false
            }

            return (Get-Content -LiteralPath $_.FullName -Raw).Contains("Avalonia")
        } |
        ForEach-Object { Convert-ToRelativePath $_.FullName } |
        Sort-Object
)

$servicesRoot = Join-Path $repositoryRoot "DownKyi/Services"
$presentationBoundContracts = @(
    Get-ChildItem -LiteralPath $servicesRoot -Recurse -File -Filter "I*.cs" |
        Where-Object {
            (Get-Content -LiteralPath $_.FullName -Raw).Contains("DownKyi.ViewModels")
        } |
        ForEach-Object { Convert-ToRelativePath $_.FullName } |
        Sort-Object
)

$typeDeclarations = @(Get-TypeDeclarations)
$duplicateSimpleNames = @(
    $typeDeclarations |
        Group-Object name |
        ForEach-Object {
            $fullNames = @($_.Group.fullName | Sort-Object -Unique)
            if ($fullNames.Count -gt 1) {
                [ordered]@{
                    name      = $_.Name
                    fullNames = $fullNames
                }
            }
        } |
        Where-Object { $_ } |
        Sort-Object name
)

$genericNames = @("Constant", "StorageManager", "Utils")
$genericTypeNames = @(
    $typeDeclarations |
        Where-Object { $_.name -in $genericNames } |
        Select-Object name, fullName, path -Unique |
        Sort-Object path, name
)

$typePattern = '(?m)^\s*(?:public|internal|protected|private|file)?\s*' +
    '(?:sealed\s+|abstract\s+|static\s+|partial\s+)*' +
    '(?:class|record(?:\s+class|\s+struct)?|struct|interface|enum)\s+' +
    '([A-Za-z_][\w]*)'
$fileTypeMismatches = @(
    foreach ($file in Get-ProductionFiles -Patterns "*.cs") {
        if ($file.Name -match '\.(g|Designer)\.cs$') {
            continue
        }

        $source = Get-Content -LiteralPath $file.FullName -Raw
        $typeNames = @(
            [regex]::Matches($source, $typePattern) |
                ForEach-Object { $_.Groups[1].Value } |
                Sort-Object -Unique
        )
        if ($typeNames.Count -eq 0) {
            continue
        }

        $fileName = [IO.Path]::GetFileNameWithoutExtension($file.Name)
        if ($fileName.EndsWith(".axaml", [StringComparison]::OrdinalIgnoreCase)) {
            $fileName = $fileName.Substring(0, $fileName.Length - ".axaml".Length)
        }

        $matches = @(
            $typeNames |
                Where-Object {
                    $fileName -ceq $_ -or
                    $fileName.StartsWith("$_.", [StringComparison]::Ordinal)
                }
        )
        if ($matches.Count -eq 0) {
            [ordered]@{
                path      = Convert-ToRelativePath $file.FullName
                fileName  = $fileName
                typeNames = $typeNames
            }
        }
    }
)

$oversizedFiles = @(
    Get-ProductionFiles -Patterns @("*.cs", "*.axaml") |
        ForEach-Object {
            $lines = (Get-Content -LiteralPath $_.FullName).Count
            if ($lines -gt 500) {
                [ordered]@{
                    path  = Convert-ToRelativePath $_.FullName
                    lines = $lines
                }
            }
        } |
        Where-Object { $_ } |
        Sort-Object lines -Descending
)

$downloadRoot = Join-Path $repositoryRoot "DownKyi/Services/Download"
$downloadSources = @(Get-ChildItem -LiteralPath $downloadRoot -Recurse -File -Filter "*.cs")
$downloadingItemReferences = 0
$domainTaskReferences = 0
foreach ($file in $downloadSources) {
    $source = Get-Content -LiteralPath $file.FullName -Raw
    $downloadingItemReferences += [regex]::Matches($source, '\bDownloadingItem\b').Count
    $domainTaskReferences += [regex]::Matches($source, '\b(?:DomainDownloadTask|DownloadTaskId)\b').Count
}

$orchestratorSource = Get-Content -LiteralPath (Join-Path $downloadRoot "DownloadOrchestrator.cs") -Raw
$pipelinePath = Join-Path $downloadRoot "DownloadPipeline.cs"
$pipelineSource = Get-Content -LiteralPath $pipelinePath -Raw
$httpClientSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "DownKyi.Core/BiliApi/BilibiliHttpClient.cs") -Raw
$webClientSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "DownKyi.Core/BiliApi/WebClient.cs") -Raw
$collectionPath = Join-Path $repositoryRoot "DownKyi/ViewModels/ImmutableObservableCollection.cs"
$collectionSource = Get-Content -LiteralPath $collectionPath -Raw
$logProviderPath = Join-Path $repositoryRoot "DownKyi.Core/Logging/ApplicationLogProvider.cs"

$requiredKnowledgePaths = @(
    "AGENTS.md",
    "ARCHITECTURE.md",
    "docs/design-docs",
    "docs/exec-plans",
    "docs/product-specs",
    "docs/testing",
    "docs/operations"
)
$knowledgeStatus = @(
    foreach ($path in $requiredKnowledgePaths) {
        [ordered]@{
            path   = $path
            exists = Test-Path -LiteralPath (Join-Path $repositoryRoot $path)
        }
    }
)

$commitSha = "unknown"
try {
    $commitSha = (git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
}
catch {
    $commitSha = "unknown"
}

$result = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    commitSha = $commitSha
    projects = $projects
    sourceMetrics = $sourceMetrics
    findings = [ordered]@{
        coreUiDependencies = $coreUiDependencies
        presentationBoundServiceContracts = $presentationBoundContracts
        duplicateSimpleNames = $duplicateSimpleNames
        genericTypeNames = $genericTypeNames
        fileTypeMismatches = $fileTypeMismatches
        oversizedFiles = $oversizedFiles
        runtimeBoundary = [ordered]@{
            downloadingItemReferences = $downloadingItemReferences
            domainTaskReferences = $domainTaskReferences
            orchestratorPollsUiCollection =
                $orchestratorSource.Contains("_downloadLists.Downloading") -and
                $orchestratorSource.Contains("Task.Delay")
            orchestratorDelayMilliseconds = if ($orchestratorSource.Contains("FromMilliseconds(500)")) { 500 } else { $null }
            pipelineLines = (Get-Content -LiteralPath $pipelinePath).Count
            pipelineRetryLimit = if ($pipelineSource.Contains("private const int Retry = 5")) { 5 } else { $null }
            httpUsesStaticFacade = $webClientSource.Contains("static BilibiliHttpClient? _client")
            httpUsesSynchronousSend = $httpClientSource.Contains("_httpClient.Send(")
            httpUsesSynchronousRead = $httpClientSource.Contains("reader.ReadToEnd()")
            httpUsesBlockingBackoff = $httpClientSource.Contains("WaitHandle.WaitOne")
            customCollectionUnsupportedMembers = [regex]::Matches(
                $collectionSource,
                'throw\s+new\s+NotImplementedException\s*\(').Count
            applicationLogProviderLines = (Get-Content -LiteralPath $logProviderPath).Count
        }
        knowledgeEnvironment = $knowledgeStatus
    }
}

$json = $result | ConvertTo-Json -Depth 12
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $json
    return
}

$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
}
else {
    Join-Path $repositoryRoot $OutputPath
}
$outputDirectory = Split-Path -Parent $resolvedOutput
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Set-Content -LiteralPath $resolvedOutput -Value $json -Encoding utf8NoBOM
Write-Output $resolvedOutput
