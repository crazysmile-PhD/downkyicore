[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Inventory', 'Windows', 'LinuxX64', 'LinuxArm64', 'MacOSX64', 'MacOSArm64')]
    [string]$Scope,

    [string]$ExpectedVersion,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path

if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'version.txt') -Raw).Trim()
}

$repositoryVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'version.txt') -Raw).Trim()
if ($ExpectedVersion -cne $repositoryVersion) {
    throw "Expected artifact version $ExpectedVersion does not match repository version $repositoryVersion."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactRoot 'pre-release-validation'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

. (Join-Path $PSScriptRoot 'release-package-catalog.ps1')
$packageSpecs = @(Get-ReleasePackageCatalog -ExpectedVersion $ExpectedVersion)

function Assert-ExactNameSet {
    param(
        [string]$Label,
        [string[]]$Actual,
        [string[]]$Expected
    )

    $differences = @(Compare-Object -ReferenceObject @($Expected | Sort-Object) `
            -DifferenceObject @($Actual | Sort-Object) -CaseSensitive)
    if ($Actual.Count -ne $Expected.Count -or $differences.Count -ne 0) {
        $description = ($differences | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join '; '
        throw "$Label inventory does not match the release contract: $description"
    }
}

function Restore-AppImageTransport {
    param([pscustomobject]$Spec)

    if ([string]::IsNullOrWhiteSpace([string]$Spec.Transport)) {
        return
    }

    $archivePath = Join-Path $artifactRoot $Spec.Transport
    $packagePath = Join-Path $artifactRoot $Spec.Name
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        if (Test-Path -LiteralPath $packagePath) {
            throw "Both AppImage transport and extracted package are present: $($Spec.Name)"
        }

        $expectedEntries = @($Spec.Name, "$($Spec.Name).sha256", $Spec.Manifest)
        $archiveEntries = @(& tar -tf $archivePath)
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to inspect AppImage transport: $($Spec.Transport)"
        }
        Assert-ExactNameSet -Label "AppImage transport $($Spec.Transport)" `
            -Actual $archiveEntries -Expected $expectedEntries

        & tar -xf $archivePath -C $artifactRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restore AppImage transport: $($Spec.Transport)"
        }
        foreach ($entry in $expectedEntries) {
            $restoredItem = Get-Item -LiteralPath (Join-Path $artifactRoot $entry) -Force
            if ($null -ne $restoredItem.LinkType) {
                throw "AppImage transport restored a link instead of a regular file: $entry"
            }
        }
        Remove-Item -LiteralPath $archivePath
    }

    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Required AppImage package is missing: $($Spec.Name)"
    }
    if (-not $IsWindows) {
        $mode = [IO.File]::GetUnixFileMode($packagePath)
        if (($mode -band [IO.UnixFileMode]::OtherExecute) -eq 0) {
            throw "Transported AppImage lost non-owner execute permission: $($Spec.Name)"
        }
    }
}

function Assert-PackageIntegrity {
    param([pscustomobject]$Spec)

    $packagePath = Join-Path $artifactRoot $Spec.Name
    $sidecarPath = "$packagePath.sha256"
    $manifestPath = Join-Path $artifactRoot $Spec.Manifest
    foreach ($requiredPath in @($packagePath, $sidecarPath, $manifestPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required downloaded release artifact is missing: $requiredPath"
        }
    }

    $sidecar = Get-Content -LiteralPath $sidecarPath -Raw
    $match = [Text.RegularExpressions.Regex]::Match(
        $sidecar,
        '\A([a-fA-F0-9]{64})  ([^\r\n]+)\r?\n?\z',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        throw "Malformed SHA-256 sidecar: $([IO.Path]::GetFileName($sidecarPath))"
    }
    if ($match.Groups[2].Value -cne $Spec.Name) {
        throw "SHA-256 sidecar names $($match.Groups[2].Value), expected $($Spec.Name)."
    }

    $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    if (-not [String]::Equals($actualHash, $match.Groups[1].Value, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Downloaded release package does not match its SHA-256 sidecar: $($Spec.Name)"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1) {
        throw "Unsupported publish manifest schema for $($Spec.Name)."
    }
    if ([string]$manifest.runtimeIdentifier -cne $Spec.RuntimeIdentifier) {
        throw "Publish manifest runtime identifier does not match $($Spec.Name)."
    }
    if ([string]$manifest.applicationVersion -cne $ExpectedVersion) {
        throw "Publish manifest version does not match $($Spec.Name)."
    }
    if (@($manifest.files).Count -eq 0) {
        throw "Publish manifest has no payload files for $($Spec.Name)."
    }
}

$selectedSpecs = if ($Scope -ceq 'Inventory') {
    $packageSpecs
}
else {
    @($packageSpecs | Where-Object Scope -CEQ $Scope)
}
if ($selectedSpecs.Count -eq 0) {
    throw "No release packages are defined for validation scope $Scope."
}

foreach ($spec in $selectedSpecs) {
    Restore-AppImageTransport -Spec $spec
}

if ($Scope -ceq 'Inventory') {
    Get-ChildItem -LiteralPath $artifactRoot -File -Filter '*.internal.transport.tar' |
        Remove-Item
    $remainingInternalFiles = @(Get-ChildItem -LiteralPath $artifactRoot -Recurse -File -Filter '*.internal.*')
    if ($remainingInternalFiles.Count -ne 0) {
        throw 'Internal canonical or candidate transports reached the release publication input.'
    }

    $expectedPackageNames = @($packageSpecs.Name)
    $actualPackageNames = @(
        Get-ChildItem -LiteralPath $artifactRoot -File |
            Where-Object Extension -CIn @('.zip', '.AppImage', '.deb', '.rpm', '.dmg') |
            ForEach-Object Name
    )
    Assert-ExactNameSet -Label 'Release package' -Actual $actualPackageNames -Expected $expectedPackageNames

    $actualSidecars = @(Get-ChildItem -LiteralPath $artifactRoot -File -Filter '*.sha256' | ForEach-Object Name)
    Assert-ExactNameSet -Label 'Release checksum sidecar' -Actual $actualSidecars `
        -Expected @($expectedPackageNames | ForEach-Object { "$_.sha256" })

    $actualManifests = @(
        Get-ChildItem -LiteralPath $artifactRoot -File -Filter 'publish-manifest-*.json' |
            ForEach-Object Name
    )
    Assert-ExactNameSet -Label 'Release publish manifest' -Actual $actualManifests `
        -Expected @($packageSpecs.Manifest)
}

foreach ($spec in $selectedSpecs) {
    Assert-PackageIntegrity -Spec $spec
}

if ($Scope -notin @('Inventory', 'MacOSX64', 'MacOSArm64')) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $packageValidator = Join-Path $PSScriptRoot 'validate-release-package.ps1'
    foreach ($spec in $selectedSpecs) {
        $verifiedManifest = Join-Path $OutputDirectory "verified-$($spec.Manifest)"
        & $packageValidator `
            -PackagePath (Join-Path $artifactRoot $spec.Name) `
            -PackageKind $spec.Kind `
            -RuntimeIdentifier $spec.RuntimeIdentifier `
            -ExpectedManifestPath (Join-Path $artifactRoot $spec.Manifest) `
            -OutputPath $verifiedManifest
    }
}
elseif ($Scope -in @('MacOSX64', 'MacOSArm64')) {
    if (-not $IsMacOS) {
        throw "$Scope package validation must run on macOS."
    }
    $dmgValidator = Join-Path $PSScriptRoot 'macos/validate-dmg-package.sh'
    foreach ($spec in $selectedSpecs) {
        & /bin/bash $dmgValidator `
            (Join-Path $artifactRoot $spec.Name) `
            $ExpectedVersion `
            $spec.RuntimeIdentifier `
            (Join-Path $artifactRoot $spec.Manifest)
        if ($LASTEXITCODE -ne 0) {
            throw "Downloaded macOS package validation failed: $($spec.Name)"
        }
    }
}

Write-Output "Validated $($selectedSpecs.Count) downloaded release package(s) for scope $Scope."
