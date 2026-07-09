namespace DownKyi.Core.Settings;

public enum FfmpegHardwareAcceleration
{
    NotSet = 0,
    Auto,
    Disabled,
    NvidiaNvenc,
    IntelQsv,
    AmdAmf,
    Vaapi,
    VideoToolbox
}
