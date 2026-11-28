using System.Globalization;
using DownKyi.Core.BiliApi.Models.Json;

namespace DownKyi.Core.Tests;

public sealed class CultureInvariantContractTests
{
    private static readonly Subtitle[] SubtitleRows =
    {
        new() { From = 1.2345m, To = 2.3455m, Content = "caption" }
    };

    [Fact]
    public void ProtocolFormattingIgnoresCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");

            var subRip = new SubtitleJson { Body = SubtitleRows }.ToSubRip();
            var expectedSubtitle = string.Join(
                Environment.NewLine,
                "1",
                "00:00:01,235 --> 00:00:02,346",
                "caption");
            Assert.Contains(expectedSubtitle, subRip, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
