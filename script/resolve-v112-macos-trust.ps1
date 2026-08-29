[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$GitHubOutputPath
)

$ErrorActionPreference = 'Stop'

& "$PSScriptRoot/resolve-macos-release-trust.ps1" @PSBoundParameters
