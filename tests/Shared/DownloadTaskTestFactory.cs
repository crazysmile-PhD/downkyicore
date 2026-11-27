using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Testing;

/// <summary>Seeds existing UI interaction fixtures through the real task boundary.</summary>
internal static class DownloadTaskTestFactory
{
    public static DownloadTaskDraft CreateDraft(DownloadingItem item, DownloadNfoRequest? nfoRequest = null)
    {
        var source = item.DownloadBase;
        return new DownloadTaskDraft(
            new DownloadTaskId(source.Id),
            new DownloadTaskMetadata(
                new DownloadMediaIdentity(source.Bvid, source.Avid, source.Cid, source.EpisodeId, source.Page, source.Order),
                source.MainTitle, source.Name, source.Duration, source.VideoCodecName,
                new DownloadQuality(source.Resolution.Id, source.Resolution.Name),
                new DownloadQuality(source.AudioCodec.Id, source.AudioCodec.Name),
                source.CoverUrl, source.PageCoverUrl, source.ZoneId),
            source.NeedDownloadContent,
            (int)item.Downloading.PlayStreamType,
            source.FilePath)
        {
            NfoRequest = nfoRequest
        };
    }

    public static DownloadTask CreateTask(
        DownloadingItem item,
        DateTimeOffset createdAtUtc,
        DownloadNfoRequest? nfoRequest = null) => CreateDraft(item, nfoRequest).CreateTask(createdAtUtc);

    public static async Task AddDownloadingAsync(
        this DownloadTaskProjectionStore projections,
        IDownloadTaskCatalog tasks,
        DownloadingItem item,
        CancellationToken cancellationToken,
        DownloadNfoRequest? nfoRequest = null)
    {
        var task = CreateTask(item, DateTimeOffset.UnixEpoch, nfoRequest);
        (await tasks.AddAsync(task, cancellationToken).ConfigureAwait(false)).RequireValue();
        projections.Track(task, item);
    }

    public static async Task<bool> AdmitAsync(
        this DownloadTaskAdmissionService admission,
        DownloadingItem item,
        CancellationToken cancellationToken,
        bool redownloadCompleted = false,
        DownloadNfoRequest? nfoRequest = null)
    {
        var draft = CreateDraft(item, nfoRequest);
        var admitted = await admission.AdmitAsync(draft, cancellationToken, redownloadCompleted).ConfigureAwait(false);
        item.DownloadBase.FilePath = draft.BasePath;
        return admitted;
    }

    public static Task<DownloadDuplicateReview> ReviewDuplicatesAsync(
        this DownloadTaskAdmissionService admission,
        IReadOnlyList<DownloadingItem> items,
        CancellationToken cancellationToken) =>
        admission.ReviewDuplicatesAsync(items.Select(item => CreateDraft(item)).ToArray(), cancellationToken);

    public static DownloadNfoRequest CreateNfo(string title = "metadata", string plot = "", string year = "", string premiered = "", string[]? genres = null) =>
        new(title, plot, year, [.. genres ?? []], [], [], null, premiered, []);
}
