[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('ad-hoc', 'developer-id')]
    [string]$TrustMode,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$BaseNotesPath = (Join-Path $PSScriptRoot '..\docs\operations\v1.1.2-release-notes.md')
)

$ErrorActionPreference = 'Stop'

$baseNotes = Get-Content -LiteralPath $BaseNotesPath -Raw
$trustDisclosure = if ($TrustMode -eq 'developer-id') {
    @'
## Published macOS trust mode

The macOS artifacts use Developer ID signing. Apple notarization, stapling,
strict codesign verification, Gatekeeper assessment, DMG remount verification,
and packaged-app launch validation completed before publication.
'@
}
else {
    @'
## Published macOS trust mode

The macOS artifacts use ad-hoc identity `-` and passed strict
`codesign --verify --deep --strict`, DMG remount verification, and packaged-app
launch validation. They are not notarized. This macOS release does not have Gatekeeper distribution trust.
'@
}

$parent = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($parent)) {
    [void](New-Item -ItemType Directory -Path $parent -Force)
}

"$($baseNotes.TrimEnd())`n`n$($trustDisclosure.Trim())`n" |
    Set-Content -LiteralPath $OutputPath -Encoding utf8
