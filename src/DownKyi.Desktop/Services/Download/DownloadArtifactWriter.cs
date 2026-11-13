using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.DanmakuApi;
using DownKyi.Core.BiliApi.Models.Json;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Danmaku2Ass;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DownKyi.Services.Download;

internal sealed partial class DownloadArtifactWriter
{
    internal const string SubtitleResponseReadFailureCode =
        "download.artifact.subtitle.response-read";
    internal const string SubtitleOutputWriteFailureCode =
        "download.artifact.subtitle.output-write";

    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly ILogger _logger;
    private readonly IBilibiliApiClient _client;

    public DownloadArtifactWriter(
        IWbiKeyProvider wbiKeyProvider,
        DownloadTaskStateWriter stateWriter,
        ILogger logger,
        IBilibiliApiClient client)
    {
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    internal static string GetDanmakuTransferKey(DownloadDanmakuOutputKind kind) =>
        kind switch
        {
            DownloadDanmakuOutputKind.Ass => DanmakuAssTransferKey,
            DownloadDanmakuOutputKind.Xml => DanmakuXmlTransferKey,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

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

    public async Task<OperationResult<DownloadArtifactWriteResult>> DownloadDanmakuAsync(
        DownloadTaskId taskId,
        DownloadTaskMetadata metadata,
        IReadOnlyList<DownloadDanmakuOutputPath> outputs,
        DanmakuApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(settings);

        var assFile = outputs
            .Where(output => output.Kind == DownloadDanmakuOutputKind.Ass)
            .Select(static output => (string?)output.File)
            .SingleOrDefault();
        var xmlFile = outputs
            .Where(output => output.Kind == DownloadDanmakuOutputKind.Xml)
            .Select(static output => (string?)output.File)
            .SingleOrDefault();
        var outputFiles = outputs.Select(output => output.File).ToList();
        var outputClaims = outputs
            .Select(output => new KeyValuePair<string, string>(
                GetDanmakuTransferKey(output.Kind),
                output.File))
            .ToList();

        var subtitleConfig = new Config
        {
            Title = metadata.Name,
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
            .SetScrollFilter(settings.ScrollFilter == AllowStatus.Yes)
            .SetCustomFilter(
                settings.RemoveEmojiAndSpecialCharacters == AllowStatus.Yes,
                settings.BlockedKeywords,
                settings.BlockedSenderUids);
        try
        {
            if (string.IsNullOrWhiteSpace(assFile) && string.IsNullOrWhiteSpace(xmlFile))
            {
                throw new ArgumentException("At least one danmaku output file is required.");
            }

            await _stateWriter.ClaimTransferFilesAsync(
                taskId,
                outputClaims,
                cancellationToken).ConfigureAwait(false);

            var danmakus = await converter.GetDanmakusAsync(
                _client,
                metadata.Media.Avid,
                metadata.Media.Cid,
                cancellationToken).ConfigureAwait(false);
            if (assFile != null)
            {
                var text = converter.CreateAss(subtitleConfig, danmakus, cancellationToken);
                await File.WriteAllTextAsync(assFile, text, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (xmlFile != null)
            {
                var xml = BilibiliDanmakuXmlWriter.CreateDocument(danmakus, cancellationToken);
                foreach (var invalidCharacter in xml.InvalidCharacters)
                {
                    var message =
                        "Skipped danmaku that XML 1.0 cannot represent. " +
                        $"dmid={invalidCharacter.Id.ToString(CultureInfo.InvariantCulture)}; " +
                        $"field={invalidCharacter.Field}; " +
                        $"invalid=U+{invalidCharacter.InvalidValue:X4}; " +
                        $"utf16Index={invalidCharacter.Utf16Index.ToString(CultureInfo.InvariantCulture)}; " +
                        $"category={invalidCharacter.Category}";
                    if (invalidCharacter.ContextBefore != null && invalidCharacter.ContextAfter != null)
                    {
                        message +=
                            $"; before={JsonConvert.ToString(invalidCharacter.ContextBefore)}" +
                            $"; after={JsonConvert.ToString(invalidCharacter.ContextAfter)}";
                    }
                    else
                    {
                        message += "; context=redacted";
                    }

                    _logger.LogWarningMessage(message + ".");
                }

                var output = new FileStream(
                    xmlFile,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    await xml.Document.SaveAsync(output, SaveOptions.DisableFormatting, cancellationToken)
                        .ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (assFile != null && !DownloadFileIntegrity.Check(assFile).IsUsable ||
                xmlFile != null && !DownloadFileIntegrity.CheckXml(xmlFile, "i").IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.danmaku.invalid",
                    "The requested danmaku output is missing or invalid.");
            }

            return OperationResult.Success(DownloadArtifactWriteResult.Created(outputFiles));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            _logger.LogErrorMessage("Danmaku download failed.", e);
            return ArtifactFailure(
                GetArtifactHttpFailureCode("danmaku", e.StatusCode),
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
        catch (XmlException e)
        {
            _logger.LogErrorMessage("Danmaku XML generation failed.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.xml",
                "The requested danmaku XML could not be generated.");
        }
        catch (ArgumentException e)
        {
            _logger.LogErrorMessage("Danmaku output contained invalid data.", e);
            return ArtifactFailure(
                "download.artifact.danmaku.data",
                "The requested danmaku output contained invalid data.");
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
                _wbiKeyProvider,
                (keys, unixTimeSeconds) => _client.GetSubtitleSelectionAsync(
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
            _logger.LogErrorMessage("Subtitle download failed.", e);
            return SubtitleArtifactFailure(
                GetArtifactHttpFailureCode("subtitle", e.StatusCode),
                "The requested subtitles could not be downloaded.");
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("Subtitle response could not be read.", e);
            return SubtitleArtifactFailure(
                SubtitleResponseReadFailureCode,
                "The requested subtitle response could not be read.");
        }

        if (parseFailure != null)
        {
            _logger.LogErrorMessage("Subtitle response parsing failed.", parseFailure);
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
            _logger.LogWarningMessage("Not all selected subtitle tracks were returned for the download task.");
            return SubtitleArtifactFailure(
                "download.artifact.subtitle.missing-tracks",
                "One or more selected subtitle tracks were not available.");
        }

        if (subRipTexts.Count == 0)
        {
            _logger.LogWarningMessage("No usable subtitles were returned for the download task.");
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
                await _stateWriter.ClaimTransferFileAsync(
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
                _logger.LogErrorMessage("Subtitle download failed.", e);
                return SubtitleArtifactFailure(
                    SubtitleOutputWriteFailureCode,
                    "A requested subtitle could not be written.");
            }
            catch (UnauthorizedAccessException e)
            {
                _logger.LogErrorMessage("Subtitle download was denied.", e);
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
                await _stateWriter.ClaimTransferFileAsync(
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
                _logger.LogErrorMessage("Default subtitle write failed.", e);
                return SubtitleArtifactFailure(
                    SubtitleOutputWriteFailureCode,
                    "The default subtitle could not be written.");
            }
            catch (UnauthorizedAccessException e)
            {
                _logger.LogErrorMessage("Default subtitle write was denied.", e);
                return SubtitleArtifactFailure(
                    "download.artifact.subtitle.permission",
                    "Permission was denied while writing the default subtitle.");
            }
        }

        return OperationResult.Success(DownloadSubtitleWriteResult.Created(
            trackFiles,
            defaultSubtitleFile));
    }

    public async Task<OperationResult<DownloadArtifactWriteResult>> GenerateNfoFileAsync(
        DownloadTaskId taskId,
        string nfoFile,
        DownloadNfoRequest metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nfoFile);
        ArgumentNullException.ThrowIfNull(metadata);

        try
        {
            await _stateWriter.ClaimTransferFileAsync(
                taskId,
                "nfo",
                nfoFile,
                cancellationToken).ConfigureAwait(false);
            var writer = XmlWriter.Create(
                nfoFile,
                new XmlWriterSettings { Async = true, Indent = true });
            try
            {
                WriteMovieMetadata(writer, metadata);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }

            if (!DownloadFileIntegrity.Check(nfoFile).IsUsable)
            {
                return ArtifactFailure(
                    "download.artifact.nfo.invalid",
                    "The requested metadata output is missing or invalid.");
            }

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

    private static OperationResult<DownloadArtifactWriteResult> ArtifactFailure(
        string code,
        string message)
    {
        return OperationResult.Failure<DownloadArtifactWriteResult>(
            OperationError.Unexpected(code, message));
    }

    private static string GetArtifactHttpFailureCode(
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

    private static OperationResult<DownloadSubtitleWriteResult> SubtitleArtifactFailure(
        string code,
        string message)
    {
        return OperationResult.Failure<DownloadSubtitleWriteResult>(
            OperationError.Unexpected(code, message));
    }

    private static void WriteMovieMetadata(XmlWriter writer, DownloadNfoRequest metadata)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("movie");
        writer.WriteElementString("title", metadata.Title);
        writer.WriteElementString("plot", metadata.Plot);
        writer.WriteElementString("year", metadata.Year);

        foreach (var genre in metadata.Genres)
        {
            writer.WriteElementString("genre", genre);
        }

        foreach (var tag in metadata.Tags)
        {
            writer.WriteElementString("tag", tag);
        }

        foreach (var actor in metadata.Actors)
        {
            writer.WriteStartElement("actor");
            writer.WriteElementString("name", actor.Name);
            writer.WriteElementString("role", actor.Role);
            writer.WriteEndElement();
        }

        if (metadata.BilibiliId != null)
        {
            writer.WriteStartElement("uniqueid");
            writer.WriteAttributeString("type", metadata.BilibiliId.Type);
            writer.WriteString(metadata.BilibiliId.Value);
            writer.WriteEndElement();
        }

        writer.WriteElementString("premiered", metadata.Premiered);
        foreach (var rating in metadata.Ratings)
        {
            writer.WriteStartElement("rating");
            writer.WriteAttributeString("name", rating.Name);
            writer.WriteAttributeString("max", rating.Max.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("default", rating.IsDefault ? "true" : "false");
            writer.WriteString(rating.Value.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
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
