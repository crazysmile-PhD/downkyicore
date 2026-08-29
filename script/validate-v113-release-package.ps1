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
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$expectedVersion = '1.1.3'
$repositoryVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot '../version.txt') -Raw).Trim()
if ($repositoryVersion -cne $expectedVersion) {
    throw "v1.1.3 package validation requires version.txt to remain exactly $expectedVersion."
}
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "downkyi-release-package-$([Guid]::NewGuid().ToString('N'))"
$extractDirectory = Join-Path $temporaryRoot 'extracted'
New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null

function Assert-LinuxBinaryArchitecture {
    param(
        [string]$Path,
        [string]$ExpectedRuntimeIdentifier
    )

    $description = (& file --brief --dereference -- $Path 2>&1) -join ' '
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect packaged binary architecture for $Path."
    }

    $matches = if ($ExpectedRuntimeIdentifier -ceq 'linux-x64') {
        $description.Contains('x86-64', [StringComparison]::OrdinalIgnoreCase)
    }
    elseif ($ExpectedRuntimeIdentifier -ceq 'linux-arm64') {
        $description.Contains('aarch64', [StringComparison]::OrdinalIgnoreCase)
    }
    else {
        $false
    }

    if (-not $matches) {
        throw "Packaged binary $Path does not match $ExpectedRuntimeIdentifier. file reported: $description"
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
    switch ($PackageKind) {
        'zip' {
            Expand-Archive -LiteralPath $package -DestinationPath $extractDirectory
        }
        'AppImage' {
            & 7z x -y "-o$extractDirectory" $package | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'AppImage extraction failed.' }
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
            if ($actualVersion -cne $expectedVersion) {
                throw "Debian package version $actualVersion does not match $expectedVersion."
            }
        }
        elseif ($PackageKind -ceq 'rpm') {
            $actualArchitecture = ((& rpm -qp --queryformat '%{ARCH}' $package 2>&1) -join '').Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect RPM package architecture.' }
            if ($actualArchitecture -cne $expectedPackageArchitecture.rpm) {
                throw "RPM package architecture $actualArchitecture does not match $RuntimeIdentifier."
            }
            $actualVersion = ((& rpm -qp --queryformat '%{VERSION}' $package 2>&1) -join '').Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect RPM package version.' }
            if ($actualVersion -cne $expectedVersion) {
                throw "RPM package version $actualVersion does not match $expectedVersion."
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
            Assert-LinuxBinaryArchitecture -Path $executable -ExpectedRuntimeIdentifier $RuntimeIdentifier
            $mode = [IO.File]::GetUnixFileMode($executable)
            if (($mode -band [IO.UnixFileMode]::OtherExecute) -eq 0) {
                throw "Packaged Linux executable is not executable by a non-owner: $executable"
            }
        }
    }

    & "$PSScriptRoot/validate-publish-output.ps1" `
        -PublishDirectory $runtimeCandidates[0] `
        -RuntimeIdentifier $RuntimeIdentifier `
        -ExpectedVersion $expectedVersion `
        -OutputPath $OutputPath

    Write-Output "Validated extracted $PackageKind package: $package"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
