[CmdletBinding()]
param(
    [string]$GitRef = $env:GITHUB_REF,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

$versionPath = Join-Path $RepositoryRoot "version.txt"
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf))
{
    throw "The single version source was not found: $versionPath"
}

$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$')
{
    throw "version.txt must contain one stable semantic version in major.minor.patch form."
}

if (-not [string]::IsNullOrWhiteSpace($GitRef) -and
    $GitRef.StartsWith("refs/tags/", [StringComparison]::Ordinal))
{
    $expectedTagRef = "refs/tags/v$version"
    if (-not [string]::Equals($GitRef, $expectedTagRef, [StringComparison]::Ordinal))
    {
        throw "Release tag '$GitRef' does not match version.txt ('$expectedTagRef')."
    }
}

Write-Output "Release version contract passed for $version."
