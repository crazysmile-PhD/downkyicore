using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class AriaRuntimeClientRegistryTests
{
    [Fact]
    public void RegistryOwnsOneRuntimeClientAndReleasesItDeterministically()
    {
        var registry = new AriaRuntimeClientRegistry();
        var first = new AriaClient();
        var second = new AriaClient();

        using (registry.Activate(first))
        {
            Assert.Same(first, registry.Current);
            Assert.Throws<InvalidOperationException>(() => registry.Activate(second));
        }

        Assert.Null(registry.Current);
        using (registry.Activate(second))
        {
            Assert.Same(second, registry.Current);
        }

        Assert.Null(registry.Current);
    }
}
