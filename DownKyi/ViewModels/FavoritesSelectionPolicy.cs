using System.Collections.Generic;
using System.Linq;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Services.Media;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.ViewModels;

internal static class FavoritesSelectionPolicy
{
    public static void SetAllAvailableSelected(
        IEnumerable<FavoritesMedia> medias,
        bool selected)
    {
        foreach (var media in medias.Where(media => !media.IsUnavailable))
        {
            media.IsSelected = selected;
        }
    }

    public static bool AreAllAvailableSelected(IEnumerable<FavoritesMedia> medias)
    {
        var available = medias.Where(media => !media.IsUnavailable).ToArray();
        return available.Length > 0 && available.All(media => media.IsSelected);
    }

    public static IReadOnlyList<ContentDownloadItem> CreateDownloadItems(
        IEnumerable<FavoritesMedia> medias)
    {
        return medias
            .Where(media => !media.IsUnavailable)
            .Select(media => new ContentDownloadItem(
                media.Bvid,
                DownloadInfoKind.Video,
                media.IsSelected))
            .ToArray();
    }
}
