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

internal sealed class UserSpaceSeasonsSeriesViewModel : ViewModelBase, IUserSpacePageLifetime
{
    public const string Tag = "PageUserSpaceSeasonsSeries";
    public ObservableCollection<SeasonsSeries> SeasonsSeries { get; } = [];
    private RelayCommand<object>? _seasonsSeriesCommand;
    public RelayCommand<object> SeasonsSeriesCommand => _seasonsSeriesCommand ??= RequiredParameterCommand.Create<object>(OpenCollection);
    private void OpenCollection(object parameter)
    {
        if (parameter is not SeasonsSeries item) return;
        object payload = item.TypeImage == NormalIcon.Current.SeasonsSeries
            ? new SeasonNavigationPayload(_mid, item.Id) : new SeriesNavigationPayload(_mid, item.Id);
        Navigation.Navigate(new AppNavigationRequest(AppRoute.SeasonsSeries, AppRoute.UserSpace, payload));
        SelectedItem = -1;
    }
    private readonly IUserSpaceLoadCoordinator _coordinator;
    private readonly ILogger<UserSpaceSeasonsSeriesViewModel> _logger;
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

    public UserSpaceSeasonsSeriesViewModel(IDesktopInteractionContext desktopInteractions, IUserSpaceLoadCoordinator coordinator,
        ILogger<UserSpaceSeasonsSeriesViewModel> logger) : base(desktopInteractions)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            SeasonsSeries.Clear();
            var result = await _coordinator.LoadSeasonsSeriesPageAsync(requestedMid, page, 20, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (requestedMid != _mid) return;
            foreach (var item in result.Items.SeasonsList)
            {
                if (item.Meta.Total <= 0) continue;
                SeasonsSeries.Add(new SeasonsSeries
                {
                    Id = item.Meta.SeasonId,
                    Cover = string.IsNullOrEmpty(item.Meta.Cover) ? "avares://DownKyi.Desktop/Resources/video-placeholder.png" : item.Meta.Cover,
                    TypeImage = NormalIcon.Current.SeasonsSeries,
                    Name = item.Meta.Name,
                    Count = item.Meta.Total,
                    Ctime = DateTimeOffset.FromUnixTimeSeconds(item.Meta.Ptime).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)
                });
            }
            foreach (var item in result.Items.SeriesList)
            {
                if (item.Meta.Total <= 0) continue;
                SeasonsSeries.Add(new SeasonsSeries
                {
                    Id = item.Meta.SeriesId,
                    Cover = string.IsNullOrEmpty(item.Meta.Cover) ? "avares://DownKyi.Desktop/Resources/video-placeholder.png" : item.Meta.Cover,
                    TypeImage = NormalIcon.Current.Channel1,
                    Name = item.Meta.Name,
                    Count = item.Meta.Total,
                    Ctime = DateTimeOffset.FromUnixTimeSeconds(item.Meta.Mtime).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)
                });
            }
            Pager.Count = result.PageCount;
            IsEmpty = SeasonsSeries.Count == 0;
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
