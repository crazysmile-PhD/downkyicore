function ConvertTo-DownKyiPowerShellArgumentList {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$BoundParameters
    )

    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $BoundParameters.GetEnumerator()) {
        $value = $entry.Value
        if ($value -is [System.Management.Automation.SwitchParameter]) {
            if ($value.IsPresent) {
                $arguments.Add("-$($entry.Key)")
            }
            continue
        }

        $arguments.Add("-$($entry.Key)")
        if ($value -is [Array]) {
            foreach ($item in $value) {
                $arguments.Add([string]$item)
            }
        }
        else {
            $arguments.Add([string]$value)
        }
    }

    return $arguments.ToArray()
}

function Test-DownKyiDelegatedCgroupScopeRequired {
    [CmdletBinding()]
    param()

    return $IsLinux -and -not [string]::Equals(
        $env:DOWNKYI_DELEGATED_CGROUP_SCOPE,
        "1",
        [StringComparison]::Ordinal)
}

function Invoke-DownKyiDelegatedCgroupScope {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ScriptPath,

        [string[]]$ArgumentList = @()
    )

    if (-not (Test-DownKyiDelegatedCgroupScopeRequired)) {
        throw "A delegated Linux cgroup scope was requested outside its acquisition boundary."
    }

    $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
    & systemd-run `
        --user `
        --scope `
        --quiet `
        -p Delegate=yes `
        --setenv=DOWNKYI_DELEGATED_CGROUP_SCOPE=1 `
        $pwsh `
        -NoProfile `
        -File $ScriptPath `
        @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "The delegated Linux repository process scope failed."
    }

}
