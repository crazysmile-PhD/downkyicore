using System.Collections.Immutable;
using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Downloads;

public sealed record DownloadHistoryRecord
{
    public DownloadHistoryRecord(
        DownloadTaskId id,
        long cid,
        int zoneId,
        int order,
        string mainTitle,
        string name,
        string durationText,
        string videoCodecName,
        int resolutionId,
        string resolutionName,
        string audioCodecName,
        string? fileSizeText,
        IEnumerable<KeyValuePair<string, string>> publishedArtifacts,
        long finishedTimestamp,
        string finishedTimeText,
        string? maximumSpeedText,
        DownloadContentSelection? requestedContent = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(mainTitle);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(durationText);
        ArgumentNullException.ThrowIfNull(videoCodecName);
        ArgumentNullException.ThrowIfNull(resolutionName);
        ArgumentNullException.ThrowIfNull(audioCodecName);
        ArgumentNullException.ThrowIfNull(publishedArtifacts);
        ArgumentNullException.ThrowIfNull(finishedTimeText);

        Id = id;
        Cid = cid;
        ZoneId = zoneId;
        Order = order;
        MainTitle = mainTitle;
        Name = name;
        DurationText = durationText;
        VideoCodecName = videoCodecName;
        ResolutionId = resolutionId;
        ResolutionName = resolutionName;
        AudioCodecName = audioCodecName;
        FileSizeText = fileSizeText;
        PublishedArtifacts = publishedArtifacts.ToImmutableDictionary(
            static entry => entry.Key,
            static entry => entry.Value,
            StringComparer.Ordinal);
        FinishedTimestamp = finishedTimestamp;
        FinishedTimeText = finishedTimeText;
        MaximumSpeedText = maximumSpeedText;
        RequestedContent = requestedContent;
    }

    public DownloadTaskId Id { get; }
    public long Cid { get; }
    public int ZoneId { get; }
    public int Order { get; }
    public string MainTitle { get; }
    public string Name { get; }
    public string DurationText { get; }
    public string VideoCodecName { get; }
    public int ResolutionId { get; }
    public string ResolutionName { get; }
    public string AudioCodecName { get; }
    public string? FileSizeText { get; }
    public ImmutableDictionary<string, string> PublishedArtifacts { get; }
    public long FinishedTimestamp { get; }
    public string FinishedTimeText { get; }
    public string? MaximumSpeedText { get; }
    public DownloadContentSelection? RequestedContent { get; }

    public static DownloadHistoryRecord FromCompletedTask(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Phase != DownloadPhase.Completed || task.Completion == null)
        {
            throw new ArgumentException("History can only be projected from a completed task.", nameof(task));
        }

        return new DownloadHistoryRecord(
            task.Id,
            task.Metadata.Media.Cid,
            task.Metadata.ZoneId,
            task.Metadata.Media.Order,
            task.Metadata.MainTitle,
            task.Metadata.Name,
            task.Metadata.DurationText,
            task.Metadata.VideoCodecName,
            task.Metadata.Resolution.Id,
            task.Metadata.Resolution.Name,
            task.Metadata.AudioCodec.Name,
            task.Output.FileSizeText,
            task.Output.PublishedArtifacts,
            task.Completion.FinishedTimestamp,
            task.Completion.FinishedTimeText,
            task.Completion.MaximumSpeedText,
            task.Plan.RequestedContent);
    }
}
