param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'release-package-catalog.ps1')

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

function New-VerificationArchive {
    param(
        [System.IO.FileInfo[]]$Files,
        [string]$DestinationPath
    )

    $archive = [IO.Compression.ZipFile]::Open(
        $DestinationPath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $Files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $file.Name,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
}

$artifactsRoot = (Resolve-Path -LiteralPath $ArtifactsDirectory).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) {
    throw "Release asset output already exists: $outputPath"
}

$packageSpecs = @(Get-ReleasePackageCatalog -ExpectedVersion $ExpectedVersion)

$allFiles = @(Get-ChildItem -LiteralPath $artifactsRoot -Recurse -Force -File)
$outputParent = Split-Path -Parent $outputPath
$stagingPath = Join-Path $outputParent ".release-assets-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
try {
    $verificationFiles = @(
        foreach ($spec in $packageSpecs) {
            $package = Get-ArtifactFile -Files $allFiles -Name $spec.Name
            $sidecar = Get-ArtifactFile -Files $allFiles -Name "$($spec.Name).sha256"
            $manifest = Get-ArtifactFile -Files $allFiles -Name $spec.Manifest
            Copy-CheckedFile -File $package -Checksum $sidecar -Destination $stagingPath
            $sidecar
            $manifest
        }
    )

    $archiveName = "DownKyi-$ExpectedVersion-verification.zip"
    New-VerificationArchive -Files $verificationFiles -DestinationPath (Join-Path $stagingPath $archiveName)
    Move-Item -LiteralPath $stagingPath -Destination $outputPath
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}

Write-Output "Assembled $($packageSpecs.Count) packages and $archiveName at $outputPath"
