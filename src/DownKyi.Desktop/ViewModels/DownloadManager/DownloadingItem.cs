using System;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.Utils;
using DownloadStatus = DownKyi.Models.DownloadStatus;

namespace DownKyi.ViewModels.DownloadManager
{
    internal class DownloadingItem : DownloadBaseItem
    {

        public DownloadingItem()
        {
            // 暂停继续按钮
            StartOrPause = ButtonIcon.Pause;
        }

        // model数据
        private Downloading _downloading = null!;


        public MovieMetadata? Metadata { get; set; }

        public Downloading Downloading
        {
            get => _downloading;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _downloading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DownloadContent));
                OnPropertyChanged(nameof(DownloadStatusTitle));
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(DownloadingFileSize));
                OnPropertyChanged(nameof(SpeedDisplay));
                RefreshControlPresentation(value.DownloadStatus);
            }
        }

        // 正在下载内容（音频、视频、弹幕、字幕、封面）
        public string? DownloadContent
        {
            get => Downloading.DownloadContent;
            set
            {
                Downloading.DownloadContent = value;
                OnPropertyChanged();
            }
        }

        // 下载状态显示
        public string? DownloadStatusTitle
        {
            get => Downloading.DownloadStatusTitle;
            set
            {
                Downloading.DownloadStatusTitle = value;
                OnPropertyChanged();
            }
        }

        // 下载进度
        public float Progress
        {
            get => Downloading.Progress;
            set
            {
                Downloading.Progress = value;
                OnPropertyChanged();
            }
        }

        //  已下载大小/文件大小
        public string? DownloadingFileSize
        {
            get => Downloading.DownloadingFileSize;
            set
            {
                Downloading.DownloadingFileSize = value;
                OnPropertyChanged();
            }
        }

        //  下载速度
        public string? SpeedDisplay
        {
            get => Downloading.SpeedDisplay;
            set
            {
                Downloading.SpeedDisplay = value;
                OnPropertyChanged();
            }
        }

        // 操作提示
        private string _operationTip = string.Empty;

        public string OperationTip
        {
            get => _operationTip;
            set => SetProperty(ref _operationTip, value);
        }

        #region 控制按钮

        private VectorImage _startOrPause = null!;

        public VectorImage StartOrPause
        {
            get => _startOrPause;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                SetProperty(ref _startOrPause, value);

                OperationTip = value.Equals(ButtonIcon.Start) ? DictionaryResource.GetString("StartDownload")
                    : value.Equals(ButtonIcon.Pause) ? DictionaryResource.GetString("PauseDownload")
                    : value.Equals(ButtonIcon.Retry) ? DictionaryResource.GetString("RetryDownload") : string.Empty;
            }
        }

        public VectorImage Delete { get; } = ButtonIcon.Delete;

        #endregion

        private void RefreshControlPresentation(DownloadStatus status)
        {
            StartOrPause = status switch
            {
                DownloadStatus.PauseStarted or DownloadStatus.Pause => ButtonIcon.Start,
                DownloadStatus.DownloadFailed => ButtonIcon.Retry,
                _ => ButtonIcon.Pause
            };
        }
    }
}
