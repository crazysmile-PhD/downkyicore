using System.Runtime.Versioning;
using DownKyi.Architecture.Tests;

namespace DownKyi.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class V115ReleasePackageTests
{
    [Fact]
    public void FinalPackageValidationRejectsMissingExecuteBits()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingExecuteBits();
    }

    [Fact]
    public void FinalPackageValidationRejectsArchitectureMismatch()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsArchitectureMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsOwnerOnlyExecuteBits()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsOwnerOnlyExecuteBits();
    }

    [Fact]
    public void FinalPackageValidationRejectsCrossFormatBinary()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsCrossFormatBinary();
    }

    [Fact]
    public void FinalPackageValidationRejectsMixedElfArchitectures()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMixedElfArchitectures();
    }

    [Fact]
    public void FinalPackageValidationRejectsMissingAppImageEntrypoint()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingAppImageEntrypoint();
    }

    [Fact]
    public void FinalPackageValidationRejectsPackageManagerVersionMismatch()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerVersionMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsPackageManagerIdentityMismatch()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerIdentityMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsRpmEvrMismatch()
    {
        V115ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsRpmEvrMismatch();
    }
}
