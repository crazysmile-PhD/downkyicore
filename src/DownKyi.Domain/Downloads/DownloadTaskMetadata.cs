namespace DownKyi.Domain.Downloads;

public sealed record DownloadQuality(int Id, string Name);

public sealed record DownloadMediaIdentity(
    string Bvid,
    long Avid,
    long Cid,
    long EpisodeId,
    int Page,
    int Order);

public sealed record DownloadTaskMetadata(
    DownloadMediaIdentity Media,
    string MainTitle,
    string Name,
    string DurationText,
    string VideoCodecName,
    DownloadQuality Resolution,
    DownloadQuality AudioCodec,
    string CoverAddress,
    string PageCoverAddress,
    int ZoneId);
