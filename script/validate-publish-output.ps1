param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

function Resolve-RequiredFile {
    param(
        [string]$Label,
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        $path = Join-Path $PublishDirectory $candidate
        if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-Item -LiteralPath $path).Length -gt 0) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    throw "$Label is missing or empty in publish output: $PublishDirectory"
}

$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$downKyiAssembly = Resolve-RequiredFile "DownKyi assembly" @("DownKyi.dll")
$downKyiExecutable = Resolve-RequiredFile "DownKyi executable" @("DownKyi.exe", "DownKyi")
$ariaExecutable = Resolve-RequiredFile "aria2 executable" @("aria2/aria2c.exe", "aria2/aria2c")
$ariaChecksum = Resolve-RequiredFile "aria2 binary checksum" @("aria2/aria2c.exe.sha256", "aria2/aria2c.sha256")
$ffmpegExecutable = Resolve-RequiredFile "FFmpeg executable" @("ffmpeg/ffmpeg.exe", "ffmpeg/ffmpeg")
$ffprobeExecutable = Resolve-RequiredFile "ffprobe executable" @("ffmpeg/ffprobe.exe", "ffmpeg/ffprobe")
$depsFile = Resolve-RequiredFile "DownKyi dependency manifest" @("DownKyi.deps.json")

$actualVersion = [Reflection.AssemblyName]::GetAssemblyName($downKyiAssembly).Version
$expected = [Version]$ExpectedVersion
if ($actualVersion.Major -ne $expected.Major -or
    $actualVersion.Minor -ne $expected.Minor -or
    $actualVersion.Build -ne $expected.Build) {
    throw "Published assembly version $actualVersion does not match expected version $ExpectedVersion."
}

$deps = Get-Content -LiteralPath $depsFile -Raw
if (-not $deps.Contains("Avalonia.Themes.Fluent", [StringComparison]::Ordinal)) {
    throw "Published dependency manifest does not contain Avalonia.Themes.Fluent."
}
if ($deps.Contains("Avalonia.Themes.Simple", [StringComparison]::Ordinal)) {
    throw "Published dependency manifest still contains Avalonia.Themes.Simple."
}

$expectedAriaHash = (Get-Content -LiteralPath $ariaChecksum -Raw).Trim()
if ($expectedAriaHash -notmatch '^[a-fA-F0-9]{64}$') {
    throw "Published aria2 checksum sidecar is malformed."
}
$actualAriaHash = (Get-FileHash -LiteralPath $ariaExecutable -Algorithm SHA256).Hash
if (-not [String]::Equals($actualAriaHash, $expectedAriaHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Published aria2 executable does not match its checksum sidecar."
}

$requiredFiles = @(
    $downKyiAssembly,
    $downKyiExecutable,
    $ariaExecutable,
    $ariaChecksum,
    $ffmpegExecutable,
    $ffprobeExecutable,
    $depsFile
)
$manifestFiles = @(
    foreach ($path in $requiredFiles) {
        $item = Get-Item -LiteralPath $path
        [ordered]@{
            path = [IO.Path]::GetRelativePath($PublishDirectory, $item.FullName).Replace('\', '/')
            bytes = $item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)

$manifest = [ordered]@{
    schemaVersion = 1
    runtimeIdentifier = $RuntimeIdentifier
    applicationVersion = $ExpectedVersion
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    files = $manifestFiles
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
Write-Output "Validated publish output for $RuntimeIdentifier and wrote $OutputPath"
