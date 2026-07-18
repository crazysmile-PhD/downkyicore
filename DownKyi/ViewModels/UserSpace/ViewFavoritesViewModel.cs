using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using DownKyi.Core.BiliApi.Favorites.Models;
using DownKyi.Images;
using DownKyi.Utils;
using Prism.Commands;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels.UserSpace;

/// <summary>
/// 用户公开收藏夹列表。
/// </summary>
internal class ViewFavoritesViewModel : ViewModelBase
{
    public const string Tag = "PageUserSpaceFavorites";

    private ObservableCollection<FavoriteFolder> _favorites = new();

    public ObservableCollection<FavoriteFolder> Favorites
    {
        get => _favorites;
        private set => SetProperty(ref _favorites, value);
    }

    private int _selectedItem = -1;

    public int SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public ViewFavoritesViewModel(IEventAggregator eventAggregator) : base(eventAggregator)
    {
        Favorites = new ObservableCollection<FavoriteFolder>();
    }

    private DelegateCommand<object>? _favoritesCommand;

    public DelegateCommand<object> FavoritesCommand =>
        _favoritesCommand ??= new DelegateCommand<object>(ExecuteFavoritesCommand);

    private void ExecuteFavoritesCommand(object parameter)
    {
        if (parameter is not FavoriteFolder favorite)
        {
            return;
        }

        NavigateToView.NavigationView(
            EventAggregator,
            ViewPublicFavoritesViewModel.Tag,
            ViewUserSpaceViewModel.Tag,
            favorite.Id);
        SelectedItem = -1;
    }

    public override void OnNavigatedFrom(NavigationContext navigationContext)
    {
        base.OnNavigatedFrom(navigationContext);
        Favorites.Clear();
        SelectedItem = -1;
    }

    public override void OnNavigatedTo(NavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        Favorites.Clear();
        SelectedItem = -1;

        var parameter = navigationContext.Parameters.GetValue<IReadOnlyList<FavoritesMetaInfo>>("object");
        if (parameter == null)
        {
            return;
        }

        foreach (var item in parameter)
        {
            if (item.MediaCount <= 0)
            {
                continue;
            }

            var cover = string.IsNullOrWhiteSpace(item.Cover)
                ? "avares://DownKyi/Resources/video-placeholder.png"
                : item.Cover;
            var updatedAt = DateTimeOffset.FromUnixTimeSeconds(item.Mtime)
                .ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

            Favorites.Add(new FavoriteFolder
            {
                Id = item.Id,
                Cover = cover,
                TypeImage = NormalIcon.Instance().Favorite,
                Title = item.Title,
                Count = item.MediaCount,
                UpdatedAt = updatedAt
            });
        }
    }
}
