using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Danmaku2Ass;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadArtifactWriter
{
    private readonly ISettingsStore _settingsStore;
    private readonly IWbiKeyProvider _wbiKeyProvider;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly ILogger _logger;

    public DownloadArtifactWriter(
        ISettingsStore settingsStore,
        IWbiKeyProvider wbiKeyProvider,
        DownloadTaskStateWriter stateWriter,
        ILogger logger)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _wbiKeyProvider = wbiKeyProvider ?? throw new ArgumentNullException(nameof(wbiKeyProvider));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> DownloadCoverAsync(
        DownloadingItem downloading,
        string? coverUrl,
        string fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingCover");
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;

        try
        {
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                return null;
            }

            WebClient.DownloadFile(coverUrl, fileName, cancellationToken: cancellationToken);
            if (downloading.Downloading.DownloadFiles.TryAdd(coverUrl, fileName))
            {
                await _stateWriter.UpdateAsync(downloading, cancellationToken).ConfigureAwait(false);
            }

            return fileName;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException e)
        {
            _logger.LogErrorMessage("Cover download failed.", e);
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("Cover download timed out.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("Cover download was denied.", e);
        }

        return null;
    }

    public async Task<string> DownloadDanmakuAsync(
        DownloadingItem downloading,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingDanmaku");
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;

        var assFile = $"{downloading.DownloadBase?.FilePath}.ass";
        if (downloading.Downloading.DownloadFiles.TryAdd("danmaku", assFile))
        {
            await _stateWriter.UpdateAsync(downloading, cancellationToken).ConfigureAwait(false);
        }

        var settings = _settingsStore.Current.Danmaku;
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
        converter.Create(downloadBase.Avid, downloadBase.Cid, subtitleConfig, assFile, cancellationToken);
        return assFile;
    }

    public async Task<IReadOnlyList<string>> DownloadSubtitleAsync(
        DownloadingItem downloading,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString("DownloadingSubtitle");
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;

        var srtFiles = new List<string>();
        var subRipTexts = await WbiRequestExecutor.ExecuteAsync(
            _wbiKeyProvider,
            (keys, unixTimeSeconds) => VideoStreamApi.GetSubtitle(
                keys,
                unixTimeSeconds,
                downloading.DownloadBase.Avid,
                downloading.DownloadBase.Bvid,
                downloading.DownloadBase.Cid,
                e => _logger.LogErrorMessage("Subtitle response parsing failed.", e),
                cancellationToken),
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
        if (subRipTexts.Count == 0)
        {
            _logger.LogWarningMessage("No usable subtitles were returned for the download task.");
        }

        foreach (var subRip in subRipTexts)
        {
            var srtFile = $"{downloading.DownloadBase.FilePath}_{subRip.LanDoc}.srt";
            try
            {
                await File.WriteAllTextAsync(srtFile, subRip.SrtString, cancellationToken).ConfigureAwait(false);
                if (downloading.Downloading.DownloadFiles.TryAdd("subtitle", srtFile))
                {
                    await _stateWriter.UpdateAsync(downloading, cancellationToken).ConfigureAwait(false);
                }

                srtFiles.Add(srtFile);
            }
            catch (IOException e)
            {
                _logger.LogErrorMessage("Subtitle download failed.", e);
            }
            catch (UnauthorizedAccessException e)
            {
                _logger.LogErrorMessage("Subtitle download was denied.", e);
            }
        }

        if (srtFiles.Count > 0)
        {
            var defaultSubtitleFile = $"{downloading.DownloadBase.FilePath}.srt";
            File.Copy(srtFiles[0], defaultSubtitleFile, true);
            srtFiles.Add(defaultSubtitleFile);
        }

        return srtFiles;
    }

    public void GenerateNfoFile(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        if (downloading.Metadata == null)
        {
            return;
        }

        try
        {
            using var writer = XmlWriter.Create(
                $"{downloading.DownloadBase.FilePath}.nfo",
                new XmlWriterSettings { Indent = true });
            WriteMovieMetadata(writer, downloading.Metadata);
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("NFO generation failed.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("NFO generation was denied.", e);
        }
        catch (XmlException e)
        {
            _logger.LogErrorMessage("NFO generation produced invalid XML.", e);
        }
    }

    private static void WriteMovieMetadata(XmlWriter writer, MovieMetadata metadata)
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
