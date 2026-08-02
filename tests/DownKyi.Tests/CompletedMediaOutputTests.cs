using DownKyi.Models;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class CompletedMediaOutputTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-completed-media-output-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("downloadDanmaku", ".ass")]
    [InlineData("downloadSubtitle", ".srt")]
    [InlineData("downloadCover", ".Cover.jpg")]
    public void RequestedAuxiliaryOutputMustExist(string contentName, string extension)
    {
        Directory.CreateDirectory(_directory);
        var downloadBase = CreateDownloadBase(contentName);

        Assert.False(CompletedMediaOutput.Exists(downloadBase));

        File.WriteAllText(downloadBase.FilePath + extension, "content");

        Assert.True(CompletedMediaOutput.Exists(downloadBase));
    }

    [Fact]
    public void LanguageSpecificSubtitleSatisfiesRequestedOutput()
    {
        Directory.CreateDirectory(_directory);
        var downloadBase = CreateDownloadBase("downloadSubtitle");
        File.WriteAllText(downloadBase.FilePath + "_zh-CN.srt", "subtitle");

        Assert.True(CompletedMediaOutput.Exists(downloadBase));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private DownloadBase CreateDownloadBase(string contentName)
    {
        var downloadBase = new DownloadBase
        {
            FilePath = Path.Combine(_directory, "completed-output")
        };
        foreach (var key in downloadBase.NeedDownloadContent.Keys.ToArray())
        {
            downloadBase.NeedDownloadContent[key] = false;
        }

        downloadBase.NeedDownloadContent[contentName] = true;
        return downloadBase;
    }
}
