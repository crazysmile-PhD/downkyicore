using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class DownloadFileIntegrityTests : IDisposable
{
    private readonly string _directory;

    public DownloadFileIntegrityTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"downkyi-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void IsUsable_RejectsEmptyFile()
    {
        var file = CreateFile("empty.mp4", Array.Empty<byte>());

        Assert.False(DownloadFileIntegrity.IsUsable(file));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>blocked</body></html>")]
    [InlineData("<html><body>blocked</body></html>")]
    [InlineData("{\"code\":-403,\"message\":\"forbidden\"}")]
    [InlineData("{\"error\":\"forbidden\"}")]
    public void IsUsable_RejectsErrorPayloads(string payload)
    {
        var file = CreateFile("error.mp4", payload);

        Assert.False(DownloadFileIntegrity.IsUsable(file));
    }

    [Theory]
    [InlineData(".aria2")]
    [InlineData(".download")]
    public void IsUsable_RejectsUnfinishedSidecars(string sidecarExtension)
    {
        var file = CreateFile("video.mp4", new byte[] { 0, 1, 2, 3 });
        File.WriteAllText($"{file}{sidecarExtension}", "unfinished");

        Assert.False(DownloadFileIntegrity.IsUsable(file));
    }

    [Fact]
    public void IsUsable_RejectsIncompleteExpectedLength()
    {
        var file = CreateFile("short.mp4", new byte[] { 0, 1, 2, 3 });

        Assert.False(DownloadFileIntegrity.IsUsable(file, expectedBytes: 8, receivedBytes: 4));
    }

    [Fact]
    public void IsUsable_AcceptsNonEmptyMediaLikeFile()
    {
        var file = CreateFile("video.mp4", new byte[] { 0, 1, 2, 3 });

        Assert.True(DownloadFileIntegrity.IsUsable(file, expectedBytes: 4, receivedBytes: 4));
    }

    private string CreateFile(string name, string content)
    {
        var file = Path.Combine(_directory, name);
        File.WriteAllText(file, content);
        return file;
    }

    private string CreateFile(string name, byte[] content)
    {
        var file = Path.Combine(_directory, name);
        File.WriteAllBytes(file, content);
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
