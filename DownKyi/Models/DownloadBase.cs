using System;
using System.Collections.Generic;
using System.ComponentModel;
using DownKyi.Core.BiliApi.BiliUtils;

namespace DownKyi.Models;

[Description("下载项的基础信息")]
public class DownloadBase
{
    public DownloadBase()
    {
        // 唯一id
        Id = Guid.NewGuid().ToString("N");

        // 初始化需要下载的内容
        NeedDownloadContent = new Dictionary<string, bool>
        {
            { "downloadAudio", true },
            { "downloadVideo", true },
            { "downloadDanmaku", true },
            { "downloadSubtitle", true },
            { "downloadCover", true }
        };
    }

    // 此条下载项的id
    public string Id { get; set; }

    // 需要下载的内容
    public Dictionary<string, bool> NeedDownloadContent { get; set; }

    // 视频的id
    public string Bvid { get; set; }

    public long Avid { get; set; }

    public long Cid { get; set; }

    public long EpisodeId { get; set; }

    // 视频封面的url
    [Description("视频封面的url")] public string CoverUrl { get; set; }

    // 视频page的封面的url
    [Description("视频page的封面的url")] public string PageCoverUrl { get; set; }

    // 分区id
    [Description("分区id")] public int ZoneId { get; set; }

    // 视频序号
    [Description("视频序号")] public int Order { get; set; }

    // 视频主标题
    [Description("视频主标题")] public string MainTitle { get; set; }

    // 视频标题
    [Description("视频标题")] public string Name { get; set; }

    // 时长
    [Description("时长")] public string Duration { get; set; }

    // 视频编码名称，AVC、HEVC
    [Description("视频编码名称，AVC、HEVC")] public string VideoCodecName { get; set; }

    // 视频画质
    [Description("视频画质")] public Quality Resolution { get; set; }

    // 音频编码
    [Description("音频编码")] public Quality? AudioCodec { get; set; }

    // 文件路径，不包含扩展名，所有内容均以此路径下载
    [Description("文件路径，不包含扩展名，所有内容均以此路径下载")]
    public string FilePath { get; set; }

    // 文件大小
    [Description("文件大小")] public string? FileSize { get; set; }

    // 视频分p(默认为1)
    [Description("视频分p(默认为1)")] public int Page { get; set; } = 1;
}