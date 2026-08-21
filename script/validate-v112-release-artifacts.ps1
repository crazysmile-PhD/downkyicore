param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
$artifacts = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path

$expected = @(
    @{ Package = 'DownKyi-1.1.2-1.win-x64.zip'; Manifest = 'publish-manifest-win-x64.json'; Rid = 'win-x64' },
    @{ Package = 'DownKyi-1.1.2-1.win-x86.zip'; Manifest = 'publish-manifest-win-x86.json'; Rid = 'win-x86' },
    @{ Package = 'DownKyi-1.1.2_linux_self-contained.x86_64.AppImage'; Manifest = 'publish-manifest-linux-x64-AppImage.json'; Rid = 'linux-x64' },
    @{ Package = 'downkyi_1.1.2_linux_self-contained_amd64.deb'; Manifest = 'publish-manifest-linux-x64-deb.json'; Rid = 'linux-x64' },
    @{ Package = 'downkyi_1.1.2_linux_self-contained.x86_64.rpm'; Manifest = 'publish-manifest-linux-x64-rpm.json'; Rid = 'linux-x64' },
    @{ Package = 'DownKyi-1.1.2_linux_self-contained.aarch64.AppImage'; Manifest = 'publish-manifest-linux-arm64-AppImage.json'; Rid = 'linux-arm64' },
    @{ Package = 'downkyi_1.1.2_linux_self-contained_arm64.deb'; Manifest = 'publish-manifest-linux-arm64-deb.json'; Rid = 'linux-arm64' },
    @{ Package = 'DownKyi-1.1.2-osx-x64.dmg'; Manifest = 'publish-manifest-osx-x64.json'; Rid = 'osx-x64' },
    @{ Package = 'DownKyi-1.1.2-osx-arm64.dmg'; Manifest = 'publish-manifest-osx-arm64.json'; Rid = 'osx-arm64' }
)

$expectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($item in $expected) {
    [void]$expectedNames.Add($item.Package)
    [void]$expectedNames.Add("$($item.Package).sha256")
    [void]$expectedNames.Add($item.Manifest)

    $packagePath = Join-Path $artifacts $item.Package
    $hashPath = "$packagePath.sha256"
    $manifestPath = Join-Path $artifacts $item.Manifest
    foreach ($path in @($packagePath, $hashPath, $manifestPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Required release artifact is missing or empty: $path"
        }
    }

    $hashLine = (Get-Content -LiteralPath $hashPath -Raw).Trim()
    if ($hashLine -notmatch '^([a-f0-9]{64})  (.+)$') {
        throw "Malformed SHA256 sidecar: $hashPath"
    }
    if ($Matches[2] -cne $item.Package) {
        throw "SHA256 sidecar names $($Matches[2]); expected $($item.Package)."
    }
    $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $Matches[1]) {
        throw "SHA256 mismatch for $($item.Package)."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.runtimeIdentifier -cne $item.Rid -or
        $manifest.applicationVersion -cne '1.1.2' -or
        @($manifest.files).Count -eq 0) {
        throw "Publish manifest contract failed: $manifestPath"
    }
}

$actualNames = @(
    Get-ChildItem -LiteralPath $artifacts -File |
        Select-Object -ExpandProperty Name
)
$unexpected = @($actualNames | Where-Object { -not $expectedNames.Contains($_) })
$missing = @($expectedNames | Where-Object { $_ -notin $actualNames })
if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
    throw "Release artifact set mismatch. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')."
}

Write-Output "Validated $($expected.Count) v1.1.2 packages, SHA256 sidecars, and publish manifests."
