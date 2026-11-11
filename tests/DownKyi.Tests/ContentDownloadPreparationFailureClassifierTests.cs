using System.IO;
using System.Net.Http;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using Newtonsoft.Json;

namespace DownKyi.Tests;

public sealed class ContentDownloadPreparationFailureClassifierTests
{
    public static TheoryData<Exception, int, int?> KnownFailures => new()
    {
        {
            ApiFailure(-101),
            (int)DownloadUnavailableReason.AccountRequired,
            -101
        },
        {
            ApiFailure(-404),
            (int)DownloadUnavailableReason.AccessRestrictedOrUnavailable,
            -404
        },
        {
            ApiFailure(62002),
            (int)DownloadUnavailableReason.VideoUnavailable,
            62002
        },
        {
            ApiFailure(12345),
            (int)DownloadUnavailableReason.ApiRejected,
            12345
        },
        {
            ApiFailure(code: null),
            (int)DownloadUnavailableReason.Unknown,
            null
        },
        {
            new PlaybackUnavailableException("No matching playback resource."),
            (int)DownloadUnavailableReason.NoPlaybackResource,
            null
        },
        {
            new HttpRequestException("Network failed."),
            (int)DownloadUnavailableReason.NetworkFailure,
            null
        },
        {
            new IOException("Response stream failed."),
            (int)DownloadUnavailableReason.NetworkFailure,
            null
        },
        {
            new OperationCanceledException("Request timed out."),
            (int)DownloadUnavailableReason.RequestTimedOut,
            null
        },
        {
            new JsonReaderException("Invalid JSON."),
            (int)DownloadUnavailableReason.InvalidResponse,
            null
        },
        {
            new System.Text.Json.JsonException("Invalid JSON."),
            (int)DownloadUnavailableReason.InvalidResponse,
            null
        },
        {
            new FormatException("Invalid response value."),
            (int)DownloadUnavailableReason.InvalidResponse,
            null
        }
    };

    [Theory]
    [MemberData(nameof(KnownFailures))]
    public void KnownOperationalFailureReturnsDisplayClassification(
        Exception exception,
        int expectedReason,
        int? expectedApiCode)
    {
        var classified = ContentDownloadPreparationFailureClassifier.TryClassify(
            exception,
            CancellationToken.None,
            out var failure);

        Assert.True(classified);
        Assert.Equal((DownloadUnavailableReason)expectedReason, failure.Reason);
        Assert.Equal(expectedApiCode, failure.ApiCode);
    }

    [Fact]
    public void CallerCancellationIsNotConvertedIntoSkippableFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var classified = ContentDownloadPreparationFailureClassifier.TryClassify(
            new OperationCanceledException(cancellation.Token),
            cancellation.Token,
            out _);

        Assert.False(classified);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(ArgumentException))]
    public void ProgrammerFailuresAreNotConvertedIntoSkippableFailures(Type exceptionType)
    {
        var exception = Assert.IsAssignableFrom<Exception>(
            Activator.CreateInstance(exceptionType));

        var classified = ContentDownloadPreparationFailureClassifier.TryClassify(
            exception,
            CancellationToken.None,
            out _);

        Assert.False(classified);
    }

    private static BilibiliApiResponseException ApiFailure(int? code) => new(
        "VideoViewInfoAsync",
        "Bilibili rejected the request.",
        code: code);
}
