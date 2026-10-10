using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DownKyi.Services.Download;

internal enum DownloadActionKind
{
    Media,
    Subtitle,
    Danmaku,
    Cover
}

internal static class DownloadActionResultEvaluator
{
    public static void Evaluate(DownloadExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.NeedsMedia)
        {
            var mediaSucceeded = context.HasUsablePublishedMedia()
                                 || context.MediaSucceeded
                                 && DownloadFileIntegrity.Check(context.OutputMedia).IsUsable;
            context.ActionResults.Set(
                DownloadActionKind.Media,
                mediaSucceeded
                    ? DownloadActionResultStatus.Succeeded
                    : DownloadActionResultStatus.Failed);
        }

        if (context.NeedsDanmaku)
        {
            var danmakuSucceeded = DownloadArtifactsStage.GetDanmakuOutputs(
                    context.WorkingBasePath,
                    context.Input.DanmakuSettings.OutputFormat)
                .All(output => context.HasPublished(output.Key)
                               || DownloadFileIntegrity.Check(output.File).IsUsable);
            context.ActionResults.Set(
                DownloadActionKind.Danmaku,
                danmakuSucceeded
                    ? DownloadActionResultStatus.Succeeded
                    : DownloadActionResultStatus.Failed);
        }

        if (context.NeedsSubtitle)
        {
            context.ActionResults.Set(
                DownloadActionKind.Subtitle,
                HasUsableSubtitle(context)
                    ? DownloadActionResultStatus.Succeeded
                    : DownloadActionResultStatus.NoResource);
        }

        if (context.NeedsCover)
        {
            var coverSucceeded = context.HasPublished("cover")
                                 || context.HasPublished("page-cover")
                                 || DownloadFileIntegrity.Check(context.CoverFile).IsUsable
                                 || DownloadFileIntegrity.Check(context.PageCoverFile).IsUsable;
            context.ActionResults.Set(
                DownloadActionKind.Cover,
                coverSucceeded
                    ? DownloadActionResultStatus.Succeeded
                    : DownloadActionResultStatus.NoResource);
        }
    }

    private static bool HasUsableSubtitle(DownloadExecutionContext context) =>
        context.PublishedArtifacts.Any(artifact =>
            artifact.Key.StartsWith("subtitle:", StringComparison.Ordinal)
            && DownloadFileIntegrity.Check(artifact.Value).IsUsable)
        || context.SubtitleFiles?.Any(file => DownloadFileIntegrity.Check(file).IsUsable) == true;
}

internal enum DownloadActionResultStatus
{
    Succeeded,
    NoResource,
    Failed
}

internal sealed class DownloadActionExecutionSummary
{
    private readonly Dictionary<DownloadActionKind, DownloadActionResultStatus> _results = [];

    public IReadOnlyDictionary<DownloadActionKind, DownloadActionResultStatus> Results => _results;

    public bool HasSucceeded => _results.ContainsValue(DownloadActionResultStatus.Succeeded);

    public void Set(DownloadActionKind action, DownloadActionResultStatus status)
    {
        _results[action] = status;
    }
}
