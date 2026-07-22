[CmdletBinding()]
param(
    [switch]$ConfirmLive,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $ConfirmLive) {
    throw 'This script sends anonymous requests to public Bilibili endpoints. Re-run with -ConfirmLive.'
}

$headers = @{
    'User-Agent' = 'Mozilla/5.0 (compatible; DownKyi-Api-Audit/1.0)'
    'Referer' = 'https://www.bilibili.com/'
}

$probes = @(
    @{ Name = 'finger-spi'; Uri = 'https://api.bilibili.com/x/frontend/finger/spi'; Kind = 'json' }
    @{ Name = 'navigation'; Uri = 'https://api.bilibili.com/x/web-interface/nav'; Kind = 'json' }
    @{ Name = 'ordinary-view-public-control'; Uri = 'https://api.bilibili.com/x/web-interface/view?bvid=BV17x411w7KC'; Kind = 'json' }
    @{ Name = 'ordinary-description'; Uri = 'https://api.bilibili.com/x/web-interface/archive/desc?bvid=BV17x411w7KC'; Kind = 'json' }
    @{ Name = 'ordinary-page-list'; Uri = 'https://api.bilibili.com/x/player/pagelist?bvid=BV17x411w7KC'; Kind = 'json' }
    @{ Name = 'ordinary-tags'; Uri = 'https://api.bilibili.com/x/web-interface/view/detail/tag?bvid=BV17x411w7KC&cid=279786'; Kind = 'json' }
    @{ Name = 'relation-stat'; Uri = 'https://api.bilibili.com/x/relation/stat?vmid=2'; Kind = 'json' }
    @{ Name = 'up-stat'; Uri = 'https://api.bilibili.com/x/space/upstat?mid=2'; Kind = 'json' }
    @{ Name = 'space-settings'; Uri = 'https://space.bilibili.com/ajax/settings/getSettings?mid=2'; Kind = 'json' }
    @{ Name = 'retired-channel-list'; Uri = 'https://api.bilibili.com/x/space/channel/list?mid=2'; Kind = 'json' }
    @{ Name = 'seasons-series'; Uri = 'https://api.bilibili.com/x/polymer/web-space/seasons_series_list?mid=2&page_num=1&page_size=1'; Kind = 'json' }
    @{ Name = 'series-metadata'; Uri = 'https://api.bilibili.com/x/series/series?series_id=1'; Kind = 'json' }
    @{ Name = 'ranking-region'; Uri = 'https://api.bilibili.com/x/web-interface/ranking/region?rid=1&day=3&ps=0'; Kind = 'json' }
    @{ Name = 'dynamic-region'; Uri = 'https://api.bilibili.com/x/web-interface/dynamic/region?rid=1&pn=1&ps=1'; Kind = 'json' }
    @{ Name = 'bangumi-season'; Uri = 'https://api.bilibili.com/pgc/view/web/season?ep_id=21495'; Kind = 'json' }
    @{ Name = 'bangumi-play-v2'; Uri = 'https://api.bilibili.com/pgc/player/web/v2/playurl?ep_id=21495&qn=64&fnver=0&fnval=4048'; Kind = 'json' }
    @{ Name = 'cheese-season'; Uri = 'https://api.bilibili.com/pugv/view/web/season?ep_id=3489'; Kind = 'json' }
    @{ Name = 'cheese-episode-list'; Uri = 'https://api.bilibili.com/pugv/view/web/ep/list?season_id=205&pn=1&ps=1'; Kind = 'json' }
    @{ Name = 'cheese-play'; Uri = 'https://api.bilibili.com/pugv/player/web/playurl?ep_id=3489&qn=64&fnver=0&fnval=4048'; Kind = 'json' }
    @{ Name = 'favorites-created-page'; Uri = 'https://api.bilibili.com/x/v3/fav/folder/created/list?up_mid=2&pn=1&ps=1'; Kind = 'json' }
    @{ Name = 'favorites-created-all-alternative'; Uri = 'https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid=2'; Kind = 'json' }
    @{ Name = 'login-qr-generate'; Uri = 'https://passport.bilibili.com/x/passport-login/web/qrcode/generate'; Kind = 'json' }
    @{ Name = 'history-cursor-auth-control'; Uri = 'https://api.bilibili.com/x/web-interface/history/cursor?ps=1'; Kind = 'json' }
    @{ Name = 'watch-later-auth-control'; Uri = 'https://api.bilibili.com/x/v2/history/toview'; Kind = 'json' }
    @{ Name = 'watch-later-web-alternative'; Uri = 'https://api.bilibili.com/x/v2/history/toview/web?jsonp=jsonp'; Kind = 'json' }
    @{ Name = 'danmaku-segment'; Uri = 'https://api.bilibili.com/x/v2/dm/web/seg.so?type=1&oid=279786&pid=170001&segment_index=1'; Kind = 'binary' }
    @{ Name = 'danmaku-segment-wbi-alternative'; Uri = 'https://api.bilibili.com/x/v2/dm/wbi/web/seg.so?type=1&oid=279786&pid=170001&segment_index=1'; Kind = 'binary' }
)

function Get-SafeMessage {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    $message = ([string]$Value) -replace '[\r\n]+', ' '
    if ($message.Length -gt 160) {
        return $message.Substring(0, 160)
    }

    return $message
}

$results = foreach ($probe in $probes) {
    try {
        $response = Invoke-WebRequest `
            -Uri $probe.Uri `
            -Headers $headers `
            -Method Get `
            -MaximumRedirection 3 `
            -SkipHttpErrorCheck `
            -TimeoutSec 20

        $contentType = [string]$response.Headers.'Content-Type'
        $result = [ordered]@{
            Name = $probe.Name
            HttpStatus = [int]$response.StatusCode
            ApiCode = $null
            Message = $null
            Envelope = $null
            ContentType = $contentType
            Bytes = $response.RawContentLength
        }

        if ($probe.Kind -eq 'json' -and -not [string]::IsNullOrWhiteSpace($response.Content)) {
            try {
                $json = $response.Content | ConvertFrom-Json
                $codeProperty = $json.PSObject.Properties['code']
                $messageProperty = $json.PSObject.Properties['message']
                $result.ApiCode = if ($null -eq $codeProperty) { $null } else { $codeProperty.Value }
                $result.Message = if ($null -eq $messageProperty) {
                    $null
                }
                else {
                    Get-SafeMessage $messageProperty.Value
                }
                $result.Envelope = @($json.PSObject.Properties.Name) -join ','
            }
            catch {
                $result.Message = 'non-JSON response'
            }
        }

        [pscustomobject]$result
    }
    catch {
        [pscustomobject][ordered]@{
            Name = $probe.Name
            HttpStatus = $null
            ApiCode = $null
            Message = Get-SafeMessage $_.Exception.Message
            Envelope = $null
            ContentType = $null
            Bytes = 0
        }
    }
}

$commit = try {
    (& git rev-parse HEAD 2>$null).Trim()
}
catch {
    'unknown'
}

$report = [ordered]@{
    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Runtime = $PSVersionTable.PSVersion.ToString()
    OperatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    Commit = $commit
    Authentication = 'anonymous; no Cookie header or local login state is loaded'
    Results = @($results)
}

$jsonReport = $report | ConvertTo-Json -Depth 6
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    $parent = [System.IO.Path]::GetDirectoryName($resolvedOutput)
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    Set-Content -LiteralPath $resolvedOutput -Value $jsonReport -Encoding utf8NoBOM
}

$jsonReport
