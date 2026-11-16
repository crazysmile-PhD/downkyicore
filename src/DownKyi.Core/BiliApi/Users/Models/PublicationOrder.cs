namespace DownKyi.Core.BiliApi.Users.Models;

public enum PublicationOrder
{
    None = 0,
    PUBDATE = 1, // 最新发布，默认
    CLICK, // 最多播放
    STOW // 最多收藏
}
