using DownKyi.Application.Bilibili;

namespace DownKyi.Application.Tests;

public sealed class BilibiliResourceAddressTests
{
    [Theory]
    [InlineData(
        "//i0.hdslb.com/bfs/archive/cover%2Fname.jpg?token=%2f%2B",
        "https://i0.hdslb.com/bfs/archive/cover%2Fname.jpg?token=%2f%2B")]
    [InlineData(
        "http://i0.hdslb.com/bfs/archive/cover%2Fname.jpg?token=%2f%2B",
        "https://i0.hdslb.com/bfs/archive/cover%2Fname.jpg?token=%2f%2B")]
    [InlineData(
        "https://i0.hdslb.com/bfs/archive/cover%2Fname.jpg?token=%2f%2B",
        "https://i0.hdslb.com/bfs/archive/cover%2Fname.jpg?token=%2f%2B")]
    public void SupportedAddressNormalizesToHttpsWithoutRewritingPathOrQuery(
        string address,
        string expected)
    {
        var result = BilibiliResourceAddress.TryNormalizeHttps(
            address,
            out var normalizedAddress);

        Assert.True(result);
        Assert.Equal(expected, normalizedAddress);
    }

    [Theory]
    [InlineData("file://server/share/cover.jpg")]
    [InlineData("ftp://i0.hdslb.com/cover.jpg")]
    [InlineData("data:image/png;base64,AA==")]
    [InlineData("i0.hdslb.com/cover.jpg")]
    public void UnsupportedAddressIsRejected(string address)
    {
        var result = BilibiliResourceAddress.TryNormalizeHttps(
            address,
            out var normalizedAddress);

        Assert.False(result);
        Assert.Empty(normalizedAddress);
    }
}
