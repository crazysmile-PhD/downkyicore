Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-HistoricalVersions {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Selector
    )

    $allVersions = @(0..24 | ForEach-Object { "1.0.$_" })
    if ([string]::Equals($Selector, 'all', [StringComparison]::OrdinalIgnoreCase)) {
        return $allVersions
    }

    $normalized = $Selector.Trim()
    if ($normalized.StartsWith('archive/v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring('archive/v'.Length)
    }
    elseif ($normalized.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(1)
    }

    if ($allVersions -notcontains $normalized) {
        throw "Historical version must be 'all' or one of 1.0.0 through 1.0.24. Received: $Selector"
    }

    return @($normalized)
}

function Get-HistoricalPlatforms {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Selector
    )

    $catalog = @(
        [pscustomobject]@{
            Name = 'windows-x64'
            Rid = 'win-x64'
            Runner = 'windows-latest'
            Extension = 'zip'
        },
        [pscustomobject]@{
            Name = 'macos-x64'
            Rid = 'osx-x64'
            Runner = 'macos-15-intel'
            Extension = 'dmg'
        },
        [pscustomobject]@{
            Name = 'linux-x64'
            Rid = 'linux-x64'
            Runner = 'ubuntu-22.04'
            Extension = 'AppImage'
        }
    )

    if ([string]::Equals($Selector, 'all', [StringComparison]::OrdinalIgnoreCase)) {
        return $catalog
    }

    $platformMatches = @($catalog | Where-Object {
        [string]::Equals($_.Name, $Selector.Trim(), [StringComparison]::OrdinalIgnoreCase)
    })
    if ($platformMatches.Count -ne 1) {
        throw "Historical platform must be 'all', windows-x64, macos-x64, or linux-x64. Received: $Selector"
    }

    return $platformMatches
}

function Get-HistoricalPackageName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Platform
    )

    switch ($Platform) {
        'windows-x64' { return "DownKyi-$Version-historical-win-x64.zip" }
        'macos-x64' { return "DownKyi-$Version-historical-osx-x64.dmg" }
        'linux-x64' { return "DownKyi-$Version-historical-linux-x64.AppImage" }
        default { throw "Unsupported historical platform: $Platform" }
    }
}

function Get-HistoricalAssetNames {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$Platform
    )

    $packageName = Get-HistoricalPackageName -Version $Version -Platform $Platform
    return @(
        $packageName,
        "$packageName.sha256",
        "$packageName.manifest.json"
    )
}

function Assert-HistoricalTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$Version
    )

    $tag = "archive/v$Version"
    $commit = (& git -C $RepositoryRoot rev-parse --verify "$tag^{commit}" 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
        throw "Required historical tag is missing or invalid: $tag"
    }

    $versionText = (& git -C $RepositoryRoot show "$tag^{commit}:version.txt" 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $versionText -ne $Version) {
        throw "$tag points to version.txt '$versionText', expected '$Version'."
    }

    return $commit
}

function Get-RepositoryRoot {
    [CmdletBinding()]
    param()

    return [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
}

function Write-GitHubOutputValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    Add-Content -LiteralPath $Path -Value "$Name=$Value" -Encoding utf8
}

function ConvertTo-SafeFailureText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [string[]]$PathsToRedact = @()
    )

    $safe = $Text
    foreach ($path in $PathsToRedact) {
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            $safe = $safe.Replace($path, '<temporary-path>', [StringComparison]::OrdinalIgnoreCase)
        }
    }

    $safe = $safe -replace '(?i)(token|password|secret)=\S+', '$1=<redacted>'
    $safe = ($safe -replace '[\r\n]+', ' ').Trim()
    if ($safe.Length -gt 600) {
        $safe = $safe.Substring(0, 600) + '...'
    }
    return $safe
}
