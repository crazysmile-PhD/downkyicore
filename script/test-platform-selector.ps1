[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "test-project-runner.ps1")

function Assert-SelectorCondition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-SelectedProjectNames {
    param(
        [Parameter(Mandatory)]
        [object[]]$Projects,

        [Parameter(Mandatory)]
        [string]$Platform
    )

    return @(
        Select-DownKyiTestProjectsForCurrentPlatform `
            -Projects $Projects `
            -CurrentPlatform $Platform |
            Select-Object -ExpandProperty BaseName
    )
}

function Assert-PlatformDeclarationFails {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [string]$ExpectedMessage,

        [Parameter(Mandatory)]
        [string]$FailureClass
    )

    try {
        @(Get-DownKyiTestProjectPlatforms -ProjectPath $ProjectPath) | Out-Null
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw
        }

        return
    }

    throw "$FailureClass did not fail closed."
}

$projects = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "tests") `
        -Filter "*.Tests.csproj" `
        -File `
        -Recurse |
        Sort-Object FullName
)
$windowsProjects = @(Get-SelectedProjectNames -Projects $projects -Platform "Windows")
$linuxProjects = @(Get-SelectedProjectNames -Projects $projects -Platform "Linux")
$macProjects = @(Get-SelectedProjectNames -Projects $projects -Platform "macOS")

Assert-SelectorCondition `
    -Condition ($windowsProjects -contains "DownKyi.Windows.Tests") `
    -Message "Windows selector omitted the Windows-owned project."
Assert-SelectorCondition `
    -Condition ($windowsProjects -notcontains "DownKyi.Linux.Tests") `
    -Message "Windows selector included the Linux-owned project."
Assert-SelectorCondition `
    -Condition ($windowsProjects -notcontains "DownKyi.MacOS.Tests") `
    -Message "Windows selector included the macOS-owned project."
Assert-SelectorCondition `
    -Condition ($linuxProjects -contains "DownKyi.Linux.Tests") `
    -Message "Linux selector omitted the Linux-owned project."
Assert-SelectorCondition `
    -Condition ($linuxProjects -notcontains "DownKyi.Windows.Tests" -and
        $linuxProjects -notcontains "DownKyi.MacOS.Tests") `
    -Message "Linux selector included a native project owned by another OS."
Assert-SelectorCondition `
    -Condition ($macProjects -contains "DownKyi.MacOS.Tests") `
    -Message "macOS selector omitted the macOS signing project."
Assert-SelectorCondition `
    -Condition ($macProjects -notcontains "DownKyi.Windows.Tests") `
    -Message "macOS selector included the Windows-owned project."
Assert-SelectorCondition `
    -Condition ($macProjects -notcontains "DownKyi.Linux.Tests") `
    -Message "macOS selector included the Linux-owned project."

$fixtureRoot = Join-Path (
    [System.IO.Path]::GetTempPath()) "downkyi-platform-selector-$([Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
try {
    $missingProject = Join-Path $fixtureRoot "Missing.Tests.csproj"
    $unknownProject = Join-Path $fixtureRoot "Unknown.Tests.csproj"
    [System.IO.File]::WriteAllText(
        $missingProject,
        "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup /></Project>")
    [System.IO.File]::WriteAllText(
        $unknownProject,
        ("<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup>" +
            "<DownKyiTestPlatforms>HaikuOS</DownKyiTestPlatforms>" +
            "</PropertyGroup></Project>"))

    Assert-PlatformDeclarationFails `
        -ProjectPath $missingProject `
        -ExpectedMessage "exactly one unconditional" `
        -FailureClass "missing ownership"
    Assert-PlatformDeclarationFails `
        -ProjectPath $unknownProject `
        -ExpectedMessage "Unsupported DownKyiTestPlatforms" `
        -FailureClass "unknown platform"
}
finally {
    if ([System.IO.Directory]::Exists($fixtureRoot)) {
        [System.IO.Directory]::Delete($fixtureRoot, $true)
    }
}

Write-Host "Test platform selector regression passed."
