Set-StrictMode -Version Latest

$script:OwnershipRootName = 'downkyi-ca1506'
$script:OperationNamePattern = '^[0-9a-f]{32}$'
$script:DefaultTimeToLive = [TimeSpan]::FromHours(24)

function Test-Ca1506OwnedOperationPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$OwnershipRoot,
        [Parameter(Mandatory)]
        [string]$OperationDirectory,
        [Parameter(Mandatory)]
        [IO.FileAttributes]$Attributes
    )

    if (($OperationDirectory -split '[\\/]') -contains '..') {
        return $false
    }

    $comparison = if ($IsWindows) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    $resolvedRoot = [IO.Path]::TrimEndingDirectorySeparator(
        [IO.Path]::GetFullPath($OwnershipRoot))
    $resolvedOperation = [IO.Path]::TrimEndingDirectorySeparator(
        [IO.Path]::GetFullPath($OperationDirectory))
    $parent = [IO.Path]::GetDirectoryName($resolvedOperation)
    $name = [IO.Path]::GetFileName($resolvedOperation)

    return $null -ne $parent -and
        $parent.Equals($resolvedRoot, $comparison) -and
        $name -cmatch $script:OperationNamePattern -and
        ($Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0
}

function Remove-Ca1506TemporaryWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$OwnershipRoot,
        [Parameter(Mandatory)]
        [string]$OperationDirectory,
        [scriptblock]$GetAttributes = {
            param($Path)
            [IO.File]::GetAttributes($Path)
        },
        [scriptblock]$DeleteDirectory = {
            param($Path)
            [IO.Directory]::Delete($Path, $true)
        }
    )

    try {
        $resolvedOperation = [IO.Path]::GetFullPath($OperationDirectory)
        if (-not [IO.Directory]::Exists($resolvedOperation)) {
            return $true
        }

        $attributes = & $GetAttributes $resolvedOperation
        if (-not (Test-Ca1506OwnedOperationPath `
                -OwnershipRoot $OwnershipRoot `
                -OperationDirectory $resolvedOperation `
                -Attributes $attributes)) {
            return $false
        }

        & $DeleteDirectory $resolvedOperation
        return -not [IO.Directory]::Exists($resolvedOperation)
    }
    catch {
        return $false
    }
}

function Invoke-Ca1506TemporaryScavenging {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$OwnershipRoot,
        [DateTimeOffset]$UtcNow = [DateTimeOffset]::UtcNow,
        [TimeSpan]$TimeToLive = $script:DefaultTimeToLive,
        [scriptblock]$DeleteDirectory = {
            param($Path)
            [IO.Directory]::Delete($Path, $true)
        }
    )

    $cutoff = $UtcNow.UtcDateTime.Subtract($TimeToLive)
    foreach ($operationDirectory in [IO.Directory]::EnumerateDirectories(
            $OwnershipRoot,
            '*',
            [IO.SearchOption]::TopDirectoryOnly)) {
        try {
            $directory = [IO.DirectoryInfo]::new($operationDirectory)
            $attributes = $directory.Attributes
            if (-not (Test-Ca1506OwnedOperationPath `
                    -OwnershipRoot $OwnershipRoot `
                    -OperationDirectory $directory.FullName `
                    -Attributes $attributes)) {
                continue
            }
            if ($directory.LastWriteTimeUtc -gt $cutoff) {
                continue
            }

            Remove-Ca1506TemporaryWorkspace `
                -OwnershipRoot $OwnershipRoot `
                -OperationDirectory $directory.FullName `
                -DeleteDirectory $DeleteDirectory | Out-Null
        }
        catch {
            continue
        }
    }
}

function New-Ca1506TemporaryWorkspace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TemporaryBasePath,
        [DateTimeOffset]$UtcNow = [DateTimeOffset]::UtcNow,
        [TimeSpan]$TimeToLive = $script:DefaultTimeToLive,
        [scriptblock]$DeleteDirectory = {
            param($Path)
            [IO.Directory]::Delete($Path, $true)
        }
    )

    $temporaryBase = [IO.Path]::GetFullPath($TemporaryBasePath)
    $ownershipRoot = [IO.Path]::GetFullPath(
        (Join-Path $temporaryBase $script:OwnershipRootName))
    [IO.Directory]::CreateDirectory($ownershipRoot) | Out-Null
    if (([IO.File]::GetAttributes($ownershipRoot) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The CA1506 temporary ownership root is not a physical directory.'
    }

    Invoke-Ca1506TemporaryScavenging `
        -OwnershipRoot $ownershipRoot `
        -UtcNow $UtcNow `
        -TimeToLive $TimeToLive `
        -DeleteDirectory $DeleteDirectory

    $operationName = [Guid]::NewGuid().ToString('N')
    $operationDirectory = [IO.Path]::GetFullPath((Join-Path $ownershipRoot $operationName))
    if ([IO.Directory]::Exists($operationDirectory)) {
        throw 'The CA1506 temporary operation directory already exists.'
    }

    [IO.Directory]::CreateDirectory($operationDirectory) | Out-Null
    $attributes = [IO.File]::GetAttributes($operationDirectory)
    if (-not (Test-Ca1506OwnedOperationPath `
            -OwnershipRoot $ownershipRoot `
            -OperationDirectory $operationDirectory `
            -Attributes $attributes)) {
        throw 'The CA1506 temporary operation directory failed ownership validation.'
    }

    return [pscustomobject]@{
        OwnershipRoot = $ownershipRoot
        OperationDirectory = $operationDirectory
    }
}

Export-ModuleMember -Function @(
    'Invoke-Ca1506TemporaryScavenging',
    'New-Ca1506TemporaryWorkspace',
    'Remove-Ca1506TemporaryWorkspace',
    'Test-Ca1506OwnedOperationPath'
)
