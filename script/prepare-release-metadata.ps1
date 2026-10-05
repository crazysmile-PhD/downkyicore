[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PreviousReleaseTag,
    [Parameter(Mandatory = $true)][string]$ReleaseNotesPath,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$') {
    throw 'Release version must be a stable semantic version in major.minor.patch form.'
}
if ($PreviousReleaseTag -notmatch '^v(?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$') {
    throw "Previous release tag is not an annotated stable SemVer tag: $PreviousReleaseTag"
}
if ([version]$Version -le [version]$Matches.version) {
    throw "Release version $Version must be greater than $($Matches.version)."
}

$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$tag = "v$Version"
& git -C $repository show-ref --verify --quiet "refs/tags/$tag"
if ($LASTEXITCODE -eq 0) {
    throw "Release tag $tag already exists. Prepare Release never reuses a tag."
}
if ($LASTEXITCODE -ne 1) {
    throw "Unable to determine whether release tag $tag exists."
}

$resolvedNotesPath = (Resolve-Path -LiteralPath $ReleaseNotesPath).Path
$notes = (Get-Content -LiteralPath $resolvedNotesPath -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw 'git-cliff produced empty release notes.'
}
if (-not $notes.StartsWith("## [$Version]", [StringComparison]::Ordinal)) {
    throw "Generated release notes do not start with the target version $Version."
}

$versionPath = Join-Path $repository 'version.txt'
$changelogPath = Join-Path $repository 'CHANGELOG.md'
$changelog = Get-Content -LiteralPath $changelogPath -Raw
$header = '# 更新日志'
if (-not $changelog.StartsWith($header, [StringComparison]::Ordinal)) {
    throw 'CHANGELOG.md does not start with the repository changelog header.'
}

$existingBody = $changelog.Substring($header.Length).TrimStart("`r", "`n")
$updatedChangelog = "$header`n`n$notes`n`n$existingBody"
[IO.File]::WriteAllText($versionPath, "$Version`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($changelogPath, $updatedChangelog, [Text.UTF8Encoding]::new($false))

Write-Output "Prepared release metadata for $tag after $PreviousReleaseTag."
