function Invoke-ExternalAssetDownload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Uri]$Uri,

        [Parameter(Mandatory)]
        [string]$Destination,

        [ValidateRange(1, 10)]
        [int]$MaximumAttempts = 3,

        [ValidateRange(0, 60)]
        [int]$RetryDelaySeconds = 2,

        [scriptblock]$TransferOperation = {
            param([Uri]$Source, [string]$Target)

            Start-BitsTransfer -Source $Source.AbsoluteUri -Destination $Target
        }
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Destination) {
                Remove-Item -LiteralPath $Destination -Force
            }

            & $TransferOperation $Uri $Destination

            $download = Get-Item -LiteralPath $Destination -ErrorAction Stop
            if ($download.Length -le 0) {
                throw [IO.InvalidDataException]::new('The downloaded external asset is empty.')
            }

            return
        }
        catch {
            if (Test-Path -LiteralPath $Destination) {
                Remove-Item -LiteralPath $Destination -Force
            }

            if ($attempt -eq $MaximumAttempts) {
                throw
            }

            if ($RetryDelaySeconds -gt 0) {
                Start-Sleep -Seconds $RetryDelaySeconds
            }
        }
    }
}

function Install-VerifiedExternalAsset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Uri]$Uri,

        [Parameter(Mandatory)]
        [string]$Destination,

        [Parameter(Mandatory)]
        [string]$StagingDirectory,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9a-fA-F]{64}$')]
        [string]$Sha256,

        [Parameter(Mandatory)]
        [ValidateRange(1, [long]::MaxValue)]
        [long]$ExpectedSize,

        [ValidateRange(1, 10)]
        [int]$MaximumAttempts = 3,

        [ValidateRange(0, 60)]
        [int]$RetryDelaySeconds = 2,

        [Parameter(Mandatory)]
        [scriptblock]$TransferOperation
    )

    $destinationPath = [IO.Path]::GetFullPath($Destination)
    $stagingPath = [IO.Path]::GetFullPath($StagingDirectory)
    $stagedAsset = Join-Path $stagingPath ([IO.Path]::GetRandomFileName())

    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    try {
        Invoke-ExternalAssetDownload `
            -Uri $Uri `
            -Destination $stagedAsset `
            -MaximumAttempts $MaximumAttempts `
            -RetryDelaySeconds $RetryDelaySeconds `
            -TransferOperation $TransferOperation

        $download = Get-Item -LiteralPath $stagedAsset -ErrorAction Stop
        if ($download.Length -ne $ExpectedSize) {
            throw [IO.InvalidDataException]::new(
                "External asset size mismatch. Expected $ExpectedSize bytes, got $($download.Length).")
        }

        $actualSha256 = (Get-FileHash -LiteralPath $stagedAsset -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualSha256, $Sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw [IO.InvalidDataException]::new(
                "External asset checksum mismatch. Expected $Sha256, got $actualSha256.")
        }

        $destinationDirectory = [IO.Path]::GetDirectoryName($destinationPath)
        if ([string]::IsNullOrWhiteSpace($destinationDirectory)) {
            throw [IO.InvalidDataException]::new("External asset destination has no parent directory: $destinationPath")
        }

        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Move-Item -LiteralPath $stagedAsset -Destination $destinationPath -Force
    }
    finally {
        if (Test-Path -LiteralPath $stagedAsset) {
            Remove-Item -LiteralPath $stagedAsset -Force
        }
    }
}
