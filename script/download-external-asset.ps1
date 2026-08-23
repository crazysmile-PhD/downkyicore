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
                throw [InvalidDataException]::new('The downloaded external asset is empty.')
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
