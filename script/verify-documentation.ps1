[CmdletBinding()]
param(
    [switch]$Verify,
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

function Get-DocumentationFailures {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string[]]$RelativeDocuments
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($relativeDocument in $RelativeDocuments) {
        $documentPath = Join-Path $resolvedRoot $relativeDocument
        if (-not (Test-Path -LiteralPath $documentPath -PathType Leaf)) {
            $failures.Add("missing documentation owner: $relativeDocument")
            continue
        }

        $content = Get-Content -LiteralPath $documentPath -Raw
        $documentDirectory = Split-Path -Parent $documentPath

        foreach ($match in [regex]::Matches($content, '\[[^\]]+\]\((?<target>[^)]+)\)')) {
            $target = $match.Groups['target'].Value.Trim()
            if ($target -match '^(?:https?://|mailto:|#)') {
                continue
            }

            $pathPart = ($target -split '#', 2)[0]
            if ([string]::IsNullOrWhiteSpace($pathPart)) {
                continue
            }

            $resolvedTarget = [System.IO.Path]::GetFullPath(
                (Join-Path $documentDirectory $pathPart))
            if (-not $resolvedTarget.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                $failures.Add("reference escapes repository: $relativeDocument -> $target")
                continue
            }

            if (-not (Test-Path -LiteralPath $resolvedTarget)) {
                $failures.Add("broken authority reference: $relativeDocument -> $target")
            }
        }

        $inlinePathPattern =
            '`(?<target>(?:AGENTS\.md|ARCHITECTURE\.md|README\.md|' +
            '(?:\.github|docs|script|src|tests|benchmarks|tools|DownKyi|DownKyi\.Core)' +
            '[/\\][^`\r\n\s,;:]+))`'
        foreach ($match in [regex]::Matches($content, $inlinePathPattern)) {
            $target = $match.Groups['target'].Value.TrimEnd('.', ')')
            if ($target.Contains('*', [System.StringComparison]::Ordinal) -or
                $target.Contains('<', [System.StringComparison]::Ordinal)) {
                continue
            }

            $resolvedTarget = Join-Path $resolvedRoot ($target -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            if (-not (Test-Path -LiteralPath $resolvedTarget)) {
                $failures.Add("broken repository path: $relativeDocument -> $target")
            }
        }

        foreach ($match in [regex]::Matches(
            $content,
            '(?im)(?:^|[ \t])(?<target>\.?[/\\](?:script|tests|docs|\.github)[/\\][^\s`]+)')) {
            $target = $match.Groups['target'].Value.TrimEnd('.', ',', ';', ')')
            if ($target.Contains('*', [System.StringComparison]::Ordinal) -or
                $target.Contains('<', [System.StringComparison]::Ordinal)) {
                continue
            }

            $repositoryRelative = $target
            if ($repositoryRelative.StartsWith('./', [System.StringComparison]::Ordinal) -or
                $repositoryRelative.StartsWith('.\', [System.StringComparison]::Ordinal)) {
                $repositoryRelative = $repositoryRelative.Substring(2)
            }
            elseif ($repositoryRelative.StartsWith('/', [System.StringComparison]::Ordinal) -or
                    $repositoryRelative.StartsWith('\', [System.StringComparison]::Ordinal)) {
                $repositoryRelative = $repositoryRelative.Substring(1)
            }
            $resolvedTarget = Join-Path $resolvedRoot $repositoryRelative
            if (-not (Test-Path -LiteralPath $resolvedTarget)) {
                $failures.Add("broken canonical command path: $relativeDocument -> $target")
            }
        }

        if ($content -match '(?im)^\s*dotnet\s+test\s+.*DownKyi\.sln') {
            $failures.Add(
                "stale canonical command: $relativeDocument uses direct solution-wide dotnet test")
        }

        if ($content.Contains('refactoring-live-plan.md', [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("retired documentation owner reference: $relativeDocument")
        }
    }

    return $failures.ToArray()
}

function Invoke-DocumentationVerifierSelfTest {
    $fixtureRoot = Join-Path (
        [System.IO.Path]::GetTempPath()) (
        "downkyi-documentation-verifier-" + [System.Guid]::NewGuid().ToString('N'))

    try {
        New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'script') -Force |
            Out-Null
        Set-Content -LiteralPath (Join-Path $fixtureRoot 'ARCHITECTURE.md') -Value '# Architecture'
        Set-Content -LiteralPath (Join-Path $fixtureRoot 'DownKyi.sln') -Value ''
        Set-Content -LiteralPath (Join-Path $fixtureRoot 'script/test-solution.ps1') -Value ''

        $validContent = @'
# Agent Entry

[Architecture](ARCHITECTURE.md)

```powershell
pwsh ./script/test-solution.ps1
```
'@
        Set-Content -LiteralPath (Join-Path $fixtureRoot 'AGENTS.md') -Value $validContent
        $validFailures = @(Get-DocumentationFailures `
            -RepositoryRoot $fixtureRoot `
            -RelativeDocuments @('AGENTS.md'))
        if ($validFailures.Count -ne 0) {
            throw "Documentation verifier rejected its valid fixture: $($validFailures -join '; ')"
        }

        Set-Content -LiteralPath (Join-Path $fixtureRoot 'AGENTS.md') `
            -Value '[Missing owner](docs/missing-owner.md)'
        $referenceFailures = @(Get-DocumentationFailures `
            -RepositoryRoot $fixtureRoot `
            -RelativeDocuments @('AGENTS.md'))
        if (-not ($referenceFailures | Where-Object { $_ -like 'broken authority reference:*' })) {
            throw 'Documentation verifier accepted a broken authority reference mutation.'
        }

        Set-Content -LiteralPath (Join-Path $fixtureRoot 'AGENTS.md') `
            -Value 'dotnet test ./DownKyi.sln -c Release'
        $commandFailures = @(Get-DocumentationFailures `
            -RepositoryRoot $fixtureRoot `
            -RelativeDocuments @('AGENTS.md'))
        if (-not ($commandFailures | Where-Object { $_ -like 'stale canonical command:*' })) {
            throw 'Documentation verifier accepted a stale canonical command mutation.'
        }

        Write-Host 'Documentation verifier mutation self-test passed.'
    }
    finally {
        if (Test-Path -LiteralPath $fixtureRoot) {
            Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
        }
    }
}

if (-not $Verify -and -not $SelfTest) {
    throw 'Specify -Verify, -SelfTest, or both.'
}

if ($SelfTest) {
    Invoke-DocumentationVerifierSelfTest
}

if ($Verify) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $currentDocuments = @(
        'AGENTS.md',
        'ARCHITECTURE.md',
        'README.md',
        'docs/ai-knowledge-graph.md',
        'docs/maintenance.md',
        'docs/operations/verification-and-rollback.md',
        'docs/testing/README.md',
        'docs/testing/assembly-lifecycle-stability.md',
        'docs/testing/module-boundary-ratchets.md',
        'docs/testing/review-invariant-policy.md',
        'docs/design-docs/README.md',
        'docs/exec-plans/README.md'
    )

    $failures = @(Get-DocumentationFailures `
        -RepositoryRoot $repositoryRoot `
        -RelativeDocuments $currentDocuments)
    if ($failures.Count -ne 0) {
        throw "Documentation verification failed:`n- $($failures -join "`n- ")"
    }

    Write-Host "Documentation verification passed for $($currentDocuments.Count) current documents."
    Write-Host 'No generated Markdown projection is configured; repository inventories remain query-on-demand.'
}
