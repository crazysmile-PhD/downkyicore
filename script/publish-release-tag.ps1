[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SubjectSha,
    [string]$GitRef = $env:GITHUB_REF,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$ValidateOnly,
    [switch]$AllowExistingExactTag
)

$ErrorActionPreference = 'Stop'
$subject = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$versionValidator = Join-Path $PSScriptRoot 'validate-release-version.ps1'
$releaseSubjectValidator = Join-Path $PSScriptRoot 'validate-release-subject.ps1'

function Invoke-SubjectGit {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $output = & git -C $subject @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $diagnostic = ($output -join [Environment]::NewLine).
            Replace($subject, '<release-subject>', [StringComparison]::OrdinalIgnoreCase)
        $portableSubject = $subject.Replace('\', '/')
        if (-not [string]::Equals($portableSubject, $subject, [StringComparison]::Ordinal)) {
            $diagnostic = $diagnostic.Replace(
                $portableSubject,
                '<release-subject>',
                [StringComparison]::OrdinalIgnoreCase)
        }

        throw "git in <release-subject> $($Arguments -join ' ') failed: $diagnostic"
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function Get-RemoteTagState {
    param([Parameter(Mandatory = $true)][string]$Tag)

    $tagRef = "refs/tags/$Tag"
    $peeledRef = "$tagRef^{}"
    $output = Invoke-SubjectGit ls-remote --tags origin $tagRef $peeledRef
    $lines = @($output -split '\r?\n') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $entries = @{}
    foreach ($line in $lines) {
        $parts = $line -split '\s+', 2
        if ($parts.Count -ne 2) {
            throw "Unexpected remote tag record: $line"
        }

        $entries[$parts[1]] = $parts[0]
    }

    return $entries
}

if (-not [string]::Equals($GitRef, 'refs/heads/main', [StringComparison]::Ordinal)) {
    throw "One-click release publication must be dispatched from refs/heads/main; received '$GitRef'."
}

$version = (Get-Content -LiteralPath (Join-Path $subject 'version.txt') -Raw).Trim()
$tag = "v$version"
& $versionValidator -RepositoryRoot $subject -GitRef "refs/tags/$tag" | Out-Null

$head = Invoke-SubjectGit rev-parse 'HEAD^{commit}'
if (-not [string]::Equals($head, $SubjectSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release subject HEAD is $head; expected $SubjectSha."
}

$trackedChanges = Invoke-SubjectGit status --porcelain --untracked-files=no
if ($trackedChanges) {
    throw "Release subject contains tracked changes:`n$trackedChanges"
}

Invoke-SubjectGit fetch --no-tags origin '+refs/heads/main:refs/remotes/origin/main' | Out-Null
$mainCommit = Invoke-SubjectGit rev-parse 'refs/remotes/origin/main^{commit}'
if (-not [string]::Equals($SubjectSha, $mainCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release subject $SubjectSha does not equal current main $mainCommit."
}

$tagRef = "refs/tags/$tag"
$peeledRef = "$tagRef^{}"
$remoteTag = Get-RemoteTagState -Tag $tag
if ($remoteTag.Count -ne 0) {
    if (-not $AllowExistingExactTag) {
        throw "Release tag '$tag' already exists. Existing release tags are immutable."
    }

    if (-not $remoteTag.ContainsKey($tagRef) -or -not $remoteTag.ContainsKey($peeledRef)) {
        throw "Existing release tag '$tag' must be an annotated tag."
    }

    if (-not [string]::Equals($remoteTag[$peeledRef], $SubjectSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing release tag '$tag' resolves to $($remoteTag[$peeledRef]); expected $SubjectSha."
    }

    Write-Output "Validated existing annotated release tag $tag at $SubjectSha for workflow retry."
    return
}

$localTag = Invoke-SubjectGit tag --list $tag
if ($localTag) {
    throw "Local release tag '$tag' exists while the remote tag is absent."
}

if ($ValidateOnly) {
    Write-Output "Validated one-click release admission for $tag at $SubjectSha."
    return
}

$createdLocalTag = $false
try {
    Invoke-SubjectGit -Arguments @(
        '-c', 'user.name=github-actions[bot]',
        '-c', 'user.email=41898282+github-actions[bot]@users.noreply.github.com',
        'tag', '-a', $tag, $SubjectSha, '-m', "Release $tag"
    ) | Out-Null
    $createdLocalTag = $true

    & $releaseSubjectValidator `
        -SubjectDirectory $subject `
        -ReleaseVersion $tag `
        -SubjectSha $SubjectSha | Out-Null

    Invoke-SubjectGit push origin "$tagRef`:$tagRef" | Out-Null
}
catch {
    if ($createdLocalTag) {
        & git -C $subject tag --delete $tag *> $null
    }

    throw
}

$publishedTag = Get-RemoteTagState -Tag $tag
if (-not $publishedTag.ContainsKey($tagRef) -or -not $publishedTag.ContainsKey($peeledRef)) {
    throw "Published release tag '$tag' is not an annotated tag."
}

if (-not [string]::Equals($publishedTag[$peeledRef], $SubjectSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Published release tag '$tag' resolves to $($publishedTag[$peeledRef]); expected $SubjectSha."
}

Write-Output "Published annotated release tag $tag at $SubjectSha."
