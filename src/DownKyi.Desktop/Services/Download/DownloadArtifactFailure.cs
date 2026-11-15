using System.Net;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

/// <summary>Stable failure codes shared by artifact operations and their presenter.</summary>
internal static class DownloadArtifactFailure
{
    internal const string SubtitleResponseReadFailureCode =
        "download.artifact.subtitle.response-read";
    internal const string SubtitleOutputWriteFailureCode =
        "download.artifact.subtitle.output-write";

    internal static OperationResult<DownloadArtifactWriteResult> ArtifactFailure(
        string code,
        string message)
    {
        return OperationResult.Failure<DownloadArtifactWriteResult>(
            OperationError.Unexpected(code, message));
    }

    internal static string GetArtifactHttpFailureCode(
        string artifact,
        HttpStatusCode? statusCode)
    {
        var suffix = statusCode switch
        {
            HttpStatusCode.Unauthorized => "http-401",
            HttpStatusCode.Forbidden => "http-403",
            HttpStatusCode.RequestTimeout => "http-408",
            HttpStatusCode.TooManyRequests => "http-429",
            >= HttpStatusCode.InternalServerError => "http-server-error",
            _ => "http"
        };
        return $"download.artifact.{artifact}.{suffix}";
    }
}
