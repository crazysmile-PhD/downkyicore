param(
    [Parameter(Mandatory)]
    [ValidateSet('x86', 'x64')]
    [string]$arch
)

$ErrorActionPreference = 'Stop'
& python (Join-Path $PSScriptRoot 'ffmpeg-source/build.py') "win-$arch"
if ($LASTEXITCODE -ne 0) {
    throw "FFmpeg source build failed for win-$arch."
}
