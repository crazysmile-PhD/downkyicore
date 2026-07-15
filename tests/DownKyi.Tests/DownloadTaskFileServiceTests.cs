using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadTaskFileServiceTests : IDisposable
{
    private static readonly string[] GeneratedFileNames = { "video-stream.mp4", "audio-stream.aac" };
    private readonly DownloadTaskFileService _service = new(NullLogger<DownloadTaskFileService>.Instance);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-file-lifecycle-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetGeneratedFilesIncludesMediaAssetsAndResumeSidecars()
    {
        Directory.CreateDirectory(_directory);
        var basePath = Path.Combine(_directory, "episode-01");

        var files = _service.GetGeneratedFiles(
            basePath,
            GeneratedFileNames);

        Assert.Contains(Path.GetFullPath(Path.Combine(_directory, "video-stream.mp4.aria2")), files);
        Assert.Contains(Path.GetFullPath(Path.Combine(_directory, "audio-stream.aac.download")), files);
        Assert.Contains(Path.GetFullPath(basePath + ".mp4"), files);
        Assert.Contains(Path.GetFullPath(basePath + ".srt"), files);
        Assert.Contains(Path.GetFullPath(basePath + ".Cover.jpg"), files);
    }

    [Fact]
    public async Task DeleteFilesAsyncRemovesPartialFilesAndResumeSidecars()
    {
        Directory.CreateDirectory(_directory);
        var files = new[]
        {
            CreateFile("video.mp4", "partial video"),
            CreateFile("video.mp4.aria2", "resume metadata"),
            CreateFile("audio.aac.download", "partial audio")
        };

        await _service.DeleteFilesAsync(
            files,
            TestContext.Current.CancellationToken);

        Assert.All(files, file => Assert.False(File.Exists(file), file));
    }

    [Fact]
    public async Task DeleteFilesAsyncDoesNotDeleteWhenAlreadyCanceled()
    {
        Directory.CreateDirectory(_directory);
        var file = CreateFile("video.mp4.aria2", "resume metadata");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.DeleteFilesAsync(new[] { file }, cancellation.Token));

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void GetGeneratedFilesRejectsNullTask()
    {
        Assert.Throws<ArgumentNullException>(() => _service.GetGeneratedFiles(null!));
    }

    private string CreateFile(string name, string contents)
    {
        var file = Path.Combine(_directory, name);
        File.WriteAllText(file, contents);
        return file;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
