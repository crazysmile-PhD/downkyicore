using System.Globalization;
using System.Xml.Linq;
using DownKyi.Core.BiliApi.DanmakuApi;
using DownKyi.Core.BiliApi.DanmakuApi.Models;

namespace DownKyi.Core.Tests;

public sealed class BilibiliDanmakuXmlWriterTests
{
    [Fact]
    public void WritesBilibiliEightFieldMetadataAndRoundTripsContent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var output = BilibiliDanmakuXmlWriter.CreateDocument(
                [
                    new BiliDanmaku
                    {
                        Id = 987654321,
                        Progress = 1_234,
                        Mode = 7,
                        Fontsize = 30,
                        Color = 16_711_680,
                        Ctime = 1_700_000_000,
                        Pool = 1,
                        MidHash = "abc123ef",
                        Weight = 99,
                        Content = "中文\n<>&\"'"
                    }
                ],
                TestContext.Current.CancellationToken);
            Assert.Empty(output.InvalidCharacters);
            var document = XDocument.Parse(
                output.Document.ToString(SaveOptions.DisableFormatting),
                LoadOptions.PreserveWhitespace);
            Assert.Equal("i", document.Root?.Name.LocalName);
            var element = Assert.Single(document.Root!.Elements("d"));
            Assert.Equal("中文\n<>&\"'", element.Value);
            Assert.Equal(
                "1.234,7,30,16711680,1700000000,1,abc123ef,987654321",
                element.Attribute("p")?.Value);
            Assert.Equal(8, element.Attribute("p")!.Value.Split(',').Length);
            Assert.DoesNotContain(",99,", element.Attribute("p")!.Value, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void SkipsOnlyDanmakusThatXml10CannotRepresent()
    {
        var output = BilibiliDanmakuXmlWriter.CreateDocument(
            [
                new BiliDanmaku
                {
                    Id = 41,
                    Progress = 1_234,
                    Mode = 7,
                    Fontsize = 25,
                    Color = 0xFFFFFF,
                    Ctime = 1_700_000_000,
                    Pool = 0,
                    MidHash = "before",
                    Content = "保留\t\n😂<>&"
                },
                new BiliDanmaku
                {
                    Id = 42,
                    MidHash = "invalid-content",
                    Content = "前\0中\v後\uFFFF"
                },
                new BiliDanmaku
                {
                    Id = 43,
                    MidHash = "invalid\0sender",
                    Content = "metadata cannot be serialized"
                },
                new BiliDanmaku
                {
                    Id = 44,
                    Progress = 5_678,
                    Mode = 5,
                    Fontsize = 30,
                    Color = 0xFF0000,
                    Ctime = 1_700_000_001,
                    Pool = 1,
                    MidHash = "after",
                    Content = "後一條仍保留"
                }
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(4, output.InvalidCharacters.Count);
        var nullCharacter = output.InvalidCharacters[0];
        Assert.Equal(42, nullCharacter.Id);
        Assert.Equal("content", nullCharacter.Field);
        Assert.Equal(1, nullCharacter.Utf16Index);
        Assert.Equal(0, nullCharacter.InvalidValue);
        Assert.Equal(UnicodeCategory.Control, nullCharacter.Category);
        Assert.Equal("前", nullCharacter.ContextBefore);
        Assert.Equal("中\v後\uFFFF", nullCharacter.ContextAfter);

        var verticalTab = output.InvalidCharacters[1];
        Assert.Equal(0x000B, verticalTab.InvalidValue);
        Assert.Equal(3, verticalTab.Utf16Index);
        Assert.Equal("前\0中", verticalTab.ContextBefore);
        Assert.Equal("後\uFFFF", verticalTab.ContextAfter);

        var senderHash = output.InvalidCharacters[3];
        Assert.Equal(43, senderHash.Id);
        Assert.Equal("senderHash", senderHash.Field);
        Assert.Equal(0, senderHash.InvalidValue);
        Assert.Null(senderHash.ContextBefore);
        Assert.Null(senderHash.ContextAfter);
        var document = XDocument.Parse(
            output.Document.ToString(SaveOptions.DisableFormatting),
            LoadOptions.PreserveWhitespace);
        var elements = document.Root!.Elements("d").ToArray();
        Assert.Equal(2, elements.Length);
        Assert.Equal("保留\t\n😂<>&", elements[0].Value);
        Assert.Equal("1.234,7,25,16777215,1700000000,0,before,41", elements[0].Attribute("p")?.Value);
        Assert.Equal("後一條仍保留", elements[1].Value);
        Assert.Equal("5.678,5,30,16711680,1700000001,1,after,44", elements[1].Attribute("p")?.Value);
    }

    [Fact]
    public async Task CancellationIsVisibleBeforeXmlGeneration()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            BilibiliDanmakuXmlWriter.CreateDocument([], cancellation.Token));
    }
}
