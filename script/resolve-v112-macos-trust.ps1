[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$GitHubOutputPath
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath) -and
    [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    throw 'OutputPath or GitHubOutputPath is required.'
}

$credentialNames = @(
    'MACOS_CERTIFICATE',
    'MACOS_CERTIFICATE_PWD',
    'APPLE_ID',
    'TEAM_ID',
    'APP_SPECIFIC_PASSWORD'
)
$presentNames = @(
    $credentialNames | Where-Object {
        -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
    }
)

if ($presentNames.Count -eq 0) {
    $trustMode = 'ad-hoc'
    $hasMacOsSigning = 'false'
}
elseif ($presentNames.Count -eq $credentialNames.Count) {
    $trustMode = 'developer-id'
    $hasMacOsSigning = 'true'
}
else {
    $missingNames = @($credentialNames | Where-Object { $_ -notin $presentNames })
    throw "Partial Apple credentials are not a valid release trust mode. Missing: $($missingNames -join ', ')."
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    [ordered]@{
        macosTrustMode = $trustMode
        hasMacosSigning = $hasMacOsSigning -eq 'true'
    } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
    @(
        "macos_trust_mode=$trustMode"
        "has_macos_signing=$hasMacOsSigning"
    ) | Add-Content -LiteralPath $GitHubOutputPath -Encoding utf8
}

Write-Output "Resolved macOS release trust mode: $trustMode"
