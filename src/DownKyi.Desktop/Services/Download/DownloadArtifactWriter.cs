using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Danmaku2Ass;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Models;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed partial class DownloadArtifactWriter
{
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly ILogger _logger;
    private readonly IBilibiliApiClient _client;
    private readonly IAtomicOutputPublisher _outputPublisher;
    private readonly DownloadOutputArtifactProvenanceRecorder? _provenanceRecorder;

    public DownloadArtifactWriter(
        IWbiKeyProvider wbiKeyProvider,
        DownloadTaskStateWriter stateWriter,
        ILogger logger,
        IBilibiliApiClient client,
        IAtomicOutputPublisher? outputPublisher = null,
        DownloadOutputArtifactProvenanceRecorder? provenanceRecorder = null)
    {
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _outputPublisher = outputPublisher ?? new AtomicOutputPublisher();
        _provenanceRecorder = provenanceRecorder;
    }

    public async Task<OperationResult<DownloadArtifactWriteResult>> DownloadCoverAsync(
        DownloadingItem downloading,
        string? coverUrl,
        string fileName,
        string transferKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferKey);
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingCover");
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;
        var taskId = new DownloadTaskId(downloading.DownloadBase.Id);
        await _stateWriter.UpdateActivityAsync(
            taskId,
            downloading.DownloadContent,
            downloading.DownloadStatusTitle,
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                return OperationResult.Success(DownloadArtifactWriteResult.NotAvailable());
            }

            await _stateWriter.ClaimTransferFileAsync(
                taskId,
                transferKey,
                fileName,
                cancellationToken).ConfigureAwait(false);
            var publish = await PublishCoverAsync(fileName, coverUrl, cancellationToken).ConfigureAwait(false);
            if (publish.IsDestinationCollision)
            {
                return OutputCollisionFailure();
            }
            var integrity = DownloadFileIntegrity.Check(fileName);
            if (!integrity.IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.cover.invalid",
                    "The requested cover output is missing or invalid.");
            }

            await RecordPublishedArtifactAsync(
                taskId,
                transferKey,
                GetCoverArtifactKind(transferKey),
                fileName,
                publish).ConfigureAwait(false);
            return OperationResult.Success(DownloadArtifactWriteResult.Created(fileName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            _logger.LogErrorMessage("Cover download failed.", e);
            return ArtifactFailure(
                "download.artifact.cover.http",
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

    public async Task<OperationResult<DownloadArtifactWriteResult>> DownloadDanmakuAsync(
        DownloadingItem downloading,
        DanmakuApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        ArgumentNullException.ThrowIfNull(settings);
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingDanmaku");
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;
        var taskId = new DownloadTaskId(downloading.DownloadBase.Id);
        await _stateWriter.UpdateActivityAsync(
            taskId,
            downloading.DownloadContent,
            downloading.DownloadStatusTitle,
            cancellationToken).ConfigureAwait(false);

        var assFile = $"{downloading.DownloadBase?.FilePath}.ass";
        var subtitleConfig = new Config
        {
            Title = downloading.Name,
            ScreenWidth = settings.ScreenWidth,
            ScreenHeight = settings.ScreenHeight,
            FontName = settings.FontName,
            BaseFontSize = settings.FontSize,
            LineCount = settings.LineCount,
            LayoutAlgorithm = GetDanmakuLayoutAlgorithmValue(settings.LayoutAlgorithm),
            TuneDuration = 0,
            DropOffset = 0,
            BottomMargin = 0,
            CustomOffset = 0
        };

        var converter = new BilibiliDanmakuConverter()
            .SetTopFilter(settings.TopFilter == AllowStatus.Yes)
            .SetBottomFilter(settings.BottomFilter == AllowStatus.Yes)
            .SetScrollFilter(settings.ScrollFilter == AllowStatus.Yes);
        var downloadBase = downloading.DownloadBase
                           ?? throw new InvalidOperationException("DownloadBase is required to download danmaku.");
        try
        {
            await _stateWriter.ClaimTransferFileAsync(
                taskId,
                "danmaku",
                assFile,
                cancellationToken).ConfigureAwait(false);
            var publish = await PublishDanmakuAsync(
                assFile,
                converter,
                downloadBase,
                subtitleConfig,
                cancellationToken).ConfigureAwait(false);
            if (publish.IsDestinationCollision)
            {
                return OutputCollisionFailure();
            }
            var integrity = DownloadFileIntegrity.Check(assFile);
            if (!integrity.IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.danmaku.invalid",
                    "The requested danmaku output is missing or invalid.");
            }

            await RecordPublishedArtifactAsync(
                taskId,
                "danmaku",
                DanmakuArtifactKind,
                assFile,
                publish).ConfigureAwait(false);
            return OperationResult.Success(DownloadArtifactWriteResult.Created(assFile));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            _logger.LogErrorMessage("Danmaku download failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.http",
                "The requested danmaku could not be downloaded.");
        }
        catch (InvalidProtocolBufferException e)
        {
            _logger.LogErrorMessage("Danmaku response parsing failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.parse",
                "The requested danmaku response was invalid.");
        }
        catch (InvalidDataException e)
        {
            _logger.LogErrorMessage("Danmaku response contract validation failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.parse",
                "The requested danmaku response was invalid.");
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("Danmaku conversion or write failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.io",
                "The requested danmaku could not be converted or written.");
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("Danmaku output was denied.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.permission",
                "Permission was denied while writing the requested danmaku.");
        }
    }

    public async Task<OperationResult<DownloadArtifactWriteResult>> DownloadSubtitleAsync(
        DownloadingItem downloading,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingSubtitle");
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;
        var taskId = new DownloadTaskId(downloading.DownloadBase.Id);
        await _stateWriter.UpdateActivityAsync(
            taskId,
            downloading.DownloadContent,
            downloading.DownloadStatusTitle,
            cancellationToken).ConfigureAwait(false);

        var srtFiles = new List<string>();
        Exception? parseFailure = null;
        IReadOnlyList<SubRipText> subRipTexts;
        try
        {
            subRipTexts = await WbiRequestExecutor.ExecuteAsync(
                _wbiKeyProvider,
                (keys, unixTimeSeconds) => _client.GetSubtitleAsync(
                    keys,
                    unixTimeSeconds,
                    downloading.DownloadBase.Avid,
                    downloading.DownloadBase.Bvid,
                    downloading.DownloadBase.Cid,
                    e => parseFailure ??= e,
                    cancellationToken),
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            _logger.LogErrorMessage("Subtitle download failed.", e);
            return ArtifactFailure(
                "download.artifact.subtitle.http",
                "The requested subtitles could not be downloaded.");
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("Subtitle response could not be read.", e);
            return ArtifactFailure(
                "download.artifact.subtitle.io",
                "The requested subtitle response could not be read.");
        }

        if (parseFailure != null)
        {
            _logger.LogErrorMessage("Subtitle response parsing failed.", parseFailure);
            return ArtifactFailure(
                "download.artifact.subtitle.parse",
                "The requested subtitle response was invalid.");
        }

        if (subRipTexts.Count == 0)
        {
            _logger.LogWarningMessage("No usable subtitles were returned for the download task.");
            return OperationResult.Success(DownloadArtifactWriteResult.NotAvailable());
        }

        for (var index = 0; index < subRipTexts.Count; index++)
        {
            var subRip = subRipTexts[index];
            var srtFile = $"{downloading.DownloadBase.FilePath}_{subRip.LanDoc}.srt";
            try
            {
                await _stateWriter.ClaimTransferFileAsync(
                    taskId,
                    GetSubtitleTrackTransferKey(index),
                    srtFile,
                    cancellationToken).ConfigureAwait(false);
                var publish = await PublishSubtitleAsync(
                    srtFile,
                    subRip.SrtString,
                    cancellationToken).ConfigureAwait(false);
                if (publish.IsDestinationCollision)
                {
                    return OutputCollisionFailure();
                }
                var integrity = DownloadFileIntegrity.Check(srtFile);
                if (!integrity.IsUsable)
                {
                    return ArtifactFailure(
                        "download.artifact.subtitle.invalid",
                        "A requested subtitle output is missing or invalid.");
                }

                await RecordPublishedArtifactAsync(
                    taskId,
                    GetSubtitleTrackTransferKey(index),
                    SubtitleArtifactKind,
                    srtFile,
                    publish).ConfigureAwait(false);
                srtFiles.Add(srtFile);
            }
            catch (IOException e)
            {
                _logger.LogErrorMessage("Subtitle download failed.", e);
                return ArtifactFailure(
                    "download.artifact.subtitle.io",
                    "A requested subtitle could not be written.");
            }
            catch (UnauthorizedAccessException e)
            {
                _logger.LogErrorMessage("Subtitle download was denied.", e);
                return ArtifactFailure(
                    "download.artifact.subtitle.permission",
                    "Permission was denied while writing a requested subtitle.");
            }
        }

        var defaultSubtitleFile = $"{downloading.DownloadBase.FilePath}.srt";
        try
        {
            await _stateWriter.ClaimTransferFileAsync(
                taskId,
                DefaultSubtitleTransferKey,
                defaultSubtitleFile,
                cancellationToken).ConfigureAwait(false);
            var publish = await PublishDefaultSubtitleAsync(
                defaultSubtitleFile,
                srtFiles[0],
                cancellationToken).ConfigureAwait(false);
            if (publish.IsDestinationCollision)
            {
                return OutputCollisionFailure();
            }
            if (!DownloadFileIntegrity.Check(defaultSubtitleFile).IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.subtitle.invalid",
                    "The default subtitle output is missing or invalid.");
            }

            await RecordPublishedArtifactAsync(
                taskId,
                DefaultSubtitleTransferKey,
                SubtitleArtifactKind,
                defaultSubtitleFile,
                publish).ConfigureAwait(false);
            srtFiles.Add(defaultSubtitleFile);
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("Default subtitle write failed.", e);
            return ArtifactFailure(
                "download.artifact.subtitle.io",
                "The default subtitle could not be written.");
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("Default subtitle write was denied.", e);
            return ArtifactFailure(
                "download.artifact.subtitle.permission",
                "Permission was denied while writing the default subtitle.");
        }

        return OperationResult.Success(DownloadArtifactWriteResult.Created(srtFiles));
    }

    public async Task<OperationResult<DownloadArtifactWriteResult>> GenerateNfoFileAsync(
        DownloadingItem downloading,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        if (downloading.Metadata == null)
        {
            return OperationResult.Success(DownloadArtifactWriteResult.NotAvailable());
        }

        var nfoFile = $"{downloading.DownloadBase.FilePath}.nfo";
        try
        {
            await _stateWriter.ClaimTransferFileAsync(
                new DownloadTaskId(downloading.DownloadBase.Id),
                "nfo",
                nfoFile,
                cancellationToken).ConfigureAwait(false);
            var publish = await PublishNfoAsync(
                nfoFile,
                downloading.Metadata,
                cancellationToken).ConfigureAwait(false);
            if (publish.IsDestinationCollision)
            {
                return OutputCollisionFailure();
            }

            if (!DownloadFileIntegrity.Check(nfoFile).IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.nfo.invalid",
                    "The requested metadata output is missing or invalid.");
            }

            await RecordPublishedArtifactAsync(
                new DownloadTaskId(downloading.DownloadBase.Id),
                "nfo",
                NfoArtifactKind,
                nfoFile,
                publish).ConfigureAwait(false);
            return OperationResult.Success(DownloadArtifactWriteResult.Created(nfoFile));
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("NFO generation failed.", e);
            return ArtifactFailure(
                "download.artifact.nfo.io",
                "The requested metadata file could not be written.");
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("NFO generation was denied.", e);
            return ArtifactFailure(
                "download.artifact.nfo.permission",
                "Permission was denied while writing the requested metadata file.");
        }
        catch (XmlException e)
        {
            _logger.LogErrorMessage("NFO generation produced invalid XML.", e);
            return ArtifactFailure(
                "download.artifact.nfo.xml",
                "The requested metadata file could not be generated.");
        }
    }

    private static XmlWriter CreateNfoWriter(string path)
    {
        return XmlWriter.Create(path, new XmlWriterSettings { Async = true, Indent = true });
    }

    private static string GetDanmakuLayoutAlgorithmValue(DanmakuLayoutAlgorithm algorithm)
    {
        return algorithm switch
        {
            DanmakuLayoutAlgorithm.None => "none",
            DanmakuLayoutAlgorithm.Async => "async",
            DanmakuLayoutAlgorithm.Sync => "sync",
            _ => throw new ArgumentOutOfRangeException(
                nameof(algorithm),
                algorithm,
                "Unsupported danmaku layout algorithm.")
        };
    }
}
