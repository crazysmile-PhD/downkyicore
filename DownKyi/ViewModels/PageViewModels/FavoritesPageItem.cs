using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Images;
using DownKyi.Utils;

namespace DownKyi.ViewModels.PageViewModels;

internal class FavoritesPageItem : ObservableObject
{
    private string coverUrl = string.Empty;

    public string CoverUrl
    {
        get => coverUrl;
        set => SetProperty(ref coverUrl, value);
    }

    public long UpperMid { get; set; }


    private string title = string.Empty;

    public string Title
    {
        get => title;
        set => SetProperty(ref title, value);
    }

    private string createTime = string.Empty;

    public string CreateTime
    {
        get => createTime;
        set => SetProperty(ref createTime, value);
    }

    private string playNumber = string.Empty;

    public string PlayNumber
    {
        get => playNumber;
        set => SetProperty(ref playNumber, value);
    }

    private string likeNumber = string.Empty;

    public string LikeNumber
    {
        get => likeNumber;
        set => SetProperty(ref likeNumber, value);
    }

    private string favoriteNumber = string.Empty;

    public string FavoriteNumber
    {
        get => favoriteNumber;
        set => SetProperty(ref favoriteNumber, value);
    }

    private string shareNumber = string.Empty;

    public string ShareNumber
    {
        get => shareNumber;
        set => SetProperty(ref shareNumber, value);
    }

    private VectorImage play = null!;

    public VectorImage Play
    {
        get => play;
        set => SetProperty(ref play, value);
    }

    private VectorImage like = null!;

    public VectorImage Like
    {
        get => like;
        set => SetProperty(ref like, value);
    }

    private VectorImage favorite = null!;

    public VectorImage Favorite
    {
        get => favorite;
        set => SetProperty(ref favorite, value);
    }

    private VectorImage share = null!;

    public VectorImage Share
    {
        get => share;
        set => SetProperty(ref share, value);
    }

    private string description = string.Empty;

    public string Description
    {
        get => description;
        set => SetProperty(ref description, value);
    }

    private int mediaCount;

    public int MediaCount
    {
        get => mediaCount;
        set => SetProperty(ref mediaCount, value);
    }

    private string upName = string.Empty;

    public string UpName
    {
        get => upName;
        set => SetProperty(ref upName, value);
    }

    private string upHeader = string.Empty;

    public string UpHeader
    {
        get => upHeader;
        set => SetProperty(ref upHeader, value);
    }

    public FavoritesPageItem()
    {
        #region 属性初始化

        Play = NormalIcon.Instance().Play;
        Play.Fill = DictionaryResource.GetColor("ColorTextGrey2");

        Like = NormalIcon.Instance().Like;
        Like.Fill = DictionaryResource.GetColor("ColorTextGrey2");

        Favorite = NormalIcon.Instance().Favorite;
        Favorite.Fill = DictionaryResource.GetColor("ColorTextGrey2");

        Share = NormalIcon.Instance().Share;
        Share.Fill = DictionaryResource.GetColor("ColorTextGrey2");

        #endregion
    }
}
