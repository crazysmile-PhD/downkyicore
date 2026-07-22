using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.Logging;
using DownKyi.CustomControl;
using DownKyi.Images;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.ViewModels;

internal partial class ViewPublicationViewModel
{
    private string _inputSearchText = string.Empty;
    private string _activeSearchText = string.Empty;
    private PublicationNavigationPayload? _loadedPayload;
    private bool _hasLoadedPage;
    private bool _suppressTabSelection;
    private RelayCommand? _searchCommand;

    public string InputSearchText
    {
        get => _inputSearchText;
        set => SetProperty(ref _inputSearchText, value);
    }

    public RelayCommand SearchCommand => _searchCommand ??= new RelayCommand(ExecuteSearch, () => IsEnabled);

    private void ExecuteSearch()
    {
        var query = InputSearchText.Trim();
        _activeSearchText = query;
        ConfigurePager(1, string.IsNullOrEmpty(query) ? GetSelectedTabPageCount() : 1);
    }

    private void SelectPublicationType(TabHeader tabHeader)
    {
        InputSearchText = string.Empty;
        _activeSearchText = string.Empty;
        ConfigurePager(1, GetPageCount(tabHeader));
    }

    private void ConfigurePager(int current, int count)
    {
        _hasLoadedPage = false;
        Pager = new CustomPagerViewModel(current, Math.Max(1, count));
        Pager.Current = current;
    }

    private int GetSelectedTabPageCount()
    {
        return SelectTabId >= 0 && SelectTabId < TabHeaders.Count
            ? GetPageCount(TabHeaders[SelectTabId])
            : 1;
    }

    private static int GetPageCount(TabHeader tabHeader)
    {
        return int.TryParse(tabHeader.SubTitle, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count)
            ? Math.Max(1, (int)Math.Ceiling((double)count / VideoNumberInPage))
            : 1;
    }

    private async Task UpdatePublicationAsync(int current)
    {
        if (SelectTabId < 0 || SelectTabId >= TabHeaders.Count)
        {
            return;
        }

        IsEnabled = false;
        SearchCommand.NotifyCanExecuteChanged();
        LoadingVisibility = true;
        NoDataVisibility = false;
        _hasLoadedPage = false;
        var cancellationToken = ReplaceCancellationSource(ref _loadCancellation);
        var tab = TabHeaders[SelectTabId];
        try
        {
            var page = await _userSpaceCoordinator.LoadPublicationPageAsync(
                _mid,
                current,
                VideoNumberInPage,
                tab.Id,
                _activeSearchText,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            Medias.Clear();
            Medias.AddRange(page.Medias);
            IsSelectAll = false;
            LoadingVisibility = false;
            NoDataVisibility = page.Medias.Count == 0;
            _hasLoadedPage = true;

            var pageCount = Math.Max(1, (int)Math.Ceiling((double)page.TotalCount / VideoNumberInPage));
            Pager.Count = Math.Max(current, pageCount);
            if (string.IsNullOrEmpty(_activeSearchText))
            {
                tab.SubTitle = page.TotalCount.ToString(CultureInfo.CurrentCulture);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            LoadingVisibility = false;
            NoDataVisibility = Medias.Count == 0;
            _logger.LogErrorMessage("Publication page loading failed.", e);
        }
        finally
        {
            IsEnabled = true;
            SearchCommand.NotifyCanExecuteChanged();
        }
    }

    private void InitView()
    {
        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
        DownloadManage = ButtonIcon.Instance().DownloadManage;
        DownloadManage.Height = 24;
        DownloadManage.Width = 24;
        DownloadManage.Fill = DictionaryResource.GetColor("ColorPrimary");
        TabHeaders.Clear();
        Medias.Clear();
        SelectTabId = -1;
        IsSelectAll = false;
        InputSearchText = string.Empty;
        _activeSearchText = string.Empty;
        _hasLoadedPage = false;
    }

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);

        if (navigationContext.Parameter is not PublicationNavigationPayload payload)
        {
            NoDataVisibility = true;
            return;
        }

        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
        if (Equals(_loadedPayload, payload))
        {
            if (!_hasLoadedPage)
            {
                RunFireAndForget(UpdatePublicationAsync(Pager.Current), nameof(UpdatePublicationAsync), _logger);
            }

            return;
        }

        InitView();
        _loadedPayload = payload;
        _mid = payload.Mid;
        if (payload.Zones.Count == 0)
        {
            TabHeaders.Add(new TabHeader
            {
                Id = 0,
                Title = DictionaryResource.GetString("AllPublicationZones"),
                SubTitle = "0"
            });
        }
        else
        {
            foreach (var zone in payload.Zones)
            {
                TabHeaders.Add(new TabHeader
                {
                    Id = zone.TypeId,
                    Title = zone.Name,
                    SubTitle = zone.Count.ToString(CultureInfo.CurrentCulture)
                });
            }
        }

        var selectedTab = TabHeaders.FirstOrDefault(item => item.Id == payload.SelectedTypeId);
        if (selectedTab == null)
        {
            NoDataVisibility = true;
            return;
        }

        _suppressTabSelection = true;
        try
        {
            SelectTabId = TabHeaders.IndexOf(selectedTab);
        }
        finally
        {
            _suppressTabSelection = false;
        }

        ConfigurePager(1, GetPageCount(selectedTab));
    }
}
