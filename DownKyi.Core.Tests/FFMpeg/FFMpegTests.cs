using FFMpegHelper = DownKyi.Core.FFMpeg.FFMpeg;

namespace DownKyi.Core.Tests.FFMpeg;

public class FFMpegTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"downkyi-ffmpeg-tests-{Guid.NewGuid():N}");

    public FFMpegTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void IsValidOutputFile_ReturnsFalse_WhenOutputIsMissing()
    {
        var output = Path.Combine(_tempDirectory, "missing.mp4");

        var result = FFMpegHelper.IsValidOutputFile(output);

        Assert.False(result);
    }

    [Fact]
    public void IsValidOutputFile_ReturnsFalse_WhenOutputIsEmpty()
    {
        var output = Path.Combine(_tempDirectory, "empty.mp4");
        File.WriteAllBytes(output, Array.Empty<byte>());

        var result = FFMpegHelper.IsValidOutputFile(output);

        Assert.False(result);
    }

    [Fact]
    public void IsValidOutputFile_ReturnsTrue_WhenOutputIsNonEmpty()
    {
        var output = Path.Combine(_tempDirectory, "valid.mp4");
        File.WriteAllBytes(output, new byte[] { 1 });

        var result = FFMpegHelper.IsValidOutputFile(output);

        Assert.True(result);
    }
}
