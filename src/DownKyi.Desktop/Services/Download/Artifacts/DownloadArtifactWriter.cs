using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;
using static DownKyi.Services.Download.DownloadArtifactFailure;

namespace DownKyi.Services.Download;

internal sealed class DownloadArtifactWriter
{
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly ILogger _logger;
    private readonly IBilibiliApiClient _client;

    private readonly DownloadDanmakuWriter _danmaku;
    private readonly DownloadSubtitleWriter _subtitle;
    private readonly DownloadMetadataWriter _metadata;

    public DownloadArtifactWriter(
        IWbiKeyProvider wbiKeyProvider,
        DownloadTaskStateWriter stateWriter,
        ILogger logger,
        IBilibiliApiClient client)
    {
        ArgumentNullException.ThrowIfNull(wbiKeyProvider);
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _danmaku = new DownloadDanmakuWriter(stateWriter, logger, client);
        _subtitle = new DownloadSubtitleWriter(wbiKeyProvider, stateWriter, logger, client);
        _metadata = new DownloadMetadataWriter(stateWriter, logger);
    }

    public async Task<OperationResult<DownloadArtifactWriteResult>> DownloadCoverAsync(
        DownloadTaskId taskId,
        string? coverUrl,
        string fileName,
        string transferKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferKey);

        try
        {
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                return OperationResult.Success(DownloadArtifactWriteResult.NotAvailable());
            }

            if (!BilibiliResourceAddress.TryNormalizeHttps(coverUrl, out var normalizedAddress))
            {
                return ArtifactFailure(
                    "download.artifact.cover.http",
                    "The requested cover URL is invalid.");
            }

            await _stateWriter.ClaimTransferFileAsync(
                taskId,
                transferKey,
                fileName,
                cancellationToken).ConfigureAwait(false);
            await _client.DownloadFileAsync(
                new BilibiliHttpRequest(normalizedAddress, includeCredentials: false),
                fileName,
                cancellationToken).ConfigureAwait(false);
            var integrity = DownloadFileIntegrity.Check(fileName);
            if (!integrity.IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.cover.invalid",
                    "The requested cover output is missing or invalid.");
            }

            return OperationResult.Success(DownloadArtifactWriteResult.Created(fileName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return OperationResult.Success(DownloadArtifactWriteResult.NotAvailable());
        }
        catch (HttpRequestException e)
        {
            _logger.LogErrorMessage("Cover download failed.", e);
            return ArtifactFailure(
                GetArtifactHttpFailureCode("cover", e.StatusCode),
                "The requested cover could not be downloaded.");
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("Cover download or write failed.", e);
            return ArtifactFailure(
                "download.artifact.cover.io",
                "The requested cover could not be written.");
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("Cover download was denied.", e);
            return ArtifactFailure(
                "download.artifact.cover.permission",
                "Permission was denied while writing the requested cover.");
        }
    }

    public Task<OperationResult<DownloadArtifactWriteResult>> DownloadDanmakuAsync(
        DownloadTaskId taskId, DownloadTaskMetadata metadata,
        IReadOnlyList<DownloadDanmakuOutputPath> outputs, DanmakuApplicationSettings settings,
        CancellationToken cancellationToken) =>
        _danmaku.DownloadDanmakuAsync(taskId, metadata, outputs, settings, cancellationToken);

    public Task<OperationResult<DownloadSubtitleWriteResult>> DownloadSubtitleAsync(
        DownloadTaskId taskId, DownloadTaskMetadata metadata, string outputBasePath,
        DownloadContentSelection content, CancellationToken cancellationToken) =>
        _subtitle.DownloadSubtitleAsync(taskId, metadata, outputBasePath, content, cancellationToken);

    public Task<OperationResult<DownloadArtifactWriteResult>> GenerateNfoFileAsync(
        DownloadTaskId taskId, string nfoFile, DownloadNfoRequest metadata,
        CancellationToken cancellationToken) =>
        _metadata.GenerateNfoFileAsync(taskId, nfoFile, metadata, cancellationToken);
}
