using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DurlDownloadIdentityTests
{
    private static readonly string[] BackupAddresses = { "https://backup.invalid/segment-7" };

    [Fact]
    public void DescriptorUsesDurlOrderAsStableDownloadKey()
    {
        var descriptor = DownloadMediaStage.CreateDurlDownloadDescriptor(new List<PlayUrlDurl>
        {
            new()
            {
                Order = 7,
                SourceAddress = "https://example.invalid/segment-7",
                BackupUrl = BackupAddresses,
                Size = 4096
            }
        });

        Assert.NotNull(descriptor);
        Assert.Equal(7, descriptor.Id);
        Assert.Equal("durl", descriptor.Codecs);
        Assert.Equal("7_durl", DownloadTransferKey.Create(descriptor.Id, descriptor.Codecs));
        Assert.Equal("https://example.invalid/segment-7", descriptor.BaseAddress);
        Assert.Equal(4096, descriptor.ExpectedSize);
    }

    [Fact]
    public void DescriptorSelectsLowestDurlOrder()
    {
        var descriptor = DownloadMediaStage.CreateDurlDownloadDescriptor(new List<PlayUrlDurl>
        {
            new() { Order = 9, SourceAddress = "https://example.invalid/segment-9" },
            new() { Order = 2, SourceAddress = "https://example.invalid/segment-2" },
            new() { Order = 5, SourceAddress = "https://example.invalid/segment-5" }
        });

        Assert.NotNull(descriptor);
        Assert.Equal(2, descriptor.Id);
        Assert.Equal("2_durl", DownloadTransferKey.Create(descriptor.Id, descriptor.Codecs));
        Assert.Equal("https://example.invalid/segment-2", descriptor.BaseAddress);
    }

    [Theory]
    [InlineData("https://i0.example.invalid/cover.jpg?token=redacted", "jpg")]
    [InlineData("//i0.example.invalid/cover.webp@672w_378h.webp?token=redacted", "webp")]
    [InlineData("images/cover.png#thumbnail", "png")]
    public void CoverExtensionIgnoresUriQueryAndFragment(string source, string expected)
    {
        Assert.Equal(expected, DownloadArtifactsStage.GetImageExtension(source));
    }

    [Fact]
    public void DownloadDirectoryUsesPathSemantics()
    {
        var filePath = Path.Combine("downloads", "nested", "video");

        Assert.Equal(
            Path.Combine("downloads", "nested"),
            ResolvePlaybackStage.GetDownloadDirectoryPath(filePath));
    }

    [Fact]
    public void PlaybackPathResolutionDoesNotRewriteFrozenBasePath()
    {
        var frozenBasePath = Path.Combine("downloads", "cafe\u0301", "video");
        var downloading = new DownloadingItem
        {
            DownloadBase = new DownloadBase { FilePath = frozenBasePath }
        };

        var directory = ResolvePlaybackStage.GetDownloadDirectoryPath(downloading);

        Assert.Equal(Path.GetDirectoryName(frozenBasePath), directory);
        Assert.Equal(frozenBasePath, downloading.DownloadBase.FilePath, ignoreCase: false);
    }
}
