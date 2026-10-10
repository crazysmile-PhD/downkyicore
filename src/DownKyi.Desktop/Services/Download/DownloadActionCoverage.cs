using System;
using System.IO;
using System.Linq;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal static class DownloadActionCoverage
{
    public static DownloadContentSelection RemoveCoveredActions(
        DownloadBaseItem existingItem,
        DownloadingItem requestedItem,
        DownloadContentSelection requestedContent,
        bool requireUsableArtifacts)
    {
        var existingBase = existingItem.DownloadBase;
        var requestedBase = requestedItem.DownloadBase;
        if (existingBase.Cid != requestedBase.Cid
            || !HasSameOutputPath(existingItem, requestedBase.FilePath))
        {
            return requestedContent;
        }

        var existingContent = GetRecordedContent(existingItem);
        var coveredClaims = DownloadActionClaims.None;
        if (requestedContent.HasMedia
            && MediaParametersMatch(
                existingItem,
                requestedItem,
                existingContent,
                requestedContent)
            && (!requireUsableArtifacts
                || CompletedOutputExists(existingItem, static key => key == "media")))
        {
            coveredClaims = coveredClaims.Union(DownloadActionClaims.Media);
        }

        if (requestedContent.HasSubtitleAction
            && SubtitleSelectionMatches(existingContent, requestedContent)
            && (!requireUsableArtifacts
                || CompletedOutputExists(existingItem, static key =>
                    key.StartsWith("subtitle:", StringComparison.Ordinal))))
        {
            coveredClaims = coveredClaims.Union(DownloadActionClaims.Subtitle);
        }

        if (requestedContent.Danmaku)
        {
            var coveredFormats = GetCoveredDanmakuFormats(
                existingItem,
                existingContent,
                requestedContent.DanmakuOutputFormat,
                requireUsableArtifacts);
            coveredClaims = coveredClaims.Union(
                DownloadActionClaims.FromDanmaku(coveredFormats));
        }

        if (requestedContent.Cover
            && existingContent.Cover
            && (!requireUsableArtifacts
                || CompletedOutputExists(existingItem, static key =>
                    key is "cover" or "page-cover")))
        {
            coveredClaims = coveredClaims.Union(DownloadActionClaims.Cover);
        }

        return coveredClaims.SubtractFrom(requestedContent);
    }

    private static bool HasSameOutputPath(DownloadBaseItem item, string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        var existingPath = item.DownloadBase.FilePath;
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            return string.Equals(
                DownloadOutputPathKey.Create(
                    existingPath,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison),
                DownloadOutputPathKey.Create(
                    requestedPath,
                    DownloadOutputPathKey.UsesCaseInsensitiveComparison),
                StringComparison.Ordinal);
        }

        if (item is not DownloadedItem { HistoryRecord: { } history })
        {
            return false;
        }

        var requestedKey = DownloadOutputPathKey.Create(
            requestedPath,
            DownloadOutputPathKey.UsesCaseInsensitiveComparison);
        return history.PublishedArtifacts.Values.Any(path =>
        {
            var artifactKey = DownloadOutputPathKey.Create(
                path,
                DownloadOutputPathKey.UsesCaseInsensitiveComparison);
            return artifactKey.StartsWith(requestedKey + ".", StringComparison.Ordinal)
                   || artifactKey.StartsWith(requestedKey + "_", StringComparison.Ordinal);
        });
    }

    private static bool MediaParametersMatch(
        DownloadBaseItem item,
        DownloadingItem requestedItem,
        DownloadContentSelection existingContent,
        DownloadContentSelection requestedContent)
    {
        if (existingContent.Audio != requestedContent.Audio
            || existingContent.Video != requestedContent.Video)
        {
            return false;
        }

        if (requestedContent.Video)
        {
            if (item.Resolution.Id != requestedItem.Resolution.Id
                || item.VideoCodecName != requestedItem.VideoCodecName)
            {
                return false;
            }

            if (existingContent.MediaKind is { } existingKind
                && requestedContent.MediaKind is { } requestedKind
                && existingKind != requestedKind)
            {
                return false;
            }
        }

        return !requestedContent.Audio
               || requestedContent.Video
               && requestedContent.MediaKind == DownloadMediaKind.Durl
               || item.AudioCodec.Name == requestedItem.AudioCodec.Name;
    }

    private static DownloadDanmakuOutputFormat GetCoveredDanmakuFormats(
        DownloadBaseItem item,
        DownloadContentSelection existingContent,
        DownloadDanmakuOutputFormat? requestedFormat,
        bool requireUsableArtifacts)
    {
        if (!existingContent.Danmaku)
        {
            return DownloadDanmakuOutputFormat.None;
        }

        var requested = requestedFormat ?? AllDanmakuFormats;
        var existing = ResolveDanmakuOutputFormat(item, existingContent);
        var covered = requested & existing;
        if (!requireUsableArtifacts)
        {
            return covered;
        }

        if (covered.HasFlag(DownloadDanmakuOutputFormat.Ass)
            && !HasUsableArtifact(item, DownloadArtifactWriter.DanmakuAssTransferKey))
        {
            covered &= ~DownloadDanmakuOutputFormat.Ass;
        }

        if (covered.HasFlag(DownloadDanmakuOutputFormat.Xml)
            && !HasUsableArtifact(item, DownloadArtifactWriter.DanmakuXmlTransferKey))
        {
            covered &= ~DownloadDanmakuOutputFormat.Xml;
        }

        return covered;
    }

    private static bool SubtitleSelectionMatches(
        DownloadContentSelection existing,
        DownloadContentSelection requested)
    {
        if (existing.SubtitleTrackSelection != requested.SubtitleTrackSelection
            || existing.DefaultSubtitleTrackId != requested.DefaultSubtitleTrackId)
        {
            return false;
        }

        return existing.SelectedSubtitleTrackIds is not { } existingIds
               || requested.SelectedSubtitleTrackIds is { } requestedIds
               && existingIds.ToHashSet().SetEquals(requestedIds);
    }

    private static DownloadDanmakuOutputFormat ResolveDanmakuOutputFormat(
        DownloadBaseItem item,
        DownloadContentSelection existing)
    {
        var existingFormat = existing.DanmakuOutputFormat;
        if (existingFormat == null && item is DownloadedItem { HistoryRecord: { } history })
        {
            var hasAss = history.PublishedArtifacts.ContainsKey(
                DownloadArtifactWriter.DanmakuAssTransferKey);
            var hasXml = history.PublishedArtifacts.ContainsKey(
                DownloadArtifactWriter.DanmakuXmlTransferKey);
            existingFormat = (hasAss, hasXml) switch
            {
                (true, true) => DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml,
                (true, false) => DownloadDanmakuOutputFormat.Ass,
                (false, true) => DownloadDanmakuOutputFormat.Xml,
                _ => null
            };
        }

        return existingFormat ?? AllDanmakuFormats;
    }

    private static bool CompletedOutputExists(
        DownloadBaseItem item,
        Func<string, bool> keyPredicate)
    {
        if (item is not DownloadedItem { HistoryRecord: { } history })
        {
            return true;
        }

        return history.PublishedArtifacts
            .Where(artifact => keyPredicate(artifact.Key))
            .Any(artifact => DownloadFileIntegrity.Check(artifact.Value).IsUsable);
    }

    private static bool HasUsableArtifact(DownloadBaseItem item, string key) =>
        item is DownloadedItem { HistoryRecord: { } history }
        && history.PublishedArtifacts.TryGetValue(key, out var path)
        && DownloadFileIntegrity.Check(path).IsUsable;

    private static DownloadContentSelection GetRecordedContent(DownloadBaseItem item)
    {
        var content = item.DownloadBase.NeedDownloadContent;
        if (item is not DownloadedItem { HistoryRecord: { } history })
        {
            return content;
        }

        if (history.RequestedContent is { } finalizedContent)
        {
            return finalizedContent;
        }

        if (!history.PublishedArtifacts.TryGetValue("media", out var mediaPath))
        {
            return history.PublishedArtifacts.Count == 0
                ? content
                : content with { Audio = false, Video = false, MediaKind = DownloadMediaKind.None };
        }

        var extension = Path.GetExtension(mediaPath);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            return content with { Audio = true, Video = false, MediaKind = DownloadMediaKind.Dash };
        }

        return content;
    }

    private const DownloadDanmakuOutputFormat AllDanmakuFormats =
        DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml;
}
