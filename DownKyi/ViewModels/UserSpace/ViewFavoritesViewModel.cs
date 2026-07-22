using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Images;
using DownKyi.Services.UserSpace;

namespace DownKyi.ViewModels.UserSpace;

internal sealed class ViewFavoritesViewModel : ViewModelBase
{
    private readonly ObservableCollection<FavoriteFolder> _favorites = [];
    private RelayCommand<object>? _favoritesCommand;
    private int _selectedItem = -1;

    public ViewFavoritesViewModel(IDesktopInteractionContext desktopInteractions)
        : base(desktopInteractions)
    {
        Favorites = new ReadOnlyObservableCollection<FavoriteFolder>(_favorites);
    }

    public ReadOnlyObservableCollection<FavoriteFolder> Favorites { get; }

    public int SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public RelayCommand<object> FavoritesCommand =>
        _favoritesCommand ??= RequiredParameterCommand.Create<object>(OpenFavoriteFolder);

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        _favorites.Clear();
        SelectedItem = -1;
        var folders = navigationContext.Parameters
            .GetValue<IReadOnlyList<UserSpaceFavoriteFolder>>("object");
        if (folders == null)
        {
            return;
        }

        foreach (var folder in folders)
        {
            _favorites.Add(new FavoriteFolder(
                folder.Id,
                folder.Cover,
                NormalIcon.Instance().Favorite,
                folder.Title,
                folder.MediaCount,
                DateTimeOffset.FromUnixTimeSeconds(folder.UpdatedAtUnixSeconds)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)));
        }
    }

    private void OpenFavoriteFolder(object parameter)
    {
        if (parameter is not FavoriteFolder folder)
        {
            return;
        }

        Navigation.Navigate(new AppNavigationRequest(
            AppRoute.PublicFavorites,
            AppRoute.UserSpace,
            folder.Id));
        SelectedItem = -1;
    }
}
