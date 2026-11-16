using System.Collections.Generic;
using DownKyi.Images;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using DownKyi.ViewModels.UserSpace;

namespace DownKyi.ViewModels;

internal partial class UserSpaceViewModel
{
    private void AddFavoriteFolders(IReadOnlyList<UserSpaceFavoriteFolder> folders)
    {
        if (folders.Count == 0)
        {
            return;
        }

        TabLeftBanners.Add(new TabLeftBanner
        {
            NavigationData = folders,
            Id = 3,
            Icon = NormalIcon.Current.FavoriteOutline,
            IconColor = "#FFFF6699",
            Title = DictionaryResource.GetString("PublicFavorites")
        });
    }
}
