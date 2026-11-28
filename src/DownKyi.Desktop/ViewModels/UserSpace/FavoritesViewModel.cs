using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.CustomControl;
using DownKyi.Images;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.UserSpace;

internal sealed class FavoritesViewModel : ViewModelBase, IUserSpacePageLifetime
{
    private readonly ObservableCollection<FavoriteFolder> _favorites = [];
    private RelayCommand<object>? _favoritesCommand;
    public ReadOnlyObservableCollection<FavoriteFolder> Favorites { get; }
    public RelayCommand<object> FavoritesCommand => _favoritesCommand ??= RequiredParameterCommand.Create<object>(OpenFavoriteFolder);
    private void OpenFavoriteFolder(object parameter)
    {
        if (parameter is not FavoriteFolder folder) return;
        Navigation.Navigate(new AppNavigationRequest(AppRoute.PublicFavorites, AppRoute.UserSpace, folder.Id));
        SelectedItem = -1;
    }
    private readonly IUserSpaceLoadCoordinator _coordinator;
    private readonly ILogger<FavoritesViewModel> _logger;
    private CancellationTokenSource? _loadCancellation;
    private CancellationToken _pageLifetime;
    private long _mid;
    private bool _active;
    private bool _hasLoadedPage;
    public long Mid => _mid;
    public int Page => Pager.Current;
    public AppRoute Route { get; private set; }
    private bool _isLoading;
    private bool _isEmpty;
    private bool _hasError;
    private int _selectedItem = -1;
    private CustomPagerViewModel _pager = new(1, 1);
    private RelayCommand? _retryCommand;

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetProperty(ref _isLoading, value)) _retryCommand?.NotifyCanExecuteChanged(); }
    }
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }
    public int SelectedItem { get => _selectedItem; set => SetProperty(ref _selectedItem, value); }
    public CustomPagerViewModel Pager { get => _pager; private set => SetProperty(ref _pager, value); }
    public RelayCommand RetryCommand => _retryCommand ??= new RelayCommand(
        () => RunFireAndForget(LoadPageAsync(Pager.Current), nameof(LoadPageAsync), _logger), () => !IsLoading);

    public FavoritesViewModel(IDesktopInteractionContext desktopInteractions, IUserSpaceLoadCoordinator coordinator,
        ILogger<FavoritesViewModel> logger) : base(desktopInteractions)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Favorites = new ReadOnlyObservableCollection<FavoriteFolder>(_favorites);
    }

    public override void OnNavigatedTo(AppNavigationContext context)
    {
        base.OnNavigatedTo(context);
        _active = false;
        CancelAndDispose(ref _loadCancellation);
        Pager.PropertyChanged -= PagerChanged;
        Pager.CurrentChanging -= PagerChanging;
        _mid = context.Parameters.GetValue<long>("mid");
        Route = context.Route;
        _hasLoadedPage = false;
        HasError = false;
        var page = Math.Max(1, context.Parameters.GetValue<int>("page"));
        Pager = new CustomPagerViewModel(page, page);
        Pager.PropertyChanged += PagerChanged;
        Pager.CurrentChanging += PagerChanging;
        SelectedItem = -1;
        ResumePage(context.Parameters.GetValue<CancellationToken>("lifetime"));
    }

    public void ResumePage(CancellationToken lifetime)
    {
        _pageLifetime = lifetime;
        _active = _mid > 0;
        if (_active && !_hasLoadedPage && !HasError)
            RunFireAndForget(LoadPageAsync(Pager.Current), nameof(LoadPageAsync), _logger);
    }

    private void PagerChanging(object? sender, CancelEventArgs args) => args.Cancel = IsLoading;
    private void PagerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_active && args.PropertyName == nameof(CustomPagerViewModel.Current))
            RunFireAndForget(LoadPageAsync(Pager.Current), nameof(LoadPageAsync), _logger);
    }

    private async Task LoadPageAsync(int page)
    {
        var requestedMid = _mid;
        var token = ReplaceCancellationSource(ref _loadCancellation, _pageLifetime);
        try
        {
            token.ThrowIfCancellationRequested();
            IsLoading = true;
            _hasLoadedPage = false;
            IsEmpty = false;
            HasError = false;
            _favorites.Clear();
            var result = await _coordinator.LoadFavoriteFoldersPageAsync(requestedMid, page, 50, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (requestedMid != _mid) return;
            foreach (var folder in result.Folders)
                _favorites.Add(new FavoriteFolder(folder.Id, folder.Cover, NormalIcon.Current.Favorite,
                    folder.Title, folder.MediaCount, DateTimeOffset.FromUnixTimeSeconds(folder.UpdatedAtUnixSeconds)
                        .ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)));
            Pager.Count = result.PageCount;
            IsEmpty = _favorites.Count == 0;
            _hasLoadedPage = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            token.ThrowIfCancellationRequested();
            HasError = true;
            _logger.LogErrorMessage("User space section could not be loaded.", exception);
        }
        finally
        {
            if (_loadCancellation?.Token == token) IsLoading = false;
        }
    }

    public override void OnNavigatedFrom(AppNavigationContext context)
    {
        _active = false;
        CancelAndDispose(ref _loadCancellation);
        IsLoading = false;
        base.OnNavigatedFrom(context);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _active = false;
            CancelAndDispose(ref _loadCancellation);
            Pager.PropertyChanged -= PagerChanged;
            Pager.CurrentChanging -= PagerChanging;
        }
        base.Dispose(disposing);
    }
}
