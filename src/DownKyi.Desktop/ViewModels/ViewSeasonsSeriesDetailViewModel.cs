using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.Images;
using DownKyi.Presentation;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class ViewSeasonsSeriesDetailViewModel : ViewModelBase
{
    private const int VideoNumberInPage = 30;
    private const string PlaceholderCover = "avares://DownKyi.Desktop/Resources/video-placeholder.png";

    private readonly ISeasonsSeriesCoordinator _coordinator;
    private readonly ILogger<ViewSeasonsSeriesDetailViewModel> _logger;
    private readonly SemaphoreSlim _pageLoadGate = new(1, 1);
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private bool _isLoadingPage;
    private bool _hasMorePages = true;
    private int _nextPage = 1;
    private int _totalCount;
    private int _loadVersion;

    public DownKyiAsyncCommandGate DownloadCommandGate { get; } = new();
    private long _mid = -1;
    private long _id = -1;
    private SeasonsSeriesKind _kind;

    private bool _loading = true;

    public bool Loading
    {
        get => _loading;
        set => SetProperty(ref _loading, value);
    }

    private bool _loadingVisibility;

    public bool LoadingVisibility
    {
        get => _loadingVisibility;
        set => SetProperty(ref _loadingVisibility, value);
    }

    private bool _loadingMoreVisibility;

    public bool LoadingMoreVisibility
    {
        get => _loadingMoreVisibility;
        set => SetProperty(ref _loadingMoreVisibility, value);
    }

    private string _downloadPreparationProgressText = string.Empty;

    public string DownloadPreparationProgressText
    {
        get => _downloadPreparationProgressText;
        set => SetProperty(ref _downloadPreparationProgressText, value);
    }

    private bool _noDataVisibility;

    public bool NoDataVisibility
    {
        get => _noDataVisibility;
        set => SetProperty(ref _noDataVisibility, value);
    }

    private VectorImage _arrowBack = null!;

    public VectorImage ArrowBack
    {
        get => _arrowBack;
        set => SetProperty(ref _arrowBack, value);
    }

    private VectorImage _downloadManage = null!;

    public VectorImage DownloadManage
    {
        get => _downloadManage;
        set => SetProperty(ref _downloadManage, value);
    }

    private string _title = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private RangeObservableCollection<ChannelMedia> _medias = new();

    public RangeObservableCollection<ChannelMedia> Medias
    {
        get => _medias;
        private set => SetProperty(ref _medias, value);
    }

    private bool _isSelectAll;

    public bool IsSelectAll
    {
        get => _isSelectAll;
        set => SetProperty(ref _isSelectAll, value);
    }

    public ViewSeasonsSeriesDetailViewModel(
        IDesktopInteractionContext desktopInteractions,
        ISeasonsSeriesCoordinator coordinator,
        ILogger<ViewSeasonsSeriesDetailViewModel> logger) : base(desktopInteractions)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ArrowBack = NavigationIcon.CreateArrowBack();

        DownloadManage = ButtonIcon.DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
    }

    private RelayCommand? _backSpaceCommand;

    public RelayCommand BackSpaceCommand => _backSpaceCommand ??= new RelayCommand(ExecuteBackSpace);

    protected internal override void ExecuteBackSpace()
    {
        CancelOperations();
        if (TryNavigateBack())
        {
            return;
        }

        NavigateToParent();
    }

    private RelayCommand? _downloadManagerCommand;

    public RelayCommand DownloadManagerCommand =>
        _downloadManagerCommand ??= new RelayCommand(ExecuteDownloadManagerCommand);

    private void ExecuteDownloadManagerCommand()
    {
        Navigation.Navigate(new AppNavigationRequest(
            AppRoute.DownloadManager,
            AppRoute.SeasonsSeries));
    }

    private RelayCommand<object>? _selectAllCommand;

    public RelayCommand<object> SelectAllCommand =>
        _selectAllCommand ??= RequiredParameterCommand.Create<object>(ExecuteSelectAllCommand);

    private void ExecuteSelectAllCommand(object parameter)
    {
        foreach (var item in Medias)
        {
            item.IsSelected = IsSelectAll;
        }
    }

    private RelayCommand<object>? _mediasCommand;

    public RelayCommand<object> MediasCommand =>
        _mediasCommand ??= RequiredParameterCommand.Create<object>(ExecuteMediasCommand);

    private void ExecuteMediasCommand(object parameter)
    {
        if (parameter is IList selectedMedia)
        {
            IsSelectAll = selectedMedia.Count == Medias.Count;
        }
    }

    private DownKyiAsyncDelegateCommand? _addToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddToDownloadCommand =>
        _addToDownloadCommand ??= new DownKyiAsyncDelegateCommand(
            () => AddToDownloadAsync(true),
            _logger,
            executionGate: DownloadCommandGate,
            executionRejected: ShowDownloadPreparationConflict);

    private DownKyiAsyncDelegateCommand? _addAllToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddAllToDownloadCommand =>
        _addAllToDownloadCommand ??= new DownKyiAsyncDelegateCommand(
            AddAllToDownloadAsync,
            _logger,
            executionGate: DownloadCommandGate,
            executionRejected: ShowDownloadPreparationConflict);

    private RelayCommand? _cancelDownloadPreparationCommand;

    public RelayCommand CancelDownloadPreparationCommand =>
        _cancelDownloadPreparationCommand ??= new RelayCommand(() => _downloadCancellation?.Cancel());

    private void ShowDownloadPreparationConflict()
    {
        Notifications.Show(DictionaryResource.GetString("TipDownloadPreparationAlreadyRunning"));
    }

    private DownKyiAsyncDelegateCommand? _loadMoreCommand;

    public DownKyiAsyncDelegateCommand LoadMoreCommand =>
        _loadMoreCommand ??= new DownKyiAsyncDelegateCommand(
            LoadMoreAsync,
            _logger,
            CanLoadMore);

    private bool CanLoadMore() =>
        _loadCancellation != null && !_isLoadingPage && _hasMorePages;

    private Task LoadMoreAsync()
    {
        var cancellation = _loadCancellation;
        return cancellation == null
            ? Task.CompletedTask
            : LoadNextPageAsync(
                reset: false,
                Volatile.Read(ref _loadVersion),
                cancellation.Token);
    }

    private async Task AddToDownloadAsync(bool onlySelected)
    {
        var cancellationToken = ReplaceCancellationSource(ref _downloadCancellation);
        var items = Medias
            .Select(media => new SeasonsSeriesDownloadItem(media.Bvid, media.IsSelected))
            .ToArray();
        try
        {
            var result = await _coordinator
                .AddToDownloadAsync(
                    items,
                    onlySelected,
                    cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (result == null)
            {
                return;
            }

            Notifications.Show(ContentDownloadNotificationFormatter.Format(result.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Season or series download preparation failed.", e);
            Notifications.Show(e.Message);
        }
        finally
        {
            ReleaseCancellationSource(ref _downloadCancellation, cancellationToken);
        }
    }

    private async Task AddAllToDownloadAsync()
    {
        var cancellationToken = ReplaceCancellationSource(ref _downloadCancellation);
        DownloadPreparationProgressText = FormatCollectionDiscoveryProgress();
        try
        {
            var pageCancellation = _loadCancellation;
            if (pageCancellation == null)
            {
                return;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                pageCancellation.Token);
            var loadVersion = Volatile.Read(ref _loadVersion);
            while (_hasMorePages)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                var loaded = await LoadNextPageAsync(
                    reset: false,
                    loadVersion,
                    linkedCancellation.Token,
                    showLoadingMore: false).ConfigureAwait(true);
                if (!loaded && _hasMorePages)
                {
                    return;
                }

                DownloadPreparationProgressText = FormatCollectionDiscoveryProgress();
            }

            DownloadPreparationProgressText = DictionaryResource
                .GetString("PreparingCollectionDownloads")
                .Replace(
                    "{0}",
                    Medias.Count.ToString(CultureInfo.CurrentCulture),
                    StringComparison.Ordinal);
            var items = Medias
                .Select(media => new SeasonsSeriesDownloadItem(media.Bvid, media.IsSelected))
                .ToArray();
            var progress = new Progress<ContentDownloadBatchProgress>(ApplyDownloadPreparationProgress);
            var result = await _coordinator
                .AddAllToDownloadIncrementallyAsync(
                    items,
                    progress,
                    cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != null)
            {
                Notifications.Show(ContentDownloadNotificationFormatter.Format(result.Value));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Season or series download preparation failed.", e);
            Notifications.Show(e.Message);
        }
        finally
        {
            DownloadPreparationProgressText = string.Empty;
            ReleaseCancellationSource(ref _downloadCancellation, cancellationToken);
        }
    }

    private string FormatCollectionDiscoveryProgress()
    {
        var total = Math.Max(_totalCount, Medias.Count);
        return DictionaryResource
            .GetString("DiscoveringCollectionVideos")
            .Replace(
                "{0}",
                Medias.Count.ToString(CultureInfo.CurrentCulture),
                StringComparison.Ordinal)
            .Replace(
                "{1}",
                total.ToString(CultureInfo.CurrentCulture),
                StringComparison.Ordinal);
    }

    private void ApplyDownloadPreparationProgress(ContentDownloadBatchProgress progress)
    {
        DownloadPreparationProgressText = DictionaryResource
            .GetString("AddingCollectionDownloads")
            .Replace(
                "{0}",
                progress.ProcessedCount.ToString(CultureInfo.CurrentCulture),
                StringComparison.Ordinal)
            .Replace(
                "{1}",
                progress.TotalCount.ToString(CultureInfo.CurrentCulture),
                StringComparison.Ordinal);
    }

    private async Task<bool> LoadNextPageAsync(
        bool reset,
        int loadVersion,
        CancellationToken cancellationToken,
        bool showLoadingMore = true)
    {
        var lockTaken = false;
        try
        {
            await _pageLoadGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            lockTaken = true;
            if (loadVersion != Volatile.Read(ref _loadVersion)
                || (!reset && !_hasMorePages))
            {
                return false;
            }

            _isLoadingPage = true;
            _loadMoreCommand?.NotifyCanExecuteChanged();
            LoadingVisibility = reset;
            LoadingMoreVisibility = !reset && showLoadingMore;
            if (reset)
            {
                NoDataVisibility = false;
            }

            var requestedPage = _nextPage;
            var page = await _coordinator
                .LoadPageAsync(
                    _mid,
                    _id,
                    _kind,
                    requestedPage,
                    VideoNumberInPage,
                    cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (loadVersion != Volatile.Read(ref _loadVersion))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(page.Title))
            {
                Title = page.Title;
            }

            var newMedia = page.Archives.Select(CreateMedia).ToArray();
            if (reset)
            {
                Medias.ReplaceRange(newMedia);
            }
            else
            {
                Medias.AddRange(newMedia);
            }

            _nextPage = requestedPage + 1;
            _totalCount = Math.Max(page.TotalCount, Medias.Count);
            _hasMorePages = newMedia.Length > 0 && Medias.Count < page.TotalCount;
            IsSelectAll = Medias.Count > 0 && Medias.All(media => media.IsSelected);
            NoDataVisibility = Medias.Count == 0;
            if (DownloadCommandGate.IsExecuting)
            {
                DownloadPreparationProgressText = FormatCollectionDiscoveryProgress();
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Season or series page loading failed.", e);
            if (loadVersion == Volatile.Read(ref _loadVersion))
            {
                if (reset)
                {
                    NoDataVisibility = Medias.Count == 0;
                }

                Notifications.Show(e.Message);
            }

            return false;
        }
        finally
        {
            if (lockTaken)
            {
                if (loadVersion == Volatile.Read(ref _loadVersion))
                {
                    LoadingVisibility = false;
                    LoadingMoreVisibility = false;
                    _isLoadingPage = false;
                    _loadMoreCommand?.NotifyCanExecuteChanged();
                }

                if (!IsDisposed)
                {
                    _pageLoadGate.Release();
                }
            }
        }
    }

    private ChannelMedia CreateMedia(SpaceSeasonsSeriesArchives video)
    {
        var cover = string.IsNullOrWhiteSpace(video.Pic)
            ? PlaceholderCover
            : video.Pic.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? video.Pic
                : $"https:{video.Pic}";
        var play = video.Stat?.View > 0 ? Format.FormatNumber(video.Stat.View) : "--";
        var createTime = DateTimeOffset
            .FromUnixTimeSeconds(video.Ctime)
            .ToLocalTime()
            .ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

        return new ChannelMedia(Navigation, AppRoute.SeasonsSeries)
        {
            Avid = video.Aid,
            Bvid = video.Bvid,
            Cover = cover,
            Duration = Format.FormatDuration3(video.Duration),
            Title = video.Title,
            PlayNumber = play,
            CreateTime = createTime
        };
    }

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        DownloadManage = ButtonIcon.DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;

        if (navigationContext.Parameter is SeriesNavigationPayload series)
        {
            InitializePage(
                series.Mid,
                series.SeriesId,
                SeasonsSeriesKind.Series);
            return;
        }

        if (navigationContext.Parameter is SeasonNavigationPayload season)
        {
            InitializePage(
                season.Mid,
                season.SeasonId,
                SeasonsSeriesKind.Season);
            return;
        }

        _hasMorePages = false;
        NoDataVisibility = true;
        _loadMoreCommand?.NotifyCanExecuteChanged();
    }

    private void InitializePage(
        long mid,
        long id,
        SeasonsSeriesKind kind)
    {
        CancelOperations();
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        var loadVersion = Interlocked.Increment(ref _loadVersion);
        _isLoadingPage = false;
        _hasMorePages = true;
        _nextPage = 1;
        _totalCount = 0;
        Medias.Clear();
        IsSelectAll = false;
        LoadingVisibility = false;
        LoadingMoreVisibility = false;
        DownloadPreparationProgressText = string.Empty;
        NoDataVisibility = false;
        _mid = mid;
        _id = id;
        _kind = kind;
        Title = string.Empty;

        RunFireAndForget(
            LoadNextPageAsync(reset: true, loadVersion, cancellationToken),
            nameof(LoadNextPageAsync),
            _logger);
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        CancelOperations();
        _isLoadingPage = false;
        LoadingVisibility = false;
        LoadingMoreVisibility = false;
        base.OnNavigatedFrom(navigationContext);
    }

    private void CancelOperations()
    {
        Interlocked.Increment(ref _loadVersion);
        CancelAndDispose(ref _loadCancellation);
        _loadMoreCommand?.NotifyCanExecuteChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelOperations();
            _pageLoadGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
