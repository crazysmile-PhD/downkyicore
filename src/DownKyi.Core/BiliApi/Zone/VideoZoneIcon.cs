namespace DownKyi.Core.BiliApi.Zone;

/// <summary>
/// 视频分区图标
/// </summary>
public static class VideoZoneIcon
{
    private const string DefaultImageKey = "videoUpDrawingImage";

    /// <summary>
    /// 根据tid，获取视频分区图标
    /// </summary>
    /// <param name="tid"></param>
    /// <returns></returns>
    public static string GetZoneImageKey(int tid)
    {
        return tid switch
        {
            -10 => "Zone.cheeseDrawingImage",
            1 => "Zone.dougaDrawingImage",
            3 => "Zone.musicDrawingImage",
            4 => "Zone.gameDrawingImage",
            5 => "Zone.entDrawingImage",
            11 => "Zone.teleplayDrawingImage",
            13 => "Zone.animeDrawingImage",
            23 => "Zone.movieDrawingImage",
            36 => "Zone.techDrawingImage",
            119 => "Zone.kichikuDrawingImage",
            129 => "Zone.danceDrawingImage",
            155 => "Zone.fashionDrawingImage",
            160 => "Zone.lifeDrawingImage",
            167 => "Zone.guochuangDrawingImage",
            177 => "Zone.documentaryDrawingImage",
            181 => "Zone.cinephileDrawingImage",
            188 => "Zone.digitalDrawingImage",
            202 => "Zone.informationDrawingImage",
            211 => "Zone.foodDrawingImage",
            217 => "Zone.animalDrawingImage",
            223 => "Zone.carDrawingImage",
            234 => "Zone.sportsDrawingImage",
            _ => DefaultImageKey
        };
    }
}
