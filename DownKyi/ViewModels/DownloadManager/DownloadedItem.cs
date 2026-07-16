using DownKyi.Images;
using DownKyi.Models;
using DownKyi.Utils;

namespace DownKyi.ViewModels.DownloadManager;

internal class DownloadedItem : DownloadBaseItem
{
    public DownloadedItem()
    {
        // 打开文件夹按钮
        OpenFolder = ButtonIcon.Instance().Folder;
        OpenFolder.Fill = DictionaryResource.GetColor("ColorPrimary");

        // 打开视频按钮
        OpenVideo = ButtonIcon.Instance().Start;
        OpenVideo.Fill = DictionaryResource.GetColor("ColorPrimary");

        // 删除按钮
        RemoveVideo = ButtonIcon.Instance().Trash;
        RemoveVideo.Fill = DictionaryResource.GetColor("ColorWarning");
    }

    // model数据
    public Downloaded Downloaded { get; set; } = null!;

    //  下载速度
    public string? MaxSpeedDisplay
    {
        get => Downloaded.MaxSpeedDisplay;
        set
        {
            Downloaded.MaxSpeedDisplay = value;
            OnPropertyChanged();
        }
    }

    // 完成时间
    public string FinishedTime
    {
        get => Downloaded.FinishedTime;
        set
        {
            Downloaded.FinishedTime = value;
            OnPropertyChanged();
        }
    }

    #region 控制按钮

    private VectorImage _openFolder = null!;

    public VectorImage OpenFolder
    {
        get => _openFolder;
        set => SetProperty(ref _openFolder, value);
    }

    private VectorImage _openVideo = null!;

    public VectorImage OpenVideo
    {
        get => _openVideo;
        set => SetProperty(ref _openVideo, value);
    }

    private VectorImage _removeVideo = null!;

    public VectorImage RemoveVideo
    {
        get => _removeVideo;
        set => SetProperty(ref _removeVideo, value);
    }

    #endregion
}
