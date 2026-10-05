[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ToolPath
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot ".config/dotnet-tools.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The repository tool manifest is missing: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$pupnet = $manifest.tools."kuiperzone.pupnet"
if ($null -eq $pupnet -or [string]::IsNullOrWhiteSpace([string]$pupnet.version)) {
    throw "The repository tool manifest does not declare a concrete KuiperZone.PupNet version."
}

& dotnet tool install `
    --tool-path $ToolPath `
    KuiperZone.PupNet `
    --version ([string]$pupnet.version)
if ($LASTEXITCODE -ne 0) {
    throw "KuiperZone.PupNet installation failed with exit code $LASTEXITCODE."
}
