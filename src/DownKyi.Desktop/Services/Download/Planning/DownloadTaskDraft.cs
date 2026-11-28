using System;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

/// <summary>Admission input, before a durable task and its UI projection exist.</summary>
internal sealed class DownloadTaskDraft(
    DownloadTaskId id,
    DownloadTaskMetadata metadata,
    DownloadContentSelection requestedContent,
    int streamType,
    string basePath)
{
    public DownloadTaskId Id { get; } = id;
    public DownloadTaskMetadata Metadata { get; set; } = metadata;
    public DownloadContentSelection RequestedContent { get; } = requestedContent;
    public int StreamType { get; } = streamType;
    public string BasePath { get; set; } = basePath;
    public DownloadNfoRequest? NfoRequest { get; set; }

    public DownloadTask CreateTask(DateTimeOffset createdAtUtc) => DownloadTask.Create(
        Id,
        Metadata,
        new DownloadPlan(RequestedContent, [], StreamType, NfoRequest),
        new DownloadOutput(BasePath, fileSizeText: null),
        createdAtUtc);
}
