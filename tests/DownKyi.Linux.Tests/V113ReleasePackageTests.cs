using System.Runtime.Versioning;
using DownKyi.Architecture.Tests;

namespace DownKyi.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class V113ReleasePackageTests
{
    [Fact]
    public void FinalPackageValidationRejectsMissingExecuteBits()
    {
        V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingExecuteBits();
    }

    [Fact]
    public void FinalPackageValidationRejectsArchitectureMismatch()
    {
        V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsArchitectureMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsOwnerOnlyExecuteBits()
    {
        V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsOwnerOnlyExecuteBits();
    }

    [Fact]
    public void FinalPackageValidationRejectsPackageManagerVersionMismatch()
    {
        V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerVersionMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsPackageManagerIdentityMismatch()
    {
        V113ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerIdentityMismatch();
    }
}
