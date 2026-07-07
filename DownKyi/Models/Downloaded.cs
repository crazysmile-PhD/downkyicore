using System;

namespace DownKyi.Models;

public class Downloaded
{
    public string Id { get; set; } = null!;

    //  下载速度
    public string? MaxSpeedDisplay { get; set; }

    // 完成时间戳
    public long FinishedTimestamp { get; set; }

    public void SetFinishedTimestamp(long finishedTimestamp)
    {
        FinishedTimestamp = finishedTimestamp;

        var startTime = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1), TimeZoneInfo.Local); // 当地时区
        var dateTime = startTime.AddSeconds(finishedTimestamp);
        FinishedTime = dateTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // 完成时间
    public string FinishedTime { get; set; } = string.Empty;

    public DownloadBase? DownloadBase { get; set; }
}