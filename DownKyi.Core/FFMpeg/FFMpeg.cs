using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Helpers;
using FFMpegCore.Pipes;

namespace DownKyi.Core.FFMpeg;

public class FFMpeg
{
    private const string Tag = "FFmpegHelper";
    private static readonly FFMpeg instance = new();
    private static readonly object FfmpegJobLock = new();
    private static int _runningFfmpegJobs;

    static FFMpeg()
    {
    }

    private FFMpeg()
    {
        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg") });
        FFMpegHelper.VerifyFFMpegExists(GlobalFFOptions.Current);
    }

    public static FFMpeg Instance => instance;

    /// <summary>
    /// 合并音频和视频
    /// </summary>
    /// <param name="audio">音频</param>
    /// <param name="video">视频</param>
    /// <param name="destVideo"></param>
    public bool MergeVideo(string? audio, string? video, string destVideo)
    {
        var audioPath = !string.IsNullOrEmpty(audio) && File.Exists(audio) ? audio : null;
        var videoPath = !string.IsNullOrEmpty(video) && File.Exists(video) ? video : null;
        if (audioPath == null && videoPath == null) return false;

        FFMpegArgumentProcessor arguments;
        if (audioPath != null && videoPath != null)
        {
            arguments = FFMpegArguments
                .FromFileInput(audioPath)
                .AddFileInput(videoPath)
                .OutputToFile(destVideo, true, options => options
                    .WithCustomArgument("-strict -2")
                    .WithAudioCodec("copy")
                    .WithVideoCodec("copy")
                    .ForceFormat("mp4")
                );
        }
        else if (videoPath != null)
        {
            arguments = FFMpegArguments.FromFileInput(videoPath).OutputToFile(
                destVideo,
                true,
                options => options.WithCustomArgument("-strict -2").WithVideoCodec("copy").WithAudioCodec("copy").ForceFormat("mp4")
            );
        }
        else
        {
            if (SettingsManager.GetInstance().GetIsTranscodingAacToMp3() == AllowStatus.Yes)
            {
                arguments = FFMpegArguments.FromFileInput(audioPath!).OutputToFile(
                    destVideo,
                    true,
                    options => options.WithCustomArgument("-strict -2").DisableChannel(Channel.Video).ForceFormat("mp3")
                );
            }
            else
            {
                arguments = FFMpegArguments.FromFileInput(audioPath!).OutputToFile(
                    destVideo,
                    true,
                    options => options.WithCustomArgument("-strict -2").DisableChannel(Channel.Video).WithAudioCodec("copy")
                );
            }
        }

        if (!RunFfmpeg(arguments, "merge media"))
        {
            return false;
        }

        try
        {
            if (audio != null)
            {
                File.Delete(audio);
            }

            if (video != null)
            {
                File.Delete(video);
            }
        }
        catch (IOException e)
        {
            Console.WriteLine("MergeVideo()发生IO异常: {0}", e);
            LogManager.Error(Tag, e);
        }

        return true;
    }

    /// <summary>
    /// 去水印，非常消耗cpu资源
    /// </summary>
    /// <param name="video"></param>
    /// <param name="destVideo"></param>
    /// <param name="x"></param>
    /// <param name="y"></param>
    /// <param name="width"></param>
    /// <param name="height"></param>
    /// <param name="action"></param>
    public void Delogo(string video, string destVideo, int x, int y, int width, int height, Action<string> action)
    {
        var arguments = FFMpegArguments
            .FromFileInput(video)
            .OutputToFile(
                destVideo,
                true,
                option => option
                    .WithCustomArgument($"-vf delogo=x={x}:y={y}:w={width}:h={height}:show=0 -hide_banner"));

        RunFfmpeg(arguments, "delogo", action);
    }

    /// <summary>
    /// 从一个视频中仅提取音频
    /// </summary>
    /// <param name="video">源视频</param>
    /// <param name="audio">目标音频</param>
    /// <param name="action">输出信息</param>
    public void ExtractAudio(string video, string audio, Action<string> action)
    {
        var arguments = FFMpegArguments
            .FromFileInput(video)
            .OutputToFile(audio,
                true,
                options => options
                    .WithCustomArgument("-hide_banner")
                    .WithAudioCodec("copy")
                    .DisableChannel(Channel.Video)
            );

        RunFfmpeg(arguments, "extract audio", action);
    }

    /// <summary>
    /// 从一个视频中仅提取视频
    /// </summary>
    /// <param name="video">源视频</param>
    /// <param name="destVideo">目标视频</param>
    /// <param name="action">输出信息</param>
    public void ExtractVideo(string video, string destVideo, Action<string> action)
    {
        var arguments = FFMpegArguments.FromFileInput(video)
            .OutputToFile(
                destVideo,
                true,
                options => options
                    .WithCustomArgument("-hide_banner")
                    .WithVideoCodec("copy")
                    .DisableChannel(Channel.Audio));

        RunFfmpeg(arguments, "extract video", action);
    }


    public async Task<MemoryStream> ExtractVideoFrame(string inputPath, TimeSpan timestamp)
    {
        var ms = new MemoryStream();
        await FFMpegArguments
            .FromFileInput(inputPath, false, options => options.Seek(timestamp))
            .OutputToPipe(new StreamPipeSink(ms), options => options
                .WithFrameOutputCount(1)
                .ForceFormat("image2")
                .WithVideoCodec("mjpeg")
            )
            .NotifyOnError(x => Console.WriteLine(x))
            .ProcessAsynchronously(false);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// 合并多个FLV视频片段为一个完整视频
    /// </summary>
    /// <param name="inputFlvs">FLV片段路径列表(按顺序)</param>
    /// <param name="outputVideo">输出视频路径</param>
    /// <param name="action">进度回调</param>
    /// <returns>是否成功</returns>
    public bool ConcatVideos(List<string> inputFlvs, string outputVideo, Action<string> action)
    {
        var listFile = string.Empty;
        try
        {
            if (inputFlvs == null || inputFlvs.Count == 0)
            {
                return false;
            }

            // 验证所有输入文件都存在
            foreach (var video in inputFlvs)
            {
                if (!File.Exists(video))
                {
                    action?.Invoke($"文件不存在: {video}");
                    return false;
                }
            }

            LogManager.Info(Tag,
                $"Concat video started. segments={inputFlvs.Count}; hw={SettingsManager.GetInstance().GetFfmpegHardwareAcceleration()}; maxParallel={SettingsManager.GetInstance().GetFfmpegMaxParallelJobs()}");

            listFile = Path.Combine(Path.GetTempPath(), $"flvlist_{Guid.NewGuid():N}.txt");
            File.WriteAllLines(listFile, inputFlvs.Select(ToConcatFileLine));

            if (TryConcatWithStreamCopy(listFile, outputVideo, action))
            {
                LogManager.Info(Tag, "Concat video completed by stream copy.");
                return true;
            }

            DeleteOutput(outputVideo);

            var encoder = FfmpegHardwareEncoderDetector.Select(SettingsManager.GetInstance().GetFfmpegHardwareAcceleration());
            if (encoder != null && TryConcatWithHardwareEncoder(listFile, outputVideo, encoder, action))
            {
                LogManager.Info(Tag, $"Concat video completed by {encoder.DisplayName}.");
                return true;
            }

            DeleteOutput(outputVideo);
            var cpuResult = TryConcatWithCpuEncoder(listFile, outputVideo, action);
            LogManager.Info(Tag, $"Concat video completed by CPU encoder. success={cpuResult}");
            return cpuResult;
        }
        catch (Exception ex)
        {
            LogManager.Error(Tag, ex);
            return false;
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(listFile) && File.Exists(listFile))
                {
                    File.Delete(listFile);
                }
            }
            catch (Exception e)
            {
                LogManager.Error(Tag, e);
            }
        }
    }

    private bool TryConcatWithStreamCopy(string listFile, string outputVideo, Action<string> action)
    {
        var arguments = BuildConcatInput(listFile)
            .OutputToFile(outputVideo, true, options => options
                .WithVideoCodec("copy")
                .WithAudioCodec("copy")
                .WithCustomArgument("-movflags +faststart")
                .WithCustomArgument("-avoid_negative_ts make_zero"));

        return RunFfmpeg(arguments, "concat stream copy", action) && IsValidOutput(outputVideo);
    }

    private bool TryConcatWithHardwareEncoder(
        string listFile,
        string outputVideo,
        FfmpegHardwareEncoderProfile encoder,
        Action<string> action)
    {
        var arguments = BuildConcatInput(listFile)
            .OutputToFile(outputVideo, true, options => options
                .WithAudioCodec("aac")
                .WithCustomArgument(encoder.OutputArguments)
                .WithCustomArgument("-movflags +faststart")
                .WithCustomArgument("-avoid_negative_ts make_zero"));

        var success = RunFfmpeg(arguments, $"concat hardware transcode ({encoder.DisplayName})", action) &&
                      IsValidOutput(outputVideo);
        if (!success)
        {
            LogManager.Info(Tag, $"Hardware encoder failed, falling back to CPU. encoder={encoder.DisplayName}");
        }

        return success;
    }

    private bool TryConcatWithCpuEncoder(string listFile, string outputVideo, Action<string> action)
    {
        var arguments = BuildConcatInput(listFile)
            .OutputToFile(outputVideo, true, options => options
                .WithVideoCodec("libx264")
                .WithAudioCodec("aac")
                .WithCustomArgument("-preset veryfast")
                .WithCustomArgument("-crf 23")
                .WithCustomArgument("-threads 2")
                .WithCustomArgument("-movflags +faststart")
                .WithCustomArgument("-avoid_negative_ts make_zero"));

        return RunFfmpeg(arguments, "concat CPU transcode", action) && IsValidOutput(outputVideo);
    }

    private static FFMpegArguments BuildConcatInput(string listFile)
    {
        return FFMpegArguments.FromFileInput(listFile, false, options => options
            .WithCustomArgument("-f concat -safe 0"));
    }

    private static string ToConcatFileLine(string file)
    {
        var normalizedPath = Path.GetFullPath(file).Replace('\\', '/');
        return $"file '{normalizedPath.Replace("'", "'\\''")}'";
    }

    private static bool IsValidOutput(string outputVideo)
    {
        return File.Exists(outputVideo) && new FileInfo(outputVideo).Length > 0;
    }

    private static void DeleteOutput(string outputVideo)
    {
        try
        {
            if (File.Exists(outputVideo))
            {
                File.Delete(outputVideo);
            }
        }
        catch (Exception e)
        {
            LogManager.Error(Tag, e);
        }
    }

    private static bool RunFfmpeg(FFMpegArgumentProcessor arguments, string operation, Action<string>? action = null)
    {
        using var _ = EnterFfmpegSlot(operation);
        try
        {
            LogManager.Debug(Tag, arguments.Arguments);
            var result = arguments
                .NotifyOnOutput(s =>
                {
                    action?.Invoke(s);
                    LogManager.Debug(Tag, s);
                })
                .NotifyOnError(s =>
                {
                    action?.Invoke(s);
                    LogManager.Debug(Tag, s);
                })
                .ProcessSynchronously(false);

            LogManager.Info(Tag, $"FFmpeg operation finished. operation={operation}; success={result}");
            return result;
        }
        catch (Exception e)
        {
            LogManager.Error(Tag, e);
            return false;
        }
    }

    private static IDisposable EnterFfmpegSlot(string operation)
    {
        var maxParallel = SettingsManager.GetInstance().GetFfmpegMaxParallelJobs();
        lock (FfmpegJobLock)
        {
            while (_runningFfmpegJobs >= maxParallel)
            {
                Monitor.Wait(FfmpegJobLock);
                maxParallel = SettingsManager.GetInstance().GetFfmpegMaxParallelJobs();
            }

            _runningFfmpegJobs++;
            LogManager.Info(Tag,
                $"FFmpeg operation started. operation={operation}; running={_runningFfmpegJobs}; maxParallel={maxParallel}");
        }

        return new FfmpegSlot();
    }

    private sealed class FfmpegSlot : IDisposable
    {
        public void Dispose()
        {
            lock (FfmpegJobLock)
            {
                _runningFfmpegJobs = Math.Max(0, _runningFfmpegJobs - 1);
                Monitor.PulseAll(FfmpegJobLock);
            }
        }
    }
}
