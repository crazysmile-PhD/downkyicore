using DownKyi.Images;

namespace DownKyi.Tests;

public sealed class NavigationIconTests
{
    [Fact]
    public void BackArrowInstancesDoNotShareMutableThemeState()
    {
        var first = NavigationIcon.CreateArrowBack();
        var second = NavigationIcon.CreateArrowBack();

        first.Fill = "white";

        Assert.NotSame(first, second);
        Assert.Equal("white", first.Fill);
        Assert.Equal("#FF000000", second.Fill);
    }
}
