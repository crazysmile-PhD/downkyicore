using DownKyi.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class AvaloniaPlatformLauncherTests
{
    [Fact]
    public async Task RelativeUriFailsGracefullyWithoutStartingAProcess()
    {
        var launcher = new AvaloniaPlatformLauncher(
            new AvaloniaDesktopContext(),
            NullLogger<AvaloniaPlatformLauncher>.Instance);

        var opened = await launcher.OpenUriAsync(
            new Uri("//example.test/path", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.False(opened);
    }

    [Fact]
    public async Task PreCanceledUriLaunchPreservesCancellation()
    {
        var launcher = new AvaloniaPlatformLauncher(
            new AvaloniaDesktopContext(),
            NullLogger<AvaloniaPlatformLauncher>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            launcher.OpenUriAsync(new Uri("https://example.test"), cancellation.Token));
    }
}
