[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CandidateDirectory,
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [ValidateRange(1, 10)][int]$MaximumAttempts = 3,
    [ValidateRange(0, 300)][int]$RetryDelaySeconds = 10
)

$ErrorActionPreference = 'Stop'
$candidateRoot = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifest = Get-Content -LiteralPath (Join-Path $candidateRoot 'candidate.json') -Raw | ConvertFrom-Json
$tag = [string]$manifest.tag
$candidateSha = [string]$manifest.candidateSha
$notesPath = Join-Path $candidateRoot ([string]$manifest.releaseNotes.name)
$assetPaths = @($manifest.assets | ForEach-Object { Join-Path $candidateRoot ([string]$_.name) })

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $output = @(& git -C $repository @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return $output
}

function Get-RemoteTagTarget {
    $remote = @(& git -C $repository ls-remote --refs origin "refs/tags/$tag" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read remote tag ${tag}: $($remote -join [Environment]::NewLine)"
    }
    if ($remote.Count -eq 0) {
        return $null
    }
    if ($remote.Count -ne 1) {
        throw "Remote tag identity is ambiguous for $tag."
    }
    Invoke-Git -Arguments @('fetch', '--force', 'origin', "refs/tags/$tag`:refs/tags/$tag") | Out-Null
    $tagType = (Invoke-Git -Arguments @('cat-file', '-t', $tag)).Trim()
    if ($tagType -ne 'tag') {
        throw "Existing release tag $tag is not annotated."
    }
    return (Invoke-Git -Arguments @('rev-parse', "$tag^{}")).Trim()
}

function Ensure-ReleaseTag {
    $remoteTarget = Get-RemoteTagTarget
    if ($null -ne $remoteTarget) {
        if (-not [string]::Equals($remoteTarget, $candidateSha, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release tag $tag already points to $remoteTarget instead of candidate $candidateSha."
        }
        Write-Output "Release tag $tag already points to the candidate; continuing the retry."
        return
    }

    & git -C $repository tag -d $tag 2>$null | Out-Null
    Invoke-Git -Arguments @('tag', '-a', $tag, $candidateSha, '-m', $tag) | Out-Null
    $push = @(& git -C $repository push origin "refs/tags/$tag" 2>&1)
    if ($LASTEXITCODE -eq 0) {
        Write-Output "Created annotated release tag $tag at $candidateSha."
        return
    }

    $raceTarget = Get-RemoteTagTarget
    if ($null -ne $raceTarget -and
        [string]::Equals($raceTarget, $candidateSha, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Output "Release tag $tag was created concurrently at the same candidate; continuing."
        return
    }
    throw "Failed to create release tag ${tag}: $($push -join [Environment]::NewLine)"
}

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Operation,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            & $Action
            return
        }
        catch {
            if ($attempt -eq $MaximumAttempts) {
                throw "$Operation failed after $MaximumAttempts attempts: $($_.Exception.Message)"
            }
            Write-Warning "$Operation attempt $attempt failed; retrying the same candidate and artifacts. $($_.Exception.Message)"
            if ($RetryDelaySeconds -gt 0) {
                Start-Sleep -Seconds $RetryDelaySeconds
            }
        }
    }
}

function Invoke-Gh {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $output = @(& gh @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return $output
}

function Get-Release {
    $output = @(& gh release view $tag --json tagName,isDraft,isPrerelease,assets 2>&1)
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    return (($output -join [Environment]::NewLine) | ConvertFrom-Json)
}

function Assert-ReleaseAssets {
    param([Parameter(Mandatory = $true)]$Release)

    $expected = @{}
    foreach ($asset in @($manifest.assets)) {
        $expected[[string]$asset.name] = $asset
    }
    $actual = @($Release.assets)
    if ($actual.Count -ne $expected.Count) {
        throw "GitHub Release has $($actual.Count) assets; expected $($expected.Count)."
    }
    foreach ($asset in $actual) {
        $name = [string]$asset.name
        if (-not $expected.ContainsKey($name) -or
            [long]$expected[$name].size -ne [long]$asset.size) {
            throw "GitHub Release asset does not match the sealed candidate: $($asset.name)"
        }
        $digest = [string]$asset.digest
        if (-not [string]::IsNullOrWhiteSpace($digest) -and
            -not [string]::Equals($digest, "sha256:$($expected[$name].sha256)", [StringComparison]::OrdinalIgnoreCase)) {
            throw "GitHub Release asset digest does not match the sealed candidate: $name"
        }
    }
}

Invoke-WithRetry 'Annotated tag publication' { Ensure-ReleaseTag }

Invoke-WithRetry 'GitHub Release publication' {
    $release = Get-Release
    if ($null -eq $release) {
        Invoke-Gh -Arguments @('release', 'create', $tag, '--verify-tag', '--draft', '--title', "DownKyi $tag", '--notes-file', $notesPath) | Out-Null
    }
    else {
        if (-not $release.isDraft -and -not $release.isPrerelease) {
            Assert-ReleaseAssets -Release $release
            Write-Output "GitHub Release $tag is already published with the sealed asset set."
            return
        }
        $unexpectedAssets = @(
            $release.assets | Where-Object {
                $name = [string]$_.name
                -not (@($manifest.assets.name) -contains $name)
            }
        )
        if ($unexpectedAssets.Count -ne 0) {
            throw "Existing GitHub Release contains assets outside this candidate: $($unexpectedAssets.name -join ', ')"
        }
    }

    Invoke-Gh -Arguments (@('release', 'upload', $tag) + $assetPaths + @('--clobber')) | Out-Null
    $uploaded = Get-Release
    if ($null -eq $uploaded) {
        throw 'GitHub Release disappeared after asset upload.'
    }
    Assert-ReleaseAssets -Release $uploaded
    Invoke-Gh -Arguments @('release', 'edit', $tag, '--draft=false', '--prerelease=false', '--latest', '--title', "DownKyi $tag", '--notes-file', $notesPath) | Out-Null
    $published = Get-Release
    if ($null -eq $published -or $published.isDraft -or $published.isPrerelease) {
        throw 'GitHub Release did not reach the stable published state.'
    }
    Assert-ReleaseAssets -Release $published
}

Write-Output "Published $tag from candidate $candidateSha using the sealed Prepare Release artifacts."
