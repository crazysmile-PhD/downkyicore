using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
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
using DownKyi.CustomControl;
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
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _downloadCancellation;

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

    private bool _isEnabled = true;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    private CustomPagerViewModel _pager = null!;

    public CustomPagerViewModel Pager
    {
        get => _pager;
        set => SetProperty(ref _pager, value);
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
            () => AddToDownloadAsync(false),
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

    private async Task UpdatePageAsync(int current)
    {
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        IsEnabled = false;
        Medias.Clear();
        IsSelectAll = false;
        LoadingVisibility = true;
        NoDataVisibility = false;

        try
        {
            var page = await _coordinator
                .LoadPageAsync(_mid, _id, _kind, current, VideoNumberInPage, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_loadCancellation?.Token != cancellationToken)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(page.Title))
            {
                Title = page.Title;
            }

            Pager.Count = Math.Max(
                current,
                (int)Math.Ceiling((double)page.TotalCount / VideoNumberInPage));
            if (page.Archives.Count == 0)
            {
                LoadingVisibility = false;
                NoDataVisibility = true;
                return;
            }

            Medias.AddRange(page.Archives.Select(CreateMedia));
            LoadingVisibility = false;
            NoDataVisibility = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            _logger.LogErrorMessage("Season or series page loading failed.", e);
            if (_loadCancellation?.Token == cancellationToken)
            {
                LoadingVisibility = false;
                NoDataVisibility = true;
                Notifications.Show(e.Message);
            }
        }
        finally
        {
            if (_loadCancellation?.Token == cancellationToken)
            {
                IsEnabled = true;
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

    private void OnCurrentChangedPager(object? sender, CancelEventArgs e)
    {
        if (!IsEnabled)
        {
            e.Cancel = true;
            return;
        }

        RunFireAndForget(
            UpdatePageAsync(((CustomPagerViewModel)sender!).ProposedCurrent),
            nameof(UpdatePageAsync),
            _logger);
    }

    private void ReplacePager(CustomPagerViewModel pager)
    {
        if (Pager != null)
        {
            Pager.CurrentChanging -= OnCurrentChangedPager;
        }

        Pager = pager;
        Pager.CurrentChanging += OnCurrentChangedPager;
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

        NoDataVisibility = true;
    }

    private void InitializePage(
        long mid,
        long id,
        SeasonsSeriesKind kind)
    {
        CancelOperations();
        IsEnabled = true;
        Medias.Clear();
        IsSelectAll = false;
        NoDataVisibility = false;
        _mid = mid;
        _id = id;
        _kind = kind;
        Title = string.Empty;

        ReplacePager(new CustomPagerViewModel(1, 1));
        Pager.Current = 1;
    }

    public override void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
        CancelOperations();
        IsEnabled = true;
        LoadingVisibility = false;
        base.OnNavigatedFrom(navigationContext);
    }

    private void CancelOperations()
    {
        CancelAndDispose(ref _loadCancellation);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelOperations();
            if (Pager != null)
            {
                Pager.CurrentChanging -= OnCurrentChangedPager;
            }
        }

        base.Dispose(disposing);
    }
}
