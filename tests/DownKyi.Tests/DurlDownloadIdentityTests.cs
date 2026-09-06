using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task PlaybackStageDoesNotRewriteFrozenBasePathWhenDirectoryPreparationFails()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-playback-path-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var blockedDirectory = Path.Combine(directory, "blocked");
            await File.WriteAllTextAsync(
                blockedDirectory,
                "not a directory",
                TestContext.Current.CancellationToken);
            var frozenBasePath = Path.Combine(blockedDirectory, "frozen\\alias");
            var downloadBase = new DownloadBase
            {
                Id = "frozen-playback-path",
                FilePath = frozenBasePath
            };
            var downloading = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = downloadBase.Id,
                    DownloadBase = downloadBase
                },
                PlayUrl = new PlayUrl()
            };
            using var store = new SqliteDownloadTaskStore(
                new SqliteDownloadTaskStoreOptions(Path.Combine(directory, "download.db")),
                new SystemClock());
            using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
            using var settings = new TestSettingsStore();
            var stage = new ResolvePlaybackStage(
                new TestDesktopInteractionContext().Notifications,
                new DownloadActivityPresenter(new DownloadTaskStateWriter(tasks)),
                new DownloadPlaybackResolver(
                    new TestWbiKeyProvider(),
                    TimeProvider.System,
                    new TestBilibiliApiClient()),
                NullLogger<ResolvePlaybackStage>.Instance);
            var context = new DownloadExecutionContext(
                new DownloadTaskId(downloadBase.Id),
                downloading,
                settings.Store.Current,
                static (_, token) => token.ThrowIfCancellationRequested());

            var result = await stage.ExecuteAsync(
                context,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsSuccess);
            Assert.Equal(frozenBasePath, downloading.DownloadBase.FilePath, ignoreCase: false);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
