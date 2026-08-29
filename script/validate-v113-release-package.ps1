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
