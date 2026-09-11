using System.Runtime.Versioning;
using DownKyi.Architecture.Tests;

namespace DownKyi.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class V116ReleasePackageTests
{
    [Fact]
    public void FinalPackageValidationRejectsMissingExecuteBits()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingExecuteBits();
    }

    [Fact]
    public void FinalPackageValidationRejectsArchitectureMismatch()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsArchitectureMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsOwnerOnlyExecuteBits()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsOwnerOnlyExecuteBits();
    }

    [Fact]
    public void FinalPackageValidationRejectsCrossFormatBinary()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsCrossFormatBinary();
    }

    [Fact]
    public void FinalPackageValidationRejectsMixedElfArchitectures()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMixedElfArchitectures();
    }

    [Fact]
    public void FinalPackageValidationRejectsMissingAppImageEntrypoint()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsMissingAppImageEntrypoint();
    }

    [Fact]
    public void FinalPackageValidationRejectsPackageManagerVersionMismatch()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerVersionMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsPackageManagerIdentityMismatch()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsPackageManagerIdentityMismatch();
    }

    [Fact]
    public void FinalPackageValidationRejectsRpmEvrMismatch()
    {
        V116ReleaseSafetyRegressionTests
            .LinuxReleasePackageValidationRejectsRpmEvrMismatch();
    }
}
