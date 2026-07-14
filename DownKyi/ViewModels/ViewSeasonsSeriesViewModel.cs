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
using DownKyi.Commands;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.CustomControl;
using DownKyi.Events;
using DownKyi.Images;
using DownKyi.PrismExtension.Dialog;
using DownKyi.Services.Download;
using DownKyi.Services.UserSpace;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;
using Prism.Commands;
using Prism.Events;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels;

internal class ViewSeasonsSeriesViewModel : ViewModelBase
{
    public const string Tag = "PageSeasonsSeries";
    private const int VideoNumberInPage = 30;
    private const string PlaceholderCover = "avares://DownKyi/Resources/video-placeholder.png";

    private readonly IAddToDownloadServiceFactory _addToDownloadServiceFactory;
    private readonly ISeasonsSeriesCoordinator _coordinator;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private long _mid = -1;
    private long _id = -1;
    private SeasonsSeriesKind _kind;

    private string _pageName = Tag;

    public string PageName
    {
        get => _pageName;
        set => SetProperty(ref _pageName, value);
    }

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

    public ViewSeasonsSeriesViewModel(
        IEventAggregator eventAggregator,
        IDialogService dialogService,
        IAddToDownloadServiceFactory addToDownloadServiceFactory,
        ISeasonsSeriesCoordinator coordinator) : base(eventAggregator)
    {
        DialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _addToDownloadServiceFactory = addToDownloadServiceFactory
            ?? throw new ArgumentNullException(nameof(addToDownloadServiceFactory));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        ArrowBack = NavigationIcon.Instance().ArrowBack;
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");

        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");
    }

    private DelegateCommand? _backSpaceCommand;

    public DelegateCommand BackSpaceCommand => _backSpaceCommand ??= new DelegateCommand(ExecuteBackSpace);

    protected internal override void ExecuteBackSpace()
    {
        ArrowBack.Fill = DictionaryResource.GetColor("ColorText");
        CancelOperations();
        if (TryNavigateBack())
        {
            return;
        }

        EventAggregator.GetEvent<NavigationEvent>().Publish(new NavigationParam
        {
            ViewName = ParentView,
            ParentViewName = null,
            Parameter = null
        });
    }

    private DelegateCommand? _downloadManagerCommand;

    public DelegateCommand DownloadManagerCommand =>
        _downloadManagerCommand ??= new DelegateCommand(ExecuteDownloadManagerCommand);

    private void ExecuteDownloadManagerCommand()
    {
        EventAggregator.GetEvent<NavigationEvent>().Publish(new NavigationParam
        {
            ViewName = ViewDownloadManagerViewModel.Tag,
            ParentViewName = Tag,
            Parameter = null
        });
    }

    private DelegateCommand<object>? _selectAllCommand;

    public DelegateCommand<object> SelectAllCommand =>
        _selectAllCommand ??= new DelegateCommand<object>(ExecuteSelectAllCommand);

    private void ExecuteSelectAllCommand(object parameter)
    {
        foreach (var item in Medias)
        {
            item.IsSelected = IsSelectAll;
        }
    }

    private DelegateCommand<object>? _mediasCommand;

    public DelegateCommand<object> MediasCommand =>
        _mediasCommand ??= new DelegateCommand<object>(ExecuteMediasCommand);

    private void ExecuteMediasCommand(object parameter)
    {
        if (parameter is IList selectedMedia)
        {
            IsSelectAll = selectedMedia.Count == Medias.Count;
        }
    }

    private DownKyiAsyncDelegateCommand? _addToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddToDownloadCommand =>
        _addToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(true));

    private DownKyiAsyncDelegateCommand? _addAllToDownloadCommand;

    public DownKyiAsyncDelegateCommand AddAllToDownloadCommand =>
        _addAllToDownloadCommand ??= new DownKyiAsyncDelegateCommand(() => AddToDownloadAsync(false));

    private async Task AddToDownloadAsync(bool onlySelected)
    {
        var addToDownloadService = _addToDownloadServiceFactory.Create(PlayStreamType.Video);
        var directory = await addToDownloadService.SetDirectory(DialogService).ConfigureAwait(true);
        if (directory == null)
        {
            return;
        }

        var cancellationToken = ReplaceCancellationSource(ref _downloadCancellation);
        var items = Medias
            .Select(media => new SeasonsSeriesDownloadItem(media.Bvid, media.IsSelected))
            .ToArray();
        try
        {
            var addedCount = await _coordinator
                .AddToDownloadAsync(
                    addToDownloadService,
                    items,
                    onlySelected,
                    directory,
                    EventAggregator,
                    DialogService,
                    cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            EventAggregator.GetEvent<MessageEvent>().Publish(addedCount <= 0
                ? DictionaryResource.GetString("TipAddDownloadingZero")
                : $"{DictionaryResource.GetString("TipAddDownloadingFinished1")}{addedCount}{DictionaryResource.GetString("TipAddDownloadingFinished2")}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            LogManager.Error(Tag, e);
            EventAggregator.GetEvent<MessageEvent>().Publish(e.Message);
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
            var archives = await _coordinator
                .LoadPageAsync(_mid, _id, _kind, current, VideoNumberInPage, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (_loadCancellation?.Token != cancellationToken)
            {
                return;
            }

            if (archives.Count == 0)
            {
                LoadingVisibility = false;
                NoDataVisibility = true;
                return;
            }

            Medias.AddRange(archives.Select(CreateMedia));
            LoadingVisibility = false;
            NoDataVisibility = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException or FormatException or Newtonsoft.Json.JsonException)
        {
            LogManager.Error(Tag, e);
            if (_loadCancellation?.Token == cancellationToken)
            {
                LoadingVisibility = false;
                NoDataVisibility = true;
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

        return new ChannelMedia(EventAggregator)
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

    private void OnCountChangedPager(object? sender, EventArgs e)
    {
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
            nameof(UpdatePageAsync));
    }

    private void ReplacePager(CustomPagerViewModel pager)
    {
        if (Pager != null)
        {
            Pager.CurrentChanging -= OnCurrentChangedPager;
            Pager.CountChanged -= OnCountChangedPager;
        }

        Pager = pager;
        Pager.CurrentChanging += OnCurrentChangedPager;
        Pager.CountChanged += OnCountChangedPager;
    }

    public override void OnNavigatedTo(NavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");

        var parameter = navigationContext.Parameters.GetValue<Dictionary<string, object>>("Parameter");
        if (parameter == null)
        {
            return;
        }

        CancelOperations();
        IsEnabled = true;
        Medias.Clear();
        IsSelectAll = false;
        _mid = (long)parameter["mid"];
        _id = (long)parameter["id"];
        _kind = (SeasonsSeriesKind)(int)parameter["type"];
        Title = (string)parameter["name"];
        var count = (int)parameter["count"];

        ReplacePager(new CustomPagerViewModel(
            1,
            (int)Math.Ceiling((double)count / VideoNumberInPage)));
        Pager.Current = 1;
    }

    public override void OnNavigatedFrom(NavigationContext navigationContext)
    {
        CancelOperations();
        IsEnabled = true;
        LoadingVisibility = false;
        base.OnNavigatedFrom(navigationContext);
    }

    private void CancelOperations()
    {
        CancelAndDispose(ref _loadCancellation);
        CancelAndDispose(ref _downloadCancellation);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            CancelOperations();
            if (Pager != null)
            {
                Pager.CurrentChanging -= OnCurrentChangedPager;
                Pager.CountChanged -= OnCountChangedPager;
            }
        }

        base.Dispose(disposing);
    }
}
