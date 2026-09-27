[CmdletBinding()]
param(
    [switch]$ConfirmAuthenticatedLive,
    [string]$EnvPath = (Join-Path $HOME '.codex/.env'),
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $ConfirmAuthenticatedLive) {
    throw 'This script sends an authenticated Cookie to read-only Bilibili APIs. Re-run with -ConfirmAuthenticatedLive.'
}

function Get-EnvValue {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Environment file was not found.'
    }

    $prefix = "^\s*(?:export\s+)?$([Regex]::Escape($Name))\s*="
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -notmatch $prefix) {
            continue
        }

        $value = $line.Substring($line.IndexOf('=') + 1).Trim()
        if ($value.Length -ge 2) {
            $first = $value[0]
            $last = $value[$value.Length - 1]
            if (($first -eq '"' -and $last -eq '"') -or
                ($first -eq "'" -and $last -eq "'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }

        if ([string]::IsNullOrWhiteSpace($value)) {
            throw 'Environment variable is empty.'
        }

        return $value
    }

    throw 'Environment variable declaration was not found.'
}

function Test-PropertyPath {
    param(
        [AllowNull()]
        [object]$Root,
        [Parameter(Mandatory)]
        [string]$Path
    )

    $current = $Root
    foreach ($segment in $Path.Split('.')) {
        if ($null -eq $current) {
            return $false
        }

        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) {
            return $false
        }

        $current = $property.Value
    }

    return $true
}

function New-BlockedResult {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [bool]$RequiresLogin
    )

    return [pscustomobject][ordered]@{
        Name = $Name
        Path = $Path
        HttpStatus = $null
        BilibiliCode = $null
        RequiresLogin = $RequiresLogin
        ResponseStructureMatchesExpected = $null
        RequiredFieldsPresent = $null
        ContractDrift = $null
        Outcome = 'blocked'
        ErrorType = 'PrerequisiteUnavailable'
    }
}

function Get-SafeRequestErrorType {
    param([Parameter(Mandatory)][Exception]$Exception)

    if ($Exception -is [TimeoutException] -or
        $Exception -is [TaskCanceledException]) {
        return 'TransportTimeout'
    }

    if ($Exception -is [System.Net.Http.HttpRequestException] -or
        $Exception -is [System.Net.WebException]) {
        return 'TransportFailure'
    }

    return 'RequestFailure'
}

function Invoke-ContractProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [Uri]$Uri,
        [Parameter(Mandatory)]
        [bool]$RequiresLogin,
        [Parameter(Mandatory)]
        [string[]]$RequiredFields,
        [Parameter(Mandatory)]
        [hashtable]$Headers,
        [Parameter(Mandatory)]
        [ref]$ResponseJson
    )

    $ResponseJson.Value = $null
    $result = [ordered]@{
        Name = $Name
        Path = $Path
        HttpStatus = $null
        BilibiliCode = $null
        RequiresLogin = $RequiresLogin
        ResponseStructureMatchesExpected = $false
        RequiredFieldsPresent = $null
        ContractDrift = $null
        Outcome = 'indeterminate'
        ErrorType = $null
    }

    try {
        $response = Invoke-WebRequest `
            -Uri $Uri `
            -Headers $Headers `
            -Method Get `
            -MaximumRedirection 3 `
            -SkipHttpErrorCheck `
            -TimeoutSec 20
        $result.HttpStatus = [int]$response.StatusCode

        try {
            $json = $response.Content | ConvertFrom-Json
        }
        catch {
            $result.Outcome = 'failed'
            $result.ContractDrift = $true
            $result.ErrorType = 'InvalidJson'
            return [pscustomobject]$result
        }

        $ResponseJson.Value = $json
        $codeProperty = $json.PSObject.Properties['code']
        if ($null -eq $codeProperty) {
            $result.Outcome = 'failed'
            $result.ContractDrift = $true
            $result.ErrorType = 'MissingApiCode'
            return [pscustomobject]$result
        }

        $result.ResponseStructureMatchesExpected = $true
        $result.BilibiliCode = [int]$codeProperty.Value
        if ($result.BilibiliCode -ne 0) {
            $result.Outcome = 'failed'
            $result.ErrorType = 'ApiRejected'
            return [pscustomobject]$result
        }

        $requiredFieldsPresent = $true
        foreach ($requiredField in $RequiredFields) {
            if (-not (Test-PropertyPath -Root $json -Path $requiredField)) {
                $requiredFieldsPresent = $false
                break
            }
        }

        $result.RequiredFieldsPresent = $requiredFieldsPresent
        $result.ContractDrift = -not $requiredFieldsPresent
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
            $result.Outcome = 'failed'
            $result.ErrorType = 'HttpFailure'
        }
        elseif (-not $requiredFieldsPresent) {
            $result.Outcome = 'failed'
            $result.ErrorType = 'MissingRequiredField'
        }
        else {
            $result.Outcome = 'passed'
        }
    }
    catch {
        $result.Outcome = 'failed'
        $result.ErrorType = Get-SafeRequestErrorType -Exception $_.Exception
    }

    return [pscustomobject]$result
}

function Add-Probe {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Results,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [Uri]$Uri,
        [Parameter(Mandatory)]
        [bool]$RequiresLogin,
        [Parameter(Mandatory)]
        [string[]]$RequiredFields,
        [Parameter(Mandatory)]
        [hashtable]$Headers,
        [Parameter(Mandatory)]
        [ref]$ResponseJson
    )

    $audit = Invoke-ContractProbe `
        -Name $Name `
        -Path $Path `
        -Uri $Uri `
        -RequiresLogin $RequiresLogin `
        -RequiredFields $RequiredFields `
        -Headers $Headers `
        -ResponseJson $ResponseJson
    $Results.Add($audit)
}

function Write-SanitizedReport {
    param(
        [Parameter(Mandatory)]
        [bool]$EnvironmentVariableLoaded,
        [Parameter(Mandatory)]
        [bool]$NavigationGatePassed,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Results,
        [string]$Destination
    )

    $commit = try {
        (& git rev-parse HEAD 2>$null).Trim()
    }
    catch {
        'unknown'
    }

    $report = [ordered]@{
        SchemaVersion = 1
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Runtime = $PSVersionTable.PSVersion.ToString()
        OperatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        Commit = $commit
        EnvironmentVariableLoaded = $EnvironmentVariableLoaded
        NavigationGatePassed = $NavigationGatePassed
        Results = @($Results)
    }

    $jsonReport = $report | ConvertTo-Json -Depth 6
    if (-not [string]::IsNullOrWhiteSpace($Destination)) {
        $resolvedOutput = [System.IO.Path]::GetFullPath($Destination)
        $parent = [System.IO.Path]::GetDirectoryName($resolvedOutput)
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            [System.IO.Directory]::CreateDirectory($parent) | Out-Null
        }

        Set-Content -LiteralPath $resolvedOutput -Value $jsonReport -Encoding utf8NoBOM
    }

    return $jsonReport
}

$environmentVariableName = 'BILIBILI_TEST_COOKIE'
$previousEnvironmentValue = [Environment]::GetEnvironmentVariable(
    $environmentVariableName,
    [EnvironmentVariableTarget]::Process)
$cookie = $null
$headers = $null
$results = [System.Collections.Generic.List[object]]::new()
$gatePassed = $false

try {
    $cookie = Get-EnvValue -Path $EnvPath -Name $environmentVariableName
    [Environment]::SetEnvironmentVariable(
        $environmentVariableName,
        $cookie,
        [EnvironmentVariableTarget]::Process)
    $headers = @{
        'Accept' = 'application/json'
        'Cookie' = $cookie
        'Referer' = 'https://www.bilibili.com/'
        'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/138.0 Safari/537.36'
    }

    $navJson = $null
    Add-Probe `
        -Results $results `
        -Name 'navigation-login-gate' `
        -Path '/x/web-interface/nav' `
        -Uri 'https://api.bilibili.com/x/web-interface/nav' `
        -RequiresLogin $false `
        -RequiredFields @('data', 'data.isLogin', 'data.mid') `
        -Headers $headers `
        -ResponseJson ([ref]$navJson)

    $navAudit = $results[0]
    $currentMid = 0L
    $midIsValid = $null -ne $navJson -and
        [long]::TryParse([string]$navJson.data.mid, [ref]$currentMid) -and
        $currentMid -gt 0
    $gatePassed = $navAudit.BilibiliCode -eq 0 -and
        $navAudit.RequiredFieldsPresent -eq $true -and
        $navJson.data.isLogin -eq $true -and
        $midIsValid
    if (-not $gatePassed) {
        $navAudit.Outcome = 'failed'
        $navAudit.ErrorType = 'NotAuthenticated'
        $reportJson = Write-SanitizedReport `
            -EnvironmentVariableLoaded $true `
            -NavigationGatePassed $false `
            -Results $results `
            -Destination $OutputPath
        $reportJson
        exit 2
    }

    $myInfoJson = $null
    Add-Probe $results 'current-account-info' '/x/space/myinfo' `
        'https://api.bilibili.com/x/space/myinfo' $true `
        @('data', 'data.mid', 'data.name', 'data.level') $headers ([ref]$myInfoJson)

    $historyJson = $null
    Add-Probe $results 'watch-history' '/x/web-interface/history/cursor' `
        'https://api.bilibili.com/x/web-interface/history/cursor?max=0&view_at=0&ps=1&business=' $true `
        @('data', 'data.cursor', 'data.list') $headers ([ref]$historyJson)

    $watchLaterJson = $null
    Add-Probe $results 'watch-later' '/x/v2/history/toview' `
        'https://api.bilibili.com/x/v2/history/toview' $true `
        @('data', 'data.count', 'data.list') $headers ([ref]$watchLaterJson)

    $createdFavoritesJson = $null
    Add-Probe $results 'created-favorites' '/x/v3/fav/folder/created/list' `
        "https://api.bilibili.com/x/v3/fav/folder/created/list?up_mid=$currentMid&pn=1&ps=1" $false `
        @('data', 'data.count', 'data.list') $headers ([ref]$createdFavoritesJson)

    $collectedFavoritesJson = $null
    Add-Probe $results 'collected-favorites' '/x/v3/fav/folder/collected/list' `
        "https://api.bilibili.com/x/v3/fav/folder/collected/list?up_mid=$currentMid&pn=1&ps=1" $true `
        @('data', 'data.count', 'data.list') $headers ([ref]$collectedFavoritesJson)

    $followersJson = $null
    Add-Probe $results 'followers' '/x/relation/followers' `
        "https://api.bilibili.com/x/relation/followers?vmid=$currentMid&pn=1&ps=1" $false `
        @('data', 'data.list', 'data.total') $headers ([ref]$followersJson)

    $followingsJson = $null
    Add-Probe $results 'followings' '/x/relation/followings' `
        "https://api.bilibili.com/x/relation/followings?vmid=$currentMid&pn=1&ps=1&order_type=attention" $false `
        @('data', 'data.list', 'data.total') $headers ([ref]$followingsJson)

    $blacksJson = $null
    Add-Probe $results 'block-list' '/x/relation/blacks' `
        'https://api.bilibili.com/x/relation/blacks?pn=1&ps=1' $true `
        @('data') $headers ([ref]$blacksJson)

    $groupsJson = $null
    Add-Probe $results 'following-groups' '/x/relation/tags' `
        'https://api.bilibili.com/x/relation/tags' $true `
        @('data') $headers ([ref]$groupsJson)

    $favoriteItems = if ($null -eq $createdFavoritesJson) {
        @()
    }
    else {
        @($createdFavoritesJson.data.list)
    }
    $favoriteId = 0L
    $favoriteIdIsValid = $favoriteItems.Count -gt 0 -and
        $null -ne $favoriteItems[0].PSObject.Properties['id'] -and
        [long]::TryParse([string]$favoriteItems[0].id, [ref]$favoriteId) -and
        $favoriteId -gt 0
    if ($favoriteIdIsValid) {
        $favoriteResourcesJson = $null
        Add-Probe $results 'favorite-resources' '/x/v3/fav/resource/list' `
            "https://api.bilibili.com/x/v3/fav/resource/list?media_id=$favoriteId&pn=1&ps=1&keyword=&order=mtime&type=0&tid=0&platform=web" $false `
            @('data', 'data.medias', 'data.has_more') $headers ([ref]$favoriteResourcesJson)

        $favoriteResourceIdsJson = $null
        Add-Probe $results 'favorite-resource-ids' '/x/v3/fav/resource/ids' `
            "https://api.bilibili.com/x/v3/fav/resource/ids?media_id=$favoriteId" $false `
            @('data') $headers ([ref]$favoriteResourceIdsJson)
    }
    else {
        $results.Add((New-BlockedResult 'favorite-resources' '/x/v3/fav/resource/list' $false))
        $results.Add((New-BlockedResult 'favorite-resource-ids' '/x/v3/fav/resource/ids' $false))
    }

    $groups = if ($null -eq $groupsJson) {
        @()
    }
    else {
        @($groupsJson.data)
    }
    $groupId = 0L
    $groupIdIsValid = $groups.Count -gt 0 -and
        $null -ne $groups[0].PSObject.Properties['tagid'] -and
        [long]::TryParse([string]$groups[0].tagid, [ref]$groupId)
    if ($groupIdIsValid) {
        $groupContentJson = $null
        Add-Probe $results 'following-group-content' '/x/relation/tag' `
            "https://api.bilibili.com/x/relation/tag?tagid=$groupId&pn=1&ps=1&order_type=attention" $true `
            @('data') $headers ([ref]$groupContentJson)
    }
    else {
        $results.Add((New-BlockedResult 'following-group-content' '/x/relation/tag' $true))
    }

    Write-SanitizedReport `
        -EnvironmentVariableLoaded $true `
        -NavigationGatePassed $true `
        -Results $results `
        -Destination $OutputPath
}
finally {
    if ($null -ne $headers) {
        $headers.Clear()
    }

    $cookie = $null
    [Environment]::SetEnvironmentVariable(
        $environmentVariableName,
        $previousEnvironmentValue,
        [EnvironmentVariableTarget]::Process)
}
