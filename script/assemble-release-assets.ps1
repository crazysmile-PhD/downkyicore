param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

function Resolve-UniqueArtifact {
    param(
        [System.IO.FileInfo[]]$Files,
        [string]$Name
    )

    $matches = @($Files | Where-Object { $_.Name -ceq $Name })
    if ($matches.Count -ne 1) {
        throw "Expected exactly one artifact named '$Name', found $($matches.Count)."
    }

    return $matches[0]
}

function Read-PackageChecksum {
    param(
        [System.IO.FileInfo]$Sidecar,
        [string]$PackageName
    )

    $content = (Get-Content -LiteralPath $Sidecar.FullName -Raw).Trim()
    $pattern = "^(?<hash>[a-fA-F0-9]{64})[ `t]+$([Regex]::Escape($PackageName))$"
    if ($content -notmatch $pattern) {
        throw "Checksum sidecar '$($Sidecar.FullName)' is malformed or names the wrong package."
    }

    return $Matches.hash.ToLowerInvariant()
}

function ConvertTo-ComparablePayload {
    param([object]$Manifest)

    return @(
        $Manifest.files |
            ForEach-Object {
                [ordered]@{
                    path = [string]$_.path
                    bytes = [long]$_.bytes
                    sha256 = ([string]$_.sha256).ToLowerInvariant()
                }
            } |
            Sort-Object -Property path
    )
}

$artifactsRoot = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "Release asset output already exists: $outputPath"
}

$outputParent = Split-Path -Parent $outputPath
if (-not $outputParent) {
    throw "Release asset output must have a parent directory: $outputPath"
}
New-Item -ItemType Directory -Path $outputParent -Force | Out-Null

$definitions = @(
    [ordered]@{ Name = "DownKyi-$ExpectedVersion-1.win-x64.zip"; Manifest = 'publish-manifest-win-x64.json'; Runtime = 'win-x64'; Kind = 'zip' },
    [ordered]@{ Name = "DownKyi-$ExpectedVersion-1.win-x86.zip"; Manifest = 'publish-manifest-win-x86.json'; Runtime = 'win-x86'; Kind = 'zip' },
    [ordered]@{ Name = "DownKyi-${ExpectedVersion}_linux_self-contained.x86_64.AppImage"; Manifest = 'publish-manifest-linux-x64-AppImage.json'; Runtime = 'linux-x64'; Kind = 'AppImage' },
    [ordered]@{ Name = "downkyi_${ExpectedVersion}_linux_self-contained_amd64.deb"; Manifest = 'publish-manifest-linux-x64-deb.json'; Runtime = 'linux-x64'; Kind = 'deb' },
    [ordered]@{ Name = "downkyi_${ExpectedVersion}_linux_self-contained.x86_64.rpm"; Manifest = 'publish-manifest-linux-x64-rpm.json'; Runtime = 'linux-x64'; Kind = 'rpm' },
    [ordered]@{ Name = "DownKyi-${ExpectedVersion}_linux_self-contained.aarch64.AppImage"; Manifest = 'publish-manifest-linux-arm64-AppImage.json'; Runtime = 'linux-arm64'; Kind = 'AppImage' },
    [ordered]@{ Name = "downkyi_${ExpectedVersion}_linux_self-contained_arm64.deb"; Manifest = 'publish-manifest-linux-arm64-deb.json'; Runtime = 'linux-arm64'; Kind = 'deb' },
    [ordered]@{ Name = "DownKyi-$ExpectedVersion-osx-x64.dmg"; Manifest = 'publish-manifest-osx-x64.json'; Runtime = 'osx-x64'; Kind = 'dmg' },
    [ordered]@{ Name = "DownKyi-$ExpectedVersion-osx-arm64.dmg"; Manifest = 'publish-manifest-osx-arm64.json'; Runtime = 'osx-arm64'; Kind = 'dmg' }
)

$allFiles = @(Get-ChildItem -LiteralPath $artifactsRoot -Recurse -Force -File)
$expectedPackageNames = @($definitions | ForEach-Object { $_.Name })
$expectedSidecarNames = @($expectedPackageNames | ForEach-Object { "$_.sha256" })
$expectedManifestNames = @($definitions | ForEach-Object { $_.Manifest })
$packageExtensions = @('.zip', '.AppImage', '.deb', '.rpm', '.dmg')
$unexpectedPackages = @(
    $allFiles |
        Where-Object { $packageExtensions -ccontains $_.Extension -and $expectedPackageNames -cnotcontains $_.Name }
)
if ($unexpectedPackages.Count -ne 0) {
    throw "Unexpected release packages found: $($unexpectedPackages.FullName -join ', ')"
}
$unexpectedSidecars = @(
    $allFiles |
        Where-Object { $_.Name -like '*.sha256' -and $expectedSidecarNames -cnotcontains $_.Name }
)
if ($unexpectedSidecars.Count -ne 0) {
    throw "Unexpected package checksum sidecars found: $($unexpectedSidecars.FullName -join ', ')"
}
$unexpectedManifests = @(
    $allFiles |
        Where-Object {
            $_.Name -like 'publish-manifest-*.json' -and
            $expectedManifestNames -cnotcontains $_.Name
        }
)
if ($unexpectedManifests.Count -ne 0) {
    throw "Unexpected publish manifests found: $($unexpectedManifests.FullName -join ', ')"
}

$packageRecords = @()
$payloadsByRuntime = [ordered]@{}
foreach ($definition in $definitions) {
    $package = Resolve-UniqueArtifact -Files $allFiles -Name $definition.Name
    if ($package.Length -le 0) {
        throw "Release package is empty: $($package.FullName)"
    }

    $sidecar = Resolve-UniqueArtifact -Files $allFiles -Name "$($definition.Name).sha256"
    $expectedHash = Read-PackageChecksum -Sidecar $sidecar -PackageName $definition.Name
    $actualHash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedHash) {
        throw "Release package '$($definition.Name)' does not match its checksum sidecar."
    }

    $manifestFile = Resolve-UniqueArtifact -Files $allFiles -Name $definition.Manifest
    $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or
        [string]$manifest.applicationVersion -cne $ExpectedVersion -or
        [string]$manifest.runtimeIdentifier -cne $definition.Runtime) {
        throw "Publish manifest '$($definition.Manifest)' does not match version $ExpectedVersion and runtime $($definition.Runtime)."
    }

    $payloadFiles = @(ConvertTo-ComparablePayload -Manifest $manifest)
    if ($payloadFiles.Count -eq 0) {
        throw "Publish manifest '$($definition.Manifest)' contains no files."
    }
    foreach ($file in $payloadFiles) {
        if ([string]::IsNullOrWhiteSpace([string]$file.path) -or
            [long]$file.bytes -le 0 -or
            [string]$file.sha256 -notmatch '^[a-f0-9]{64}$') {
            throw "Publish manifest '$($definition.Manifest)' contains an invalid file record."
        }
    }

    $payloadJson = ConvertTo-Json -InputObject $payloadFiles -Depth 5 -Compress
    if ($payloadsByRuntime.Contains($definition.Runtime)) {
        if ($payloadsByRuntime[$definition.Runtime].ComparableJson -cne $payloadJson) {
            throw "Publish manifests for runtime '$($definition.Runtime)' do not describe the same payload."
        }
    }
    else {
        $payloadsByRuntime[$definition.Runtime] = [ordered]@{
            GeneratedAtUtc = [string]$manifest.generatedAtUtc
            Files = $payloadFiles
            ComparableJson = $payloadJson
        }
    }

    $packageRecords += [ordered]@{
        name = $definition.Name
        runtimeIdentifier = $definition.Runtime
        packageKind = $definition.Kind
        bytes = $package.Length
        sha256 = $actualHash
        SourcePath = $package.FullName
    }
}

$stagingPath = Join-Path $outputParent ".release-assets-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingPath | Out-Null
try {
    foreach ($package in $packageRecords) {
        $destination = Join-Path $stagingPath $package.name
        Copy-Item -LiteralPath $package.SourcePath -Destination $destination
        $copiedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copiedHash -cne $package.sha256) {
            throw "Copied release package '$($package.name)' no longer matches its validated SHA-256."
        }
    }

    $sortedPackageNames = [string[]]@($packageRecords | ForEach-Object { $_.name })
    [Array]::Sort($sortedPackageNames, [StringComparer]::Ordinal)
    $sortedPackageRecords = @(
        foreach ($packageName in $sortedPackageNames) {
            $packageRecords | Where-Object { $_.name -ceq $packageName }
        }
    )
    $checksumLines = @($sortedPackageRecords | ForEach-Object { "$($_.sha256)  $($_.name)" })
    $checksumLines | Set-Content -LiteralPath (Join-Path $stagingPath 'SHA256SUMS.txt') -Encoding ascii

    $publicPackageRecords = @(
        $sortedPackageRecords |
            ForEach-Object {
                [ordered]@{
                    name = $_.name
                    runtimeIdentifier = $_.runtimeIdentifier
                    packageKind = $_.packageKind
                    bytes = $_.bytes
                    sha256 = $_.sha256
                }
            }
    )
    $sortedRuntimeIdentifiers = [string[]]@($payloadsByRuntime.Keys)
    [Array]::Sort($sortedRuntimeIdentifiers, [StringComparer]::Ordinal)
    $runtimePayloads = @(
        foreach ($runtimeIdentifier in $sortedRuntimeIdentifiers) {
            $payload = $payloadsByRuntime[$runtimeIdentifier]
                [ordered]@{
                    runtimeIdentifier = $runtimeIdentifier
                    generatedAtUtc = $payload.GeneratedAtUtc
                    files = @($payload.Files)
                }
        }
    )
    $releaseManifest = [ordered]@{
        schemaVersion = 1
        applicationVersion = $ExpectedVersion
        packages = $publicPackageRecords
        runtimePayloads = $runtimePayloads
    }
    $releaseManifest |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $stagingPath 'release-manifest.json') -Encoding utf8NoBOM

    $publicFiles = @(Get-ChildItem -LiteralPath $stagingPath -File)
    if ($publicFiles.Count -ne 11) {
        throw "Expected exactly 11 public release assets, found $($publicFiles.Count)."
    }

    Move-Item -LiteralPath $stagingPath -Destination $outputPath
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}

Write-Output "Assembled 9 packages, SHA256SUMS.txt, and release-manifest.json at $outputPath"
