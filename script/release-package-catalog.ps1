function Get-ReleasePackageCatalog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedVersion
    )

    @(
        [pscustomobject]@{
            Scope = 'Windows'
            Name = "DownKyi-$ExpectedVersion-1.win-x64.zip"
            Kind = 'zip'
            RuntimeIdentifier = 'win-x64'
            Manifest = 'publish-manifest-win-x64.json'
            Transport = $null
        },
        [pscustomobject]@{
            Scope = 'Windows'
            Name = "DownKyi-$ExpectedVersion-1.win-x86.zip"
            Kind = 'zip'
            RuntimeIdentifier = 'win-x86'
            Manifest = 'publish-manifest-win-x86.json'
            Transport = $null
        },
        [pscustomobject]@{
            Scope = 'LinuxX64'
            Name = "DownKyi-${ExpectedVersion}_linux_self-contained.x86_64.AppImage"
            Kind = 'AppImage'
            RuntimeIdentifier = 'linux-x64'
            Manifest = 'publish-manifest-linux-x64-AppImage.json'
            Transport = 'appimage-x64.transport.tar'
        },
        [pscustomobject]@{
            Scope = 'LinuxX64'
            Name = "downkyi_${ExpectedVersion}_linux_self-contained_amd64.deb"
            Kind = 'deb'
            RuntimeIdentifier = 'linux-x64'
            Manifest = 'publish-manifest-linux-x64-deb.json'
            Transport = $null
        },
        [pscustomobject]@{
            Scope = 'LinuxX64'
            Name = "downkyi_${ExpectedVersion}_linux_self-contained.x86_64.rpm"
            Kind = 'rpm'
            RuntimeIdentifier = 'linux-x64'
            Manifest = 'publish-manifest-linux-x64-rpm.json'
            Transport = $null
        },
        [pscustomobject]@{
            Scope = 'LinuxArm64'
            Name = "DownKyi-${ExpectedVersion}_linux_self-contained.aarch64.AppImage"
            Kind = 'AppImage'
            RuntimeIdentifier = 'linux-arm64'
            Manifest = 'publish-manifest-linux-arm64-AppImage.json'
            Transport = 'appimage-arm64.transport.tar'
        },
        [pscustomobject]@{
            Scope = 'LinuxArm64'
            Name = "downkyi_${ExpectedVersion}_linux_self-contained_arm64.deb"
            Kind = 'deb'
            RuntimeIdentifier = 'linux-arm64'
            Manifest = 'publish-manifest-linux-arm64-deb.json'
            Transport = $null
        },
        [pscustomobject]@{
            Scope = 'MacOSX64'
            Name = "DownKyi-$ExpectedVersion-osx-x64.dmg"
            Kind = 'dmg'
            RuntimeIdentifier = 'osx-x64'
            Manifest = 'publish-manifest-osx-x64.json'
            Transport = $null
        },
        [pscustomobject]@{
            Scope = 'MacOSArm64'
            Name = "DownKyi-$ExpectedVersion-osx-arm64.dmg"
            Kind = 'dmg'
            RuntimeIdentifier = 'osx-arm64'
            Manifest = 'publish-manifest-osx-arm64.json'
            Transport = $null
        }
    )
}
