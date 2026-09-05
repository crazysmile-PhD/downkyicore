using System.Runtime.Versioning;
using DownKyi.Architecture.Tests;

namespace DownKyi.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class V113ReleasePackageTests
{
    [Fact]
    public async Task FinalPackageValidationRejectsMissingExecuteBits()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingExecuteBits();
    }

    [Fact]
    public async Task FinalPackageValidationRejectsArchitectureMismatch()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsArchitectureMismatch();
    }

    [Fact]
    public async Task FinalPackageValidationRejectsOwnerOnlyExecuteBits()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsOwnerOnlyExecuteBits();
    }

    [Fact]
    public async Task FinalPackageValidationRejectsCrossFormatBinary()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsCrossFormatBinary();
    }

    [Fact]
    public async Task FinalPackageValidationRejectsMixedElfArchitectures()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMixedElfArchitectures();
    }

    [Fact]
    public async Task FinalPackageValidationRejectsMissingAppImageEntrypoint()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingAppImageEntrypoint();
    }

    [Fact]
    public async Task FinalPackageValidationRejectsPackageManagerVersionMismatch()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerVersionMismatch();
    }

    [Fact]
    public async Task FinalPackageValidationRejectsPackageManagerIdentityMismatch()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerIdentityMismatch();
    }

    [Fact]
    public async Task FinalPackageValidationRejectsRpmEvrMismatch()
    {
        await V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsRpmEvrMismatch();
    }
}
