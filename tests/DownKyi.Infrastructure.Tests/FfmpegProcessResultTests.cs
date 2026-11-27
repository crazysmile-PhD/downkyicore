using DownKyi.Application.Media;
using DownKyi.Infrastructure.FFmpeg;

namespace DownKyi.Infrastructure.Tests;

public sealed class FfmpegProcessResultTests
{
    [Theory]
    [InlineData("Error while opening encoder - maybe incorrect parameters", FfmpegOperationFailureKind.EncoderUnavailable)]
    [InlineData("Encoding hardware device setup failed: Generic error", FfmpegOperationFailureKind.EncoderUnavailable)]
    [InlineData("No space left on device", FfmpegOperationFailureKind.OutputIoFailure)]
    [InlineData("Disk quota exceeded", FfmpegOperationFailureKind.OutputIoFailure)]
    [InlineData("Read-only file system", FfmpegOperationFailureKind.OutputIoFailure)]
    [InlineData("Permission denied", FfmpegOperationFailureKind.DestinationAccessDenied)]
    [InlineData("Access is denied", FfmpegOperationFailureKind.DestinationAccessDenied)]
    [InlineData("Invalid data found when processing input\nError while opening encoder", FfmpegOperationFailureKind.InputInvalid)]
    [InlineData("Error while opening encoder\nNo space left on device", FfmpegOperationFailureKind.OutputIoFailure)]
    [InlineData("Error while opening encoder\nPermission denied", FfmpegOperationFailureKind.DestinationAccessDenied)]
    [InlineData("Unknown process failure", FfmpegOperationFailureKind.ProcessFailure)]
    public void FailureClassificationKeepsNonEncoderFailuresOutOfFallback(
        string diagnostic, FfmpegOperationFailureKind expected)
    {
        var result = new FfmpegProcessResult(false, 1, string.Empty, diagnostic, false);

        Assert.Equal(expected, result.ToOperationFailureKind());
    }
}
