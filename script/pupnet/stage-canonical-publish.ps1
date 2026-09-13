$ErrorActionPreference = 'Stop'

$source = $env:CANONICAL_PUBLISH_DIRECTORY
$destination = $env:BUILD_APP_BIN
if ([string]::IsNullOrWhiteSpace($source)) {
    throw 'CANONICAL_PUBLISH_DIRECTORY is required for standalone PupNet packaging.'
}
if ([string]::IsNullOrWhiteSpace($destination)) {
    throw 'BUILD_APP_BIN was not supplied by PupNet.'
}

$sourceDirectory = (Resolve-Path -LiteralPath $source).Path
if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
    throw 'PupNet BUILD_APP_BIN does not exist.'
}
$destinationDirectory = (Resolve-Path -LiteralPath $destination).Path
if ([String]::Equals($sourceDirectory, $destinationDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Canonical publish source and PupNet staging destination must be distinct.'
}

$sourceEntries = @(Get-ChildItem -LiteralPath $sourceDirectory -Force)
if ($sourceEntries.Count -eq 0) {
    throw 'Canonical publish payload is empty.'
}
if (@(Get-ChildItem -LiteralPath $destinationDirectory -Force).Count -ne 0) {
    throw 'PupNet staging destination must be empty before the canonical payload is copied.'
}

foreach ($entry in $sourceEntries) {
    Copy-Item -LiteralPath $entry.FullName -Destination $destinationDirectory -Recurse -Force
}

Write-Output "Staged $($sourceEntries.Count) canonical publish entries for PupNet packaging."
