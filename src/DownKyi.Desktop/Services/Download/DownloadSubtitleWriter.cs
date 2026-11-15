using System.Net.Http;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;
using static DownKyi.Services.Download.DownloadArtifactFailure;

using static DownKyi.Services.Download.DownloadArtifactIdentity;

namespace DownKyi.Services.Download;

internal sealed class DownloadSubtitleWriter(IWbiKeyProvider wbiKeyProvider, DownloadTaskStateWriter stateWriter,
    ILogger logger, IBilibiliApiClient client)
{
    public async Task<OperationResult<DownloadSubtitleWriteResult>> DownloadSubtitleAsync(
        DownloadTaskId taskId,
        DownloadTaskMetadata metadata,
        string outputBasePath,
        DownloadContentSelection content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        ArgumentNullException.ThrowIfNull(content);

        var trackFiles = new Dictionary<long, string>();
        string? defaultSubtitleSource = null;
        Exception? parseFailure = null;
        SubtitleFetchResult fetchResult;
        try
        {
            fetchResult = await WbiRequestExecutor.ExecuteAsync(
                wbiKeyProvider,
                (keys, unixTimeSeconds) => client.GetSubtitleSelectionAsync(
                    keys,
                    unixTimeSeconds,
                    metadata.Media.Avid,
                    metadata.Media.Bvid,
                    metadata.Media.Cid,
                    e => parseFailure ??= e,
                    cancellationToken,
                    content.SelectedSubtitleTrackIds is { } selectedIds ? selectedIds : null,
                    new SubtitleTrackPreference(
                        content.PreferredSubtitleLanguage,
                        content.PreferredSubtitleType)),
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            logger.LogErrorMessage("Subtitle download failed.", e);
            return SubtitleArtifactFailure(
                GetArtifactHttpFailureCode("subtitle", e.StatusCode),
                "The requested subtitles could not be downloaded.");
        }
        catch (IOException e)
        {
            logger.LogErrorMessage("Subtitle response could not be read.", e);
            return SubtitleArtifactFailure(
                SubtitleResponseReadFailureCode,
                "The requested subtitle response could not be read.");
        }

        if (parseFailure != null)
        {
            logger.LogErrorMessage("Subtitle response parsing failed.", parseFailure);
            return SubtitleArtifactFailure(
                "download.artifact.subtitle.parse",
                "The requested subtitle response was invalid.");
        }

        var subRipTexts = fetchResult.Subtitles;
        var returnedTrackIds = subRipTexts.Select(static subtitle => subtitle.TrackId).ToHashSet();
        if (returnedTrackIds.Count != subRipTexts.Count)
        {
            return SubtitleArtifactFailure(
                "download.artifact.subtitle.duplicate-track",
                "The subtitle response contained duplicate track identifiers.");
        }

        var hasMissingSelectedTrack = !returnedTrackIds.SetEquals(fetchResult.ExpectedTrackIds);
        var hasMissingDefaultTrack = content.DefaultSubtitleTrackId is { } defaultTrackId
                                     && !returnedTrackIds.Contains(defaultTrackId);
        if (hasMissingSelectedTrack || hasMissingDefaultTrack)
        {
            logger.LogWarningMessage("Not all selected subtitle tracks were returned for the download task.");
            return SubtitleArtifactFailure(
                "download.artifact.subtitle.missing-tracks",
                "One or more selected subtitle tracks were not available.");
        }

        if (subRipTexts.Count == 0)
        {
            logger.LogWarningMessage("No usable subtitles were returned for the download task.");
            return OperationResult.Success(DownloadSubtitleWriteResult.NotAvailable());
        }

        var duplicateLanguageDescriptions = subRipTexts
            .GroupBy(subtitle => subtitle.LanDoc, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputFiles = subRipTexts.ToDictionary(
            subtitle => subtitle.TrackId,
            subtitle =>
            {
                var fileLabel = duplicateLanguageDescriptions.Contains(subtitle.LanDoc)
                    ? $"{subtitle.LanDoc}_{subtitle.TrackId}"
                    : subtitle.LanDoc;
                return DownloadOutputLayout.GetSubtitleTrackPath(outputBasePath, fileLabel);
            });
        if (outputFiles.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputFiles.Count)
        {
            return SubtitleArtifactFailure(
                "download.artifact.subtitle.duplicate-output",
                "Selected subtitle tracks would overwrite the same output file.");
        }

        foreach (var subRip in subRipTexts)
        {
            var srtFile = outputFiles[subRip.TrackId];
            try
            {
                await stateWriter.ClaimTransferFileAsync(
                    taskId,
                    GetSubtitleTrackTransferKey(subRip.TrackId),
                    srtFile,
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(srtFile, subRip.SrtString, cancellationToken).ConfigureAwait(false);
                var integrity = DownloadFileIntegrity.Check(srtFile);
                if (!integrity.IsUsable)
                {
                    return SubtitleArtifactFailure(
                        "download.artifact.subtitle.invalid",
                        "A requested subtitle output is missing or invalid.");
                }

                trackFiles.Add(subRip.TrackId, srtFile);
                if (subRip.TrackId == content.DefaultSubtitleTrackId)
                {
                    defaultSubtitleSource = srtFile;
                }
            }
            catch (IOException e)
            {
                logger.LogErrorMessage("Subtitle download failed.", e);
                return SubtitleArtifactFailure(
                    SubtitleOutputWriteFailureCode,
                    "A requested subtitle could not be written.");
            }
            catch (UnauthorizedAccessException e)
            {
                logger.LogErrorMessage("Subtitle download was denied.", e);
                return SubtitleArtifactFailure(
                    "download.artifact.subtitle.permission",
                    "Permission was denied while writing a requested subtitle.");
            }
        }

        var defaultSubtitleSourceToWrite = defaultSubtitleSource ??
                                           (content.SelectedSubtitleTrackIds == null
                                               ? trackFiles.Values.First()
                                               : null);
        string? defaultSubtitleFile = null;
        if (defaultSubtitleSourceToWrite != null)
        {
            defaultSubtitleFile = DownloadOutputLayout.GetDefaultSubtitlePath(outputBasePath);
            try
            {
                await stateWriter.ClaimTransferFileAsync(
                    taskId,
                    DefaultSubtitleTransferKey,
                    defaultSubtitleFile,
                    cancellationToken).ConfigureAwait(false);
                File.Copy(defaultSubtitleSourceToWrite, defaultSubtitleFile, true);
                if (!DownloadFileIntegrity.Check(defaultSubtitleFile).IsUsable)
                {
                    return SubtitleArtifactFailure(
                        "download.artifact.subtitle.invalid",
                        "The default subtitle output is missing or invalid.");
                }
            }
            catch (IOException e)
            {
                logger.LogErrorMessage("Default subtitle write failed.", e);
                return SubtitleArtifactFailure(
                    SubtitleOutputWriteFailureCode,
                    "The default subtitle could not be written.");
            }
            catch (UnauthorizedAccessException e)
            {
                logger.LogErrorMessage("Default subtitle write was denied.", e);
                return SubtitleArtifactFailure(
                    "download.artifact.subtitle.permission",
                    "Permission was denied while writing the default subtitle.");
            }
        }

        return OperationResult.Success(DownloadSubtitleWriteResult.Created(
            trackFiles,
            defaultSubtitleFile));
    }

    private static OperationResult<DownloadSubtitleWriteResult> SubtitleArtifactFailure(
        string code,
        string message)
    {
        return OperationResult.Failure<DownloadSubtitleWriteResult>(
            OperationError.Unexpected(code, message));
    }
}
