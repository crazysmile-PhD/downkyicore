using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Models;
using DownKyi.Utils;

namespace DownKyi.ViewModels.DownloadManager
{
    internal class DownloadBaseItem : ObservableObject
    {
        // model数据
        private DownloadBase _downloadBase = new();

        public DownloadBase DownloadBase
        {
            get => _downloadBase;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                var zoneChanged = _downloadBase.ZoneId != value.ZoneId;
                _downloadBase = value;

                if (zoneChanged || ZoneImage == null)
                {
                    ZoneImage = DictionaryResource.GetIfApplicationInitialized<DrawingImage>(
                        VideoZoneIcon.GetZoneImageKey(value.ZoneId));
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(Order));
                OnPropertyChanged(nameof(MainTitle));
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(VideoCodecName));
                OnPropertyChanged(nameof(Resolution));
                OnPropertyChanged(nameof(AudioCodec));
                OnPropertyChanged(nameof(FileSize));
            }
        }

        // 视频分区image
        private DrawingImage? _zoneImage;

        public DrawingImage? ZoneImage
        {
            get => _zoneImage;
            set => SetProperty(ref _zoneImage, value);
        }

        // 视频序号
        public int Order => DownloadBase.Order;

        // 视频主标题
        public string MainTitle => DownloadBase.MainTitle;

        // 视频标题
        public string Name => DownloadBase.Name;

        // 时长
        public string Duration => DownloadBase.Duration;

        // 视频编码名称，AVC、HEVC
        public string VideoCodecName => DownloadBase.VideoCodecName;

        // 视频画质
        public Quality Resolution => DownloadBase.Resolution;

        // 音频编码
        public Quality AudioCodec => DownloadBase.AudioCodec;

        // 文件大小
        public string? FileSize => DownloadBase.FileSize;
    }
}
