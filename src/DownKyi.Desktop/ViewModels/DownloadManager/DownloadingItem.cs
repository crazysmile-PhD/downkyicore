using System;
using System.ComponentModel;
using DownKyi.Domain.Downloads;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.Utils;
using DownloadStatus = DownKyi.Models.DownloadStatus;

namespace DownKyi.ViewModels.DownloadManager
{
    internal class DownloadingItem : DownloadBaseItem
    {
        private bool _isSelected;
        private bool _isGrouped;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsGrouped
        {
            get => _isGrouped;
            set
            {
                if (SetProperty(ref _isGrouped, value))
                {
                    OnPropertyChanged(nameof(QueueTitle));
                }
            }
        }

        public string QueueTitle => IsGrouped
            ? $"{Order} · {Name}"
            : string.IsNullOrWhiteSpace(MainTitle) || MainTitle == Name
                ? Name
                : $"{MainTitle} · {Name}";

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName is nameof(MainTitle) or nameof(Name) or nameof(Order))
            {
                OnPropertyChanged(nameof(QueueTitle));
            }
        }

        public DownloadingItem()
        {
            // 暂停继续按钮
            StartOrPause = ButtonIcon.Pause;
        }

        // model数据
        private Downloading _downloading = null!;


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
                OnPropertyChanged(nameof(HasFailureDetails));
                if (value.DownloadStatus != DownloadStatus.DownloadFailed)
                {
                    IsFailureDetailsExpanded = false;
                }
                RefreshControlPresentation(value.DownloadStatus);
            }
        }

        private DownloadFailure? _failure;

        public DownloadFailure? Failure
        {
            get => _failure;
            set
            {
                if (!SetProperty(ref _failure, value))
                {
                    return;
                }

                IsFailureDetailsExpanded = false;
                OnPropertyChanged(nameof(HasFailureDetails));
                OnPropertyChanged(nameof(FailureDetailsText));
            }
        }

        public bool HasFailureDetails =>
            _failure != null && _downloading?.DownloadStatus == DownloadStatus.DownloadFailed;

        private bool _isFailureDetailsExpanded;

        public bool IsFailureDetailsExpanded
        {
            get => _isFailureDetailsExpanded;
            set
            {
                if (SetProperty(ref _isFailureDetailsExpanded, value && HasFailureDetails))
                {
                    OnPropertyChanged(nameof(FailureDetailsToggleText));
                }
            }
        }

        public string FailureDetailsToggleText => DictionaryResource.GetString(
            IsFailureDetailsExpanded ? "HideFailureDetails" : "DownloadFailureDetails");

        public string FailureDetailsText => _failure is null
            ? string.Empty
            : DownloadActivityPresenter.FormatFailureDetails(_failure);

        // 正在下载内容（音频、视频、弹幕、字幕、封面）
        public string? DownloadContent
        {
            get => Downloading.DownloadContent;
            set
            {
                if (Downloading.DownloadContent == value) return;
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
                if (Downloading.DownloadStatusTitle == value) return;
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
                if (Downloading.Progress == value) return;
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
                if (Downloading.DownloadingFileSize == value) return;
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
                if (Downloading.SpeedDisplay == value) return;
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
                if (!SetProperty(ref _startOrPause, value))
                {
                    return;
                }

                OperationTip = value.Equals(ButtonIcon.Start) ? DictionaryResource.GetString("StartDownload")
                    : value.Equals(ButtonIcon.Pause) ? DictionaryResource.GetString("PauseDownload")
                    : value.Equals(ButtonIcon.Retry) ? DictionaryResource.GetString("RetryDownload") : string.Empty;
            }
        }

        public VectorImage Delete { get; } = ButtonIcon.Delete;

        private bool _isToggleVisible = true;

        public bool IsToggleVisible
        {
            get => _isToggleVisible;
            set => SetProperty(ref _isToggleVisible, value);
        }

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
