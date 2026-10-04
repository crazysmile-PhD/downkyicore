function Get-DownKyiCiInfrastructureTestSlices {
    return @(
        [pscustomobject]@{
            ProjectPath = 'tests/DownKyi.Architecture.Tests/DownKyi.Architecture.Tests.csproj'
            Platforms = @('Windows', 'Linux', 'macOS')
            ClassNames = @(
                'DownKyi.Architecture.Tests.CentralTestRunnerCancellationComponentTests'
                'DownKyi.Architecture.Tests.CentralTestRunnerCommandTests'
                'DownKyi.Architecture.Tests.CentralTestRunnerFixtureDispatchTests'
                'DownKyi.Architecture.Tests.CentralTestRunnerRecorderTests'
                'DownKyi.Architecture.Tests.FlightRecorderOutputTests'
                'DownKyi.Architecture.Tests.PowerShellTestEntryContractTests'
            )
        }
        [pscustomobject]@{
            ProjectPath = 'tests/DownKyi.Windows.Tests/DownKyi.Windows.Tests.csproj'
            Platforms = @('Windows')
            ClassNames = @(
                'DownKyi.PlatformShared.Tests.OwnedProcessScopePlatformTests'
                'DownKyi.Windows.Tests.TargetedResourceForensicsWindowsTests'
                'DownKyi.Windows.Tests.TestDataIsolationFixtureWindowsTests'
                'DownKyi.Windows.Tests.WindowsEtwResourceFlightRecorderTests'
                'DownKyi.Windows.Tests.WindowsProcessSnapshotTests'
            )
        }
        [pscustomobject]@{
            ProjectPath = 'tests/DownKyi.Linux.Tests/DownKyi.Linux.Tests.csproj'
            Platforms = @('Linux')
            ClassNames = @('DownKyi.PlatformShared.Tests.OwnedProcessScopePlatformTests')
        }
        [pscustomobject]@{
            ProjectPath = 'tests/DownKyi.MacOS.Tests/DownKyi.MacOS.Tests.csproj'
            Platforms = @('macOS')
            ClassNames = @('DownKyi.PlatformShared.Tests.OwnedProcessScopePlatformTests')
        }
    )
}

function Get-DownKyiCiInfrastructureTestClassNames {
    return @(
        Get-DownKyiCiInfrastructureTestSlices |
            ForEach-Object { $_.ClassNames } |
            Sort-Object -Unique
    )
}
