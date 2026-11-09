using System.Collections.Generic;
using System.Linq;
using DownKyi.Presentation;
using DownKyi.Services.Media;

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
            .Select(media => ContentDownloadItem.TryCreate(media.Bvid, media.IsSelected))
            .OfType<ContentDownloadItem>()
            .ToArray();
    }
}
