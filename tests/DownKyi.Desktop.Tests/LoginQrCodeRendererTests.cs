using Avalonia.Headless.XUnit;
using DownKyi.Services.Account;

namespace DownKyi.Desktop.Tests;

public sealed class LoginQrCodeRendererTests
{
    [AvaloniaFact]
    public async Task RendererCreatesAUsableBitmapForAnAbsoluteLoginUri()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            using var bitmap = new LoginQrCodeRenderer().Render(
                new Uri("https://passport.bilibili.com/login?test=contract"));

            Assert.True(bitmap.PixelSize.Width > 0);
            Assert.True(bitmap.PixelSize.Height > 0);
            Assert.Equal(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        }).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task RendererRejectsRelativeUris()
    {
        await AvaloniaTestDispatcher.RunAsync(() =>
        {
            var renderer = new LoginQrCodeRenderer();

            Assert.Throws<ArgumentException>(() =>
                renderer.Render(new Uri("/relative", UriKind.Relative)));
        }).ConfigureAwait(true);
    }
}
