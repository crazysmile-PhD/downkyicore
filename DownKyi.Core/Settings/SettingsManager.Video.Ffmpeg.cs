namespace DownKyi.Core.Settings;

public partial class SettingsManager
{
    private const FfmpegHardwareAcceleration DefaultFfmpegHardwareAcceleration =
        Settings.FfmpegHardwareAcceleration.Auto;
    private const int FfmpegMaxParallelJobs = 1;

    public FfmpegHardwareAcceleration GetFfmpegHardwareAcceleration()
    {
        if (_appSettings.Video.FfmpegHardwareAcceleration != FfmpegHardwareAcceleration.NotSet)
        {
            return _appSettings.Video.FfmpegHardwareAcceleration;
        }

        SetFfmpegHardwareAcceleration(DefaultFfmpegHardwareAcceleration);
        return DefaultFfmpegHardwareAcceleration;
    }

    public bool SetFfmpegHardwareAcceleration(FfmpegHardwareAcceleration acceleration)
    {
        return SetProperty(
            _appSettings.Video.FfmpegHardwareAcceleration,
            acceleration,
            v => _appSettings.Video.FfmpegHardwareAcceleration = v);
    }

    public int GetFfmpegMaxParallelJobs()
    {
        if (_appSettings.Video.FfmpegMaxParallelJobs != -1)
        {
            return Math.Clamp(_appSettings.Video.FfmpegMaxParallelJobs, 1, 4);
        }

        SetFfmpegMaxParallelJobs(FfmpegMaxParallelJobs);
        return FfmpegMaxParallelJobs;
    }

    public bool SetFfmpegMaxParallelJobs(int maxParallelJobs)
    {
        var value = Math.Clamp(maxParallelJobs, 1, 4);
        return SetProperty(
            _appSettings.Video.FfmpegMaxParallelJobs,
            value,
            v => _appSettings.Video.FfmpegMaxParallelJobs = v);
    }
}
