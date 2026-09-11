using System.Runtime.Versioning;
using DownKyi.Architecture.Tests;

namespace DownKyi.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class ReleasePackageTests
{
    [Fact]
    public void FinalPackageValidationRejectsMissingExecuteBits()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingExecuteBits();
    }

    [Fact]
    public void FinalPackageValidationRejectsArchitectureMismatch()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsArchitectureMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsOwnerOnlyExecuteBits()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsOwnerOnlyExecuteBits();
    }

    [Fact]
    public void FinalPackageValidationRejectsCrossFormatBinary()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsCrossFormatBinary();
    }

    [Fact]
    public void FinalPackageValidationRejectsMixedElfArchitectures()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMixedElfArchitectures();
    }

    [Fact]
    public void FinalPackageValidationRejectsMissingAppImageEntrypoint()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingAppImageEntrypoint();
    }

    [Fact]
    public void FinalPackageValidationRejectsPackageManagerVersionMismatch()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerVersionMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsPackageManagerIdentityMismatch()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerIdentityMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsRpmEvrMismatch()
    {
        ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsRpmEvrMismatch();
    }
}
