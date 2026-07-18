using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownKyi.ViewModels.PageViewModels;

internal class VideoInfoView : ObservableObject
{
    private string _coverUrl = string.Empty;

    public string CoverUrl
    {
        get => _coverUrl;
        set => SetProperty(ref _coverUrl, value);
    }

    public long UpperMid { get; set; }
    public int TypeId { get; set; }

    private string _title = string.Empty;

    public float? Score { get; set; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private string _videoZone = string.Empty;

    public string VideoZone
    {
        get => _videoZone;
        set => SetProperty(ref _videoZone, value);
    }

    private string _createTime = string.Empty;

    public string CreateTime
    {
        get => _createTime;
        set => SetProperty(ref _createTime, value);
    }

    private string _playNumber = string.Empty;

    public string PlayNumber
    {
        get => _playNumber;
        set => SetProperty(ref _playNumber, value);
    }

    private string _danmakuNumber = string.Empty;

    public string DanmakuNumber
    {
        get => _danmakuNumber;
        set => SetProperty(ref _danmakuNumber, value);
    }

    private string _likeNumber = string.Empty;

    public string LikeNumber
    {
        get => _likeNumber;
        set => SetProperty(ref _likeNumber, value);
    }

    private string _coinNumber = string.Empty;

    public string CoinNumber
    {
        get => _coinNumber;
        set => SetProperty(ref _coinNumber, value);
    }

    private string _favoriteNumber = string.Empty;

    public string FavoriteNumber
    {
        get => _favoriteNumber;
        set => SetProperty(ref _favoriteNumber, value);
    }

    private string _shareNumber = string.Empty;

    public string ShareNumber
    {
        get => _shareNumber;
        set => SetProperty(ref _shareNumber, value);
    }

    private string _replyNumber = string.Empty;

    public string ReplyNumber
    {
        get => _replyNumber;
        set => SetProperty(ref _replyNumber, value);
    }

    private string _description = string.Empty;

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    private string _upName = string.Empty;

    public string UpName
    {
        get => _upName;
        set => SetProperty(ref _upName, value);
    }

    private string _upHeader = string.Empty;

    public string UpHeader
    {
        get => _upHeader;
        set => SetProperty(ref _upHeader, value);
    }

}
