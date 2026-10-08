param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"

function Get-ArtifactFile {
    param(
        [System.IO.FileInfo[]]$Files,
        [string]$Name
    )

    $matchingFiles = @($Files | Where-Object { $_.Name -ceq $Name })
    if ($matchingFiles.Count -ne 1 -or $matchingFiles[0].Length -eq 0) {
        throw "Expected exactly one nonempty artifact named '$Name'."
    }

    return $matchingFiles[0]
}

function Copy-CheckedFile {
    param(
        [System.IO.FileInfo]$File,
        [System.IO.FileInfo]$Checksum,
        [string]$Destination
    )

    $content = (Get-Content -LiteralPath $Checksum.FullName -Raw).Trim()
    $pattern = "^(?<hash>[a-fA-F0-9]{64})[ \t]+$([Regex]::Escape($File.Name))$"
    if ($content -cnotmatch $pattern) {
        throw "Checksum sidecar '$($Checksum.Name)' is malformed or names the wrong package."
    }
    $expectedHash = $Matches.hash

    $destinationPath = Join-Path $Destination $File.Name
    Copy-Item -LiteralPath $File.FullName -Destination $destinationPath
    $actualHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
    if (-not [String]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release package '$($File.Name)' does not match its checksum sidecar."
    }
}

$artifactsRoot = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "Release asset output already exists: $outputPath"
}

$packageManifests = [ordered]@{
    "DownKyi-$ExpectedVersion-1.win-x64.zip" = 'publish-manifest-win-x64.json'
    "DownKyi-$ExpectedVersion-1.win-x86.zip" = 'publish-manifest-win-x86.json'
    "DownKyi-${ExpectedVersion}_linux_self-contained.x86_64.AppImage" = 'publish-manifest-linux-x64-AppImage.json'
    "downkyi_${ExpectedVersion}_linux_self-contained_amd64.deb" = 'publish-manifest-linux-x64-deb.json'
    "downkyi_${ExpectedVersion}_linux_self-contained.x86_64.rpm" = 'publish-manifest-linux-x64-rpm.json'
    "DownKyi-${ExpectedVersion}_linux_self-contained.aarch64.AppImage" = 'publish-manifest-linux-arm64-AppImage.json'
    "downkyi_${ExpectedVersion}_linux_self-contained_arm64.deb" = 'publish-manifest-linux-arm64-deb.json'
    "DownKyi-$ExpectedVersion-osx-x64.dmg" = 'publish-manifest-osx-x64.json'
    "DownKyi-$ExpectedVersion-osx-arm64.dmg" = 'publish-manifest-osx-arm64.json'
}

$allFiles = @(Get-ChildItem -LiteralPath $artifactsRoot -Recurse -Force -File)
$outputParent = Split-Path -Parent $outputPath
$stagingPath = Join-Path $outputParent ".release-assets-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
try {
    $verificationFiles = @(
        foreach ($entry in $packageManifests.GetEnumerator()) {
            $package = Get-ArtifactFile -Files $allFiles -Name $entry.Key
            $sidecar = Get-ArtifactFile -Files $allFiles -Name "$($entry.Key).sha256"
            $manifest = Get-ArtifactFile -Files $allFiles -Name $entry.Value
            Copy-CheckedFile -File $package -Checksum $sidecar -Destination $stagingPath
            $sidecar
            $manifest
        }
    )

    $archiveName = "DownKyi-$ExpectedVersion-verification.zip"
    Compress-Archive -LiteralPath $verificationFiles.FullName -DestinationPath (Join-Path $stagingPath $archiveName)
    Move-Item -LiteralPath $stagingPath -Destination $outputPath
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}

Write-Output "Assembled $($packageManifests.Count) packages and $archiveName at $outputPath"
