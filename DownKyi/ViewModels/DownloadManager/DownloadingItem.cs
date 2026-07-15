using System;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.Utils;
using Downloader;
using DownloadStatus = DownKyi.Models.DownloadStatus;

namespace DownKyi.ViewModels.DownloadManager
{
    internal class DownloadingItem : DownloadBaseItem
    {

        public DownloadingItem()
        {
            // 暂停继续按钮
            StartOrPause = ButtonIcon.Instance().Pause;
            StartOrPause.Fill = DictionaryResource.GetColor("ColorPrimary");

            // 删除按钮
            Delete = ButtonIcon.Instance().Delete;
            Delete.Fill = DictionaryResource.GetColor("ColorPrimary");
        }

        public DownloadService? DownloadService { get; set; }

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
                RefreshControlPresentation(value.DownloadStatus);
            }
        }

        // 视频流链接
        public PlayUrl PlayUrl { get; set; } = null!;

        // 正在下载内容（音频、视频、弹幕、字幕、封面）
        public string? DownloadContent
        {
            get => Downloading.DownloadContent;
            set
            {
                Downloading.DownloadContent = value;
                RaisePropertyChanged();
            }
        }

        // 下载状态显示
        public string? DownloadStatusTitle
        {
            get => Downloading.DownloadStatusTitle;
            set
            {
                Downloading.DownloadStatusTitle = value;
                RaisePropertyChanged();
            }
        }

        // 下载进度
        public float Progress
        {
            get => Downloading.Progress;
            set
            {
                Downloading.Progress = value;
                RaisePropertyChanged();
            }
        }

        //  已下载大小/文件大小
        public string? DownloadingFileSize
        {
            get => Downloading.DownloadingFileSize;
            set
            {
                Downloading.DownloadingFileSize = value;
                RaisePropertyChanged();
            }
        }

        //  下载速度
        public string? SpeedDisplay
        {
            get => Downloading.SpeedDisplay;
            set
            {
                Downloading.SpeedDisplay = value;
                RaisePropertyChanged();
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

                OperationTip = value.Equals(ButtonIcon.Instance().Start) ? DictionaryResource.GetString("StartDownload")
                    : value.Equals(ButtonIcon.Instance().Pause) ? DictionaryResource.GetString("PauseDownload")
                    : value.Equals(ButtonIcon.Instance().Retry) ? DictionaryResource.GetString("RetryDownload") : string.Empty;
            }
        }

        private VectorImage _delete = null!;

        public VectorImage Delete
        {
            get => _delete;
            set => SetProperty(ref _delete, value);
        }

        #endregion

        internal void ApplyControlStatus(DownloadStatus status)
        {
            Downloading.DownloadStatus = status;
            DownloadStatusTitle = status switch
            {
                DownloadStatus.PauseStarted or DownloadStatus.Pause => DictionaryResource.GetString("Pausing"),
                DownloadStatus.NotStarted or DownloadStatus.WaitForDownload => DictionaryResource.GetString("Waiting"),
                DownloadStatus.Downloading => DictionaryResource.GetString("WhileDownloading"),
                DownloadStatus.DownloadFailed => DictionaryResource.GetString("DownloadFailed"),
                _ => DownloadStatusTitle
            };
            RefreshControlPresentation(status);
        }

        internal void RestoreControlStatus(DownloadStatus status, string? title)
        {
            Downloading.DownloadStatus = status;
            DownloadStatusTitle = title;
            RefreshControlPresentation(status);
        }

        private void RefreshControlPresentation(DownloadStatus status)
        {
            StartOrPause = status switch
            {
                DownloadStatus.PauseStarted or DownloadStatus.Pause => ButtonIcon.Instance().Start,
                DownloadStatus.DownloadFailed => ButtonIcon.Instance().Retry,
                _ => ButtonIcon.Instance().Pause
            };
            StartOrPause.Fill = DictionaryResource.GetColor("ColorPrimary");
        }
    }
}
