using System.Globalization;
using DownKyi.Application.FileName;
using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class DownloadDraftNamingTests
{
    [Fact]
    public void EveryMetadataFieldKeepsItsExistingText()
    {
        using var fixture = new Fixture();
        FileNamePart[] parts =
        [
            FileNamePart.Order, FileNamePart.Underscore, FileNamePart.Section, FileNamePart.Underscore,
            FileNamePart.MainTitle, FileNamePart.Underscore, FileNamePart.PageTitle, FileNamePart.Underscore,
            FileNamePart.VideoZone, FileNamePart.Underscore, FileNamePart.AudioQuality, FileNamePart.Underscore,
            FileNamePart.VideoQuality, FileNamePart.Underscore, FileNamePart.VideoCodec, FileNamePart.Underscore,
            FileNamePart.VideoPublishTime, FileNamePart.Underscore, FileNamePart.Avid, FileNamePart.Underscore,
            FileNamePart.Bvid, FileNamePart.Underscore, FileNamePart.Cid, FileNamePart.Underscore,
            FileNamePart.UpMid, FileNamePart.Underscore, FileNamePart.UpName
        ];

        var path = fixture.Create(parts, video: true);

        Assert.Equal(Path.Combine(fixture.Directory,
            "7_section_main_page_Technology_FLAC_1080P_AVC_2024-01-02_av170001_BV17x411w7KC_123456789_987654321_creator"), path);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-TW")]
    [InlineData("ar-SA")]
    public void NumericTemplateRemainsCultureInvariant(string culture)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var fixture = new Fixture();
            fixture.Section.VideoPages = [.. Enumerable.Repeat(fixture.Page, 100)];

            var path = fixture.Create([FileNamePart.Order, FileNamePart.Underscore, FileNamePart.Avid,
                FileNamePart.Underscore, FileNamePart.Cid, FileNamePart.Underscore, FileNamePart.UpMid], OrderFormat.LeadingZeros);

            Assert.Equal(Path.Combine(fixture.Directory, "007_av170001_123456789_987654321"), path);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData(FileNamePart.Slash, "main/page")]
    [InlineData(FileNamePart.Underscore, "main_page")]
    [InlineData(FileNamePart.Hyphen, "main-page")]
    [InlineData(FileNamePart.Plus, "main+page")]
    [InlineData(FileNamePart.Comma, "main,page")]
    [InlineData(FileNamePart.Period, "main.page")]
    [InlineData(FileNamePart.And, "main&page")]
    [InlineData(FileNamePart.Number, "main#page")]
    [InlineData(FileNamePart.OpenParen, "main(page")]
    [InlineData(FileNamePart.CloseParen, "main)page")]
    [InlineData(FileNamePart.OpenBracket, "main[page")]
    [InlineData(FileNamePart.CloseBracket, "main]page")]
    [InlineData(FileNamePart.OpenBrace, "main{page")]
    [InlineData(FileNamePart.CloseBrace, "main}page")]
    [InlineData(FileNamePart.Blank, "main page")]
    public void SavedSeparatorValuesKeepTheirLiteralOutput(FileNamePart separator, string expected)
    {
        using var fixture = new Fixture();

        var path = fixture.Create([FileNamePart.MainTitle, separator, FileNamePart.PageTitle]);

        Assert.Equal(Path.Combine(fixture.Directory, expected), path);
    }

    [Theory]
    [InlineData(OrderFormat.NotSet, "ORDER")]
    [InlineData(OrderFormat.Natural, "7")]
    [InlineData(OrderFormat.LeadingZeros, "007")]
    public void OrderFormatKeepsLegacyPlaceholderAndWidth(OrderFormat format, string expected)
    {
        using var fixture = new Fixture();
        fixture.Section.VideoPages = [.. Enumerable.Repeat(fixture.Page, 100)];

        Assert.Equal(Path.Combine(fixture.Directory, expected), fixture.Create([FileNamePart.Order], format));
    }

    [Theory]
    [InlineData(1, "main/page")]
    [InlineData(2, "main/section/page")]
    public void EmptySectionsAndDuplicateSlashesKeepTheSameRelativePath(int sectionCount, string expected)
    {
        using var fixture = new Fixture();

        var path = fixture.Create([FileNamePart.Slash, FileNamePart.Slash, FileNamePart.MainTitle, FileNamePart.Slash,
            FileNamePart.Section, FileNamePart.Slash, FileNamePart.Slash, FileNamePart.PageTitle, FileNamePart.Slash],
            sectionCount: sectionCount);

        Assert.Equal(Path.Combine(fixture.Directory, expected), path);
    }

    [Fact]
    public void SelectedNamesKeepExistingCharacterCleanupAndMultiplicity()
    {
        using var fixture = new Fixture();
        fixture.Video.Title = " .ma/in\0. ";
        fixture.Page.Name = " .pa/ge\0. ";

        var path = fixture.Create([FileNamePart.MainTitle, FileNamePart.Plus, FileNamePart.MainTitle,
            FileNamePart.Slash, FileNamePart.PageTitle, FileNamePart.Plus, FileNamePart.PageTitle]);

        Assert.Equal(Path.Combine(fixture.Directory, "main+main/page+page"), path);
    }

    [Theory]
    [InlineData(false, "FLAC__")]
    [InlineData(true, "FLAC_1080P_AVC")]
    public void VideoLabelsOnlyUseTheFinalizedVideoSelection(bool video, string expected)
    {
        using var fixture = new Fixture();

        var path = fixture.Create([FileNamePart.AudioQuality, FileNamePart.Underscore,
            FileNamePart.VideoQuality, FileNamePart.Underscore, FileNamePart.VideoCodec], video: video);

        Assert.Equal(Path.Combine(fixture.Directory, expected), path);
    }

    [Fact]
    public void UnicodeNamesRemainUnchanged()
    {
        using var fixture = new Fixture();
        fixture.Video.Title = "主標題🎵";
        fixture.Page.Name = "分集🎬";

        Assert.Equal(Path.Combine(fixture.Directory, "主標題🎵/分集🎬"),
            fixture.Create([FileNamePart.MainTitle, FileNamePart.Slash, FileNamePart.PageTitle]));
    }

    [Fact]
    public void MissingOwnerKeepsLegacyFallbacks()
    {
        using var fixture = new Fixture();
        fixture.Page.Owner = null;

        Assert.Equal(Path.Combine(fixture.Directory, "-1_"),
            fixture.Create([FileNamePart.UpMid, FileNamePart.Underscore, FileNamePart.UpName]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyOrPunctuationOnlyOutputFallsBackToCid(bool emptyTemplate)
    {
        using var fixture = new Fixture();
        fixture.Page.Name = " .:/<>*?\0 ";
        FileNamePart[] parts = emptyTemplate ? [] : [FileNamePart.Slash, FileNamePart.PageTitle, FileNamePart.Slash];

        Assert.Equal(Path.Combine(fixture.Directory, "123456789"), fixture.Create(parts));
    }

    [Fact]
    public void UnknownMetadataValueRemainsIgnoredAndUnknownSeparatorStillFails()
    {
        using var fixture = new Fixture();

        Assert.Equal(Path.Combine(fixture.Directory, "123456789"), fixture.Create([FileNamePart.None, (FileNamePart)50]));
        Assert.Throws<KeyNotFoundException>(() => fixture.Create([(FileNamePart)999]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestSettingsStore _settings = new();
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "downkyi-naming-contract", Guid.NewGuid().ToString("N"));
        public VideoInfoView Video { get; } = new() { Title = "main", VideoZone = "Technology>Software" };
        public VideoPage Page { get; } = new()
        {
            Name = "page",
            Order = 7,
            Avid = 170001,
            Bvid = "BV17x411w7KC",
            Cid = 123456789,
            PublishTime = "2024-01-02",
            Owner = new() { Mid = 987654321, Name = "creator" }
        };
        public VideoSection Section { get; }

        public Fixture() => Section = new VideoSection { Title = "section", VideoPages = [Page] };

        public string Create(IReadOnlyList<FileNamePart> parts, OrderFormat format = OrderFormat.Natural,
            int sectionCount = 2, bool video = false)
        {
            var current = _settings.Store.Current;
            var settings = current with { Video = current.Video with { FileNameParts = [.. parts], OrderFormat = format } };
            var quality = new VideoQuality { Quality = 80, QualityFormat = "1080P", SelectedVideoCodec = "AVC High" };
            var content = video ? DownloadContentSelection.None with { Video = true, Audio = true }
                : DownloadContentSelection.None with { Cover = true };
            return DownloadTaskDraftFactory.Create(Directory, Video, Section, sectionCount, Page, quality, "FLAC",
                new DownloadMediaCapabilities(DownloadMediaOutputModes.AudioVideo), settings, content).DownloadBase.FilePath;
        }

        public void Dispose() => _settings.Dispose();
    }
}
