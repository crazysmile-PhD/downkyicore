using System.Globalization;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.FileName;

namespace DownKyi.Core.Tests;

public sealed class CultureInvariantContractTests
{
    private static readonly Subtitle[] SubtitleRows =
    {
        new() { From = 1.2345m, To = 2.3455m, Content = "caption" }
    };

    private static readonly FileNamePart[] FileNameParts =
    {
        FileNamePart.Order,
        FileNamePart.Underscore,
        FileNamePart.Avid,
        FileNamePart.Underscore,
        FileNamePart.Cid,
        FileNamePart.Underscore,
        FileNamePart.UpMid
    };

    [Fact]
    public void ProtocolAndFileNameFormattingIgnoreCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");

            var subRip = new SubtitleJson { Body = SubtitleRows }.ToSubRip();
            var fileName = FileNameBuilder.Create(FileNameParts)
                .SetOrder(7, 100)
                .SetAvid(170001)
                .SetCid(123456789)
                .SetUpMid(987654321)
                .RelativePath();

            var expectedSubtitle = string.Join(
                Environment.NewLine,
                "1",
                "00:00:01,235 --> 00:00:02,346",
                "caption");
            Assert.Contains(expectedSubtitle, subRip, StringComparison.Ordinal);
            Assert.Equal("007_av170001_123456789_987654321", fileName);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
