using DownKyi.Core.Versioning;

namespace DownKyi.Core.Tests;

public sealed class SemanticVersionPolicyTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("1.2.3-beta.1+build.9", "1.2.3-beta.1")]
    public void DisplayNormalizationPreservesPrereleaseAndHidesBuildMetadata(
        string input,
        string expected)
    {
        Assert.Equal(expected, SemanticVersionPolicy.NormalizeForDisplay(input));
    }

    [Theory]
    [InlineData("1.2", false, "")]
    [InlineData("1.02.3", false, "")]
    [InlineData("not-a-version", false, "")]
    [InlineData("v1.2.3-rc.1+sha.abc", true, "1.2.3-rc.1+sha.abc")]
    public void IdentityNormalizationRequiresSemverAndPreservesBuildMetadata(
        string input,
        bool expectedSuccess,
        string expected)
    {
        var success = SemanticVersionPolicy.TryNormalizeIdentity(input, out var normalized);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha", true)]
    [InlineData("1.0.0-beta", "1.0.0-alpha.9", true)]
    [InlineData("1.0.0", "1.0.0-rc.1", true)]
    [InlineData("1.0.0-rc.1", "1.0.0", false)]
    [InlineData("1.0.0+new", "1.0.0+old", false)]
    public void NewerComparisonFollowsSemverPrecedence(
        string candidate,
        string current,
        bool expected)
    {
        Assert.Equal(expected, SemanticVersionPolicy.IsNewer(candidate, current));
    }

    [Fact]
    public void SkipEquivalenceIgnoresBuildMetadataButNotPrerelease()
    {
        Assert.True(SemanticVersionPolicy.HasSamePrecedence(
            "v1.2.3-beta.1+build.1",
            "1.2.3-beta.1+build.2"));
        Assert.False(SemanticVersionPolicy.HasSamePrecedence(
            "1.2.3-beta.1",
            "1.2.3"));
    }
}
