[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('zip', 'AppImage', 'deb', 'rpm')]
    [string]$PackageKind,

    [Parameter(Mandatory = $true)]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$expectedVersion = '1.1.3'
$repositoryVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../version.txt') -Raw).Trim()
if ($repositoryVersion -cne $expectedVersion) {
    throw "v1.1.3 package validation requires version.txt to remain exactly $expectedVersion."
}
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$expectedManifestPath = (Resolve-Path -LiteralPath $ExpectedManifestPath).Path
$approvedManifestPath = [IO.Path]::GetFullPath($OutputPath)
if ([String]::Equals($expectedManifestPath, $approvedManifestPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Expected and approved publish manifest paths must be distinct.'
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "downkyi-release-package-$([Guid]::NewGuid().ToString('N'))"
$extractDirectory = Join-Path $temporaryRoot 'extracted'
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

function ConvertTo-ComparableManifestJson {
    param([string]$Path)

    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $files = @(
        $manifest.files |
            Sort-Object -Property path |
            ForEach-Object {
                [ordered]@{
                    path = [string]$_.path
                    bytes = [long]$_.bytes
                    sha256 = ([string]$_.sha256).ToLowerInvariant()
                }
            }
    )
    return ([ordered]@{
        schemaVersion = [int]$manifest.schemaVersion
        runtimeIdentifier = [string]$manifest.runtimeIdentifier
        applicationVersion = [string]$manifest.applicationVersion
        files = $files
    } | ConvertTo-Json -Depth 5 -Compress)
}

function Assert-LinuxBinaryArchitecture {
    param(
        [string]$Path,
        [string]$ExpectedRuntimeIdentifier
    )

    $description = (& file --brief --dereference -- $Path 2>&1) -join ' '
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect packaged binary architecture for $Path."
    }

    $isElf = $description.Contains('ELF', [StringComparison]::OrdinalIgnoreCase)
    $matches = if ($ExpectedRuntimeIdentifier -ceq 'linux-x64') {
        $isElf -and $description.Contains('x86-64', [StringComparison]::OrdinalIgnoreCase)
    }
    elseif ($ExpectedRuntimeIdentifier -ceq 'linux-arm64') {
        $isElf -and $description.Contains('aarch64', [StringComparison]::OrdinalIgnoreCase)
    }
    else {
        $false
    }

    if (-not $matches) {
        throw "Packaged binary $Path does not match $ExpectedRuntimeIdentifier. file reported: $description"
    }
}

function Test-ElfFile {
    param([string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 4) {
            return $false
        }

        $magic = [byte[]]::new(4)
        return $stream.Read($magic, 0, $magic.Length) -eq $magic.Length -and
            $magic[0] -eq 0x7f -and
            $magic[1] -eq 0x45 -and
            $magic[2] -eq 0x4c -and
            $magic[3] -eq 0x46
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-Type2AppImage {
    param([string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 11) {
            throw "AppImage is too short to contain a type 2 runtime: $Path"
        }
        $stream.Position = 8
        $magic = [byte[]]::new(3)
        if ($stream.Read($magic, 0, $magic.Length) -ne $magic.Length -or
            $magic[0] -ne 0x41 -or
            $magic[1] -ne 0x49 -or
            $magic[2] -ne 0x02) {
            throw "AppImage does not contain the type 2 runtime marker at offset 8: $Path"
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-LaunchStaysRunning {
    param(
        [string]$Label,
        [string]$Path,
        [string[]]$Arguments,
        [hashtable]$Environment
    )

    $savedEnvironment = @{}
    try {
        foreach ($entry in $Environment.GetEnumerator()) {
            $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
        }

        & timeout '--signal=TERM' '--kill-after=2s' '5s' xvfb-run -a $Path @Arguments *> $null
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 124) {
            throw "$Label launch smoke exited before the five-second validation window (exit code $exitCode)."
        }
    }
    finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}

function Assert-WindowsBinaryArchitecture {
    param(
        [string]$Path,
        [string]$ExpectedRuntimeIdentifier
    )

    $expectedMachine = if ($ExpectedRuntimeIdentifier -ceq 'win-x86') {
        0x014c
    }
    elseif ($ExpectedRuntimeIdentifier -ceq 'win-x64') {
        0x8664
    }
    else {
        throw "Unsupported v1.1.3 Windows runtime identifier: $ExpectedRuntimeIdentifier"
    }

    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) {
            throw "Packaged Windows binary is not a valid PE image: $Path"
        }

        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or ($peOffset + 6) -gt $stream.Length) {
            throw "Packaged Windows binary has an invalid PE header offset: $Path"
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Packaged Windows binary has an invalid PE signature: $Path"
        }

        $actualMachine = $reader.ReadUInt16()
        if ($actualMachine -ne $expectedMachine) {
            $actualMachineText = '0x{0:X4}' -f $actualMachine
            throw "Packaged Windows binary $Path has PE machine $actualMachineText and does not match $ExpectedRuntimeIdentifier."
        }
    }
    finally {
        $stream.Dispose()
    }
}

try {
    if ($PackageKind -cne 'AppImage') {
        New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
    }

    switch ($PackageKind) {
        'zip' {
            Expand-Archive -LiteralPath $package -DestinationPath $extractDirectory
        }
        'AppImage' {
            Assert-LinuxBinaryArchitecture -Path $package -ExpectedRuntimeIdentifier $RuntimeIdentifier
            Assert-Type2AppImage -Path $package
            $packageMode = [IO.File]::GetUnixFileMode($package)
            if (($packageMode -band [IO.UnixFileMode]::OtherExecute) -eq 0) {
                throw "AppImage is not executable by a non-owner: $package"
            }
            Push-Location $temporaryRoot
            try {
                & $package '--appimage-extract' *> $null
                if ($LASTEXITCODE -ne 0) { throw 'AppImage-native extraction failed.' }
            }
            finally {
                Pop-Location
            }
            $extractDirectory = Join-Path $temporaryRoot 'squashfs-root'
            if (-not (Test-Path -LiteralPath $extractDirectory -PathType Container)) {
                throw 'AppImage-native extraction did not produce squashfs-root.'
            }
        }
        'deb' {
            & dpkg-deb --extract $package $extractDirectory
            if ($LASTEXITCODE -ne 0) { throw 'Debian package extraction failed.' }
        }
        'rpm' {
            Push-Location $extractDirectory
            try {
                & bash -c 'rpm2cpio "$1" | cpio -idm --quiet' _ $package
                if ($LASTEXITCODE -ne 0) { throw 'RPM package extraction failed.' }
            }
            finally {
                Pop-Location
            }
        }
    }

    if ($PackageKind -ceq 'AppImage') {
        $appRun = Join-Path $extractDirectory 'AppRun'
        $appRunItem = Get-Item -LiteralPath $appRun -Force -ErrorAction SilentlyContinue
        if ($null -eq $appRunItem -or $appRunItem.PSIsContainer) {
            throw 'AppImage entrypoint AppRun is missing or empty.'
        }

        if ($appRunItem.LinkType -ceq 'SymbolicLink') {
            $linkTargets = @($appRunItem.Target)
            if ($linkTargets.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$linkTargets[0])) {
                throw 'AppImage entrypoint AppRun symlink has no unambiguous target.'
            }
            $linkTarget = ([string]$linkTargets[0]).Replace('\', '/')
            if ($linkTarget -cne 'usr/bin/DownKyi') {
                throw "AppImage entrypoint AppRun symlink target is incorrect: $linkTarget"
            }
            $resolvedTarget = [IO.Path]::GetFullPath((Join-Path $extractDirectory $linkTarget))
            $trimSeparators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $extractRoot = [IO.Path]::GetFullPath($extractDirectory).TrimEnd($trimSeparators) +
                [IO.Path]::DirectorySeparatorChar
            if (-not $resolvedTarget.StartsWith($extractRoot, [StringComparison]::Ordinal)) {
                throw 'AppImage entrypoint AppRun symlink escapes the extracted AppImage root.'
            }
            if (-not (Test-Path -LiteralPath $resolvedTarget -PathType Leaf)) {
                throw 'AppImage entrypoint AppRun symlink target does not exist.'
            }
            $targetMode = [IO.File]::GetUnixFileMode($resolvedTarget)
            if (($targetMode -band [IO.UnixFileMode]::OtherExecute) -eq 0) {
                throw 'AppImage entrypoint AppRun symlink target is not executable by a non-owner.'
            }
        }
        else {
            if ($appRunItem.Length -eq 0) {
                throw 'AppImage entrypoint AppRun is missing or empty.'
            }
            $appRunMode = [IO.File]::GetUnixFileMode($appRun)
            if (($appRunMode -band [IO.UnixFileMode]::OtherExecute) -eq 0) {
                throw 'AppImage entrypoint AppRun is not executable by a non-owner.'
            }
        }

        $launchHome = Join-Path $temporaryRoot 'launch-home'
        New-Item -ItemType Directory -Path $launchHome -Force | Out-Null
        $launchEnvironment = @{
            APPDIR = $extractDirectory
            APPIMAGE = $package
            HOME = $launchHome
            OWD = $temporaryRoot
        }
        Assert-LaunchStaysRunning `
            -Label 'AppRun' `
            -Path $appRun `
            -Arguments @() `
            -Environment $launchEnvironment
        Assert-LaunchStaysRunning `
            -Label 'AppImage runtime' `
            -Path $package `
            -Arguments @('--appimage-extract-and-run') `
            -Environment $launchEnvironment
    }

    $runtimeCandidates = @(
        Get-ChildItem -LiteralPath $extractDirectory -Recurse -File -Filter 'DownKyi.dll' |
            ForEach-Object { $_.Directory.FullName } |
            Where-Object {
                (Test-Path -LiteralPath (Join-Path $_ 'DownKyi') -PathType Leaf) -or
                (Test-Path -LiteralPath (Join-Path $_ 'DownKyi.exe') -PathType Leaf)
            } |
            Select-Object -Unique
    )
    if ($runtimeCandidates.Count -ne 1) {
        throw "Expected exactly one packaged DownKyi runtime, found $($runtimeCandidates.Count)."
    }

    if ($PackageKind -ceq 'zip') {
        $runtime = $runtimeCandidates[0]
        $windowsExecutables = @(
            (Join-Path $runtime 'DownKyi.exe'),
            (Join-Path $runtime 'aria2/aria2c.exe'),
            (Join-Path $runtime 'ffmpeg/ffmpeg.exe'),
            (Join-Path $runtime 'ffmpeg/ffprobe.exe')
        )
        foreach ($executable in $windowsExecutables) {
            Assert-WindowsBinaryArchitecture -Path $executable -ExpectedRuntimeIdentifier $RuntimeIdentifier
        }
    }
    else {
        if ($RuntimeIdentifier -notin @('linux-x64', 'linux-arm64')) {
            throw "Unsupported v1.1.3 Linux runtime identifier: $RuntimeIdentifier"
        }

        $expectedPackageArchitecture = if ($RuntimeIdentifier -ceq 'linux-x64') {
            @{ deb = 'amd64'; rpm = 'x86_64' }
        }
        else {
            @{ deb = 'arm64'; rpm = 'aarch64' }
        }

        if ($PackageKind -ceq 'deb') {
            $actualArchitecture = ((& dpkg-deb --field $package Architecture 2>&1) -join '').Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect Debian package architecture.' }
            if ($actualArchitecture -cne $expectedPackageArchitecture.deb) {
                throw "Debian package architecture $actualArchitecture does not match $RuntimeIdentifier."
            }
            $actualVersion = ((& dpkg-deb --field $package Version 2>&1) -join '').Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect Debian package version.' }
            $expectedDebianVersion = "$expectedVersion-1"
            if ($actualVersion -cne $expectedDebianVersion) {
                throw "Debian package version $actualVersion does not match $expectedDebianVersion."
            }
            $actualName = ((& dpkg-deb --field $package Package 2>&1) -join '').Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect Debian package identity.' }
            if ($actualName -cne 'downkyi') {
                throw "Debian package identity $actualName does not match downkyi."
            }
        }
        elseif ($PackageKind -ceq 'rpm') {
            $actualArchitecture = ((& rpm -qp --queryformat '%{ARCH}' $package 2>&1) -join '').Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect RPM package architecture.' }
            if ($actualArchitecture -cne $expectedPackageArchitecture.rpm) {
                throw "RPM package architecture $actualArchitecture does not match $RuntimeIdentifier."
            }
            $actualEvr = ((& rpm -qp --queryformat '%{EPOCHNUM}:%{VERSION}-%{RELEASE}' $package 2>&1) -join '').Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect RPM package EVR.' }
            $expectedEvr = "0:$expectedVersion-1"
            if ($actualEvr -cne $expectedEvr) {
                throw "RPM package EVR $actualEvr does not match $expectedEvr."
            }
            $actualName = ((& rpm -qp --queryformat '%{NAME}' $package 2>&1) -join '').Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect RPM package identity.' }
            if ($actualName -cne 'downkyi') {
                throw "RPM package identity $actualName does not match downkyi."
            }
        }

        $runtime = $runtimeCandidates[0]
        $linuxExecutables = @(
            (Join-Path $runtime 'DownKyi'),
            (Join-Path $runtime 'aria2/aria2c'),
            (Join-Path $runtime 'ffmpeg/ffmpeg'),
            (Join-Path $runtime 'ffmpeg/ffprobe')
        )
        foreach ($executable in $linuxExecutables) {
            $mode = [IO.File]::GetUnixFileMode($executable)
            if (($mode -band [IO.UnixFileMode]::OtherExecute) -eq 0) {
                throw "Packaged Linux executable is not executable by a non-owner: $executable"
            }
        }

        $elfFiles = @(
            Get-ChildItem -LiteralPath $runtime -Recurse -File |
                Where-Object { Test-ElfFile -Path $_.FullName }
        )
        if ($elfFiles.Count -eq 0) {
            throw 'Packaged Linux runtime does not contain any ELF files.'
        }
        foreach ($elfFile in $elfFiles) {
            Assert-LinuxBinaryArchitecture -Path $elfFile.FullName -ExpectedRuntimeIdentifier $RuntimeIdentifier
        }
    }

    $packageManifestPath = Join-Path $temporaryRoot 'package-manifest.json'
    & "$PSScriptRoot/validate-publish-output.ps1" `
        -PublishDirectory $runtimeCandidates[0] `
        -RuntimeIdentifier $RuntimeIdentifier `
        -ExpectedVersion $expectedVersion `
        -OutputPath $packageManifestPath

    $expectedManifest = ConvertTo-ComparableManifestJson -Path $expectedManifestPath
    $packageManifest = ConvertTo-ComparableManifestJson -Path $packageManifestPath
    if ($expectedManifest -cne $packageManifest) {
        throw 'Final package payload does not match the validated publish manifest.'
    }

    $outputDirectory = Split-Path -Parent $approvedManifestPath
    if ($outputDirectory) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    Copy-Item -LiteralPath $expectedManifestPath -Destination $approvedManifestPath

    Write-Output "Validated extracted $PackageKind package: $package"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
