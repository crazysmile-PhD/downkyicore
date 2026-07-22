using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.Logging;
using DownKyi.CustomControl;
using DownKyi.Images;
using DownKyi.Utils;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.ViewModels;

internal partial class ViewMyFavoritesViewModel
{
    private string _inputSearchText = string.Empty;
    private string _activeSearchText = string.Empty;
    private long _loadedMid = -1;
    private bool _foldersLoaded;
    private bool _hasLoadedMediaPage;
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
        ConfigurePager(1, string.IsNullOrEmpty(query) ? GetSelectedFolderPageCount() : 1);
    }

    private void SelectFavoritesFolder(TabHeader tabHeader)
    {
        MediaContentVisibility = false;
        InputSearchText = string.Empty;
        _activeSearchText = string.Empty;
        ConfigurePager(1, GetPageCount(tabHeader));
    }

    private void ConfigurePager(int current, int count)
    {
        _hasLoadedMediaPage = false;
        Pager = new CustomPagerViewModel(current, Math.Max(1, count));
        Pager.Current = current;
    }

    private int GetSelectedFolderPageCount()
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

    private async Task UpdateFavoritesMediaListAsync(int current)
    {
        if (SelectTabId < 0 || SelectTabId >= TabHeaders.Count)
        {
            return;
        }

        var cancellationToken = ReplaceCancellationSource(ref _mediaLoadCancellation);
        try
        {
            IsSelectAll = false;
            MediaLoadingVisibility = true;
            MediaNoDataVisibility = false;
            IsEnabled = false;
            SearchCommand.NotifyCanExecuteChanged();
            _hasLoadedMediaPage = false;

            var tab = TabHeaders[SelectTabId];
            var page = await _favoritesCoordinator.LoadMediaPageAsync(
                tab.Id,
                current,
                VideoNumberInPage,
                _activeSearchText,
                cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();

            Medias.Clear();
            Medias.AddRange(page.Medias);
            MediaContentVisibility = true;
            MediaLoadingVisibility = false;
            MediaNoDataVisibility = page.Medias.Count == 0;
            _hasLoadedMediaPage = true;
            if (!string.IsNullOrEmpty(_activeSearchText))
            {
                Pager.Count = current + (page.HasMore ? 1 : 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            MediaLoadingVisibility = false;
            MediaNoDataVisibility = Medias.Count == 0;
            _logger.LogErrorMessage("Favorites media loading failed.", e);
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
        ContentVisibility = false;
        LoadingVisibility = true;
        NoDataVisibility = false;
        MediaLoadingVisibility = false;
        MediaNoDataVisibility = false;
        TabHeaders.Clear();
        Medias.Clear();
        SelectTabId = -1;
        IsSelectAll = false;
        InputSearchText = string.Empty;
        _activeSearchText = string.Empty;
        _foldersLoaded = false;
        _hasLoadedMediaPage = false;
    }

    public override void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        base.OnNavigatedTo(navigationContext);
        var mid = navigationContext.Parameter is long value ? value : 0;
        if (mid <= 0)
        {
            NoDataVisibility = true;
            return;
        }

        ArrowBack.Fill = DictionaryResource.GetColor("ColorTextDark");
        if (_loadedMid == mid && _foldersLoaded)
        {
            if (!_hasLoadedMediaPage)
            {
                RunFireAndForget(
                    UpdateFavoritesMediaListAsync(Pager.Current),
                    nameof(UpdateFavoritesMediaListAsync),
                    _logger);
            }

            return;
        }

        RunFireAndForget(LoadFoldersAsync(mid), nameof(LoadFoldersAsync), _logger);
    }

    private async Task LoadFoldersAsync(long mid)
    {
        InitView();
        _mid = mid;
        _loadedMid = mid;
        var cancellationToken = ReplaceCancellationSource(ref _folderLoadCancellation);
        try
        {
            var folders = await _favoritesCoordinator.LoadFoldersAsync(mid, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            TabHeaders.AddRange(folders);
            _foldersLoaded = true;
            ContentVisibility = TabHeaders.Count > 0;
            LoadingVisibility = false;
            NoDataVisibility = TabHeaders.Count == 0;
            if (TabHeaders.Count == 0)
            {
                return;
            }

            _suppressTabSelection = true;
            try
            {
                SelectTabId = 0;
            }
            finally
            {
                _suppressTabSelection = false;
            }

            ConfigurePager(1, GetPageCount(TabHeaders[0]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidOperationException or ArgumentException
            or FormatException or Newtonsoft.Json.JsonException)
        {
            LoadingVisibility = false;
            NoDataVisibility = true;
            _logger.LogErrorMessage("Favorites folder loading failed.", e);
        }
    }
}
