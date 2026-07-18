using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.ViewModels.Friends;
using DownKyi.ViewModels.Settings;
using DownKyi.ViewModels.Toolbox;
using DownKyi.ViewModels.UserSpace;
using Microsoft.Extensions.DependencyInjection;
using RootSeasonsSeriesViewModel = DownKyi.ViewModels.ViewSeasonsSeriesViewModel;
using UserSpaceSeasonsSeriesViewModel = DownKyi.ViewModels.UserSpace.ViewSeasonsSeriesViewModel;

namespace DownKyi.Platform;

internal sealed class AvaloniaNavigationService : IAppNavigationService, IDisposable
{
    private const int MainHistoryCapacity = 32;
    private readonly Func<AppRoute, object> _viewModelFactory;
    private readonly Action<Action> _dispatch;
    private readonly Dictionary<AppNavigationRegion, List<NavigationEntry>> _entries = [];
    private bool _disposed;

    public AvaloniaNavigationService(IServiceProvider services)
        : this(
            route => (services ?? throw new ArgumentNullException(nameof(services)))
                .GetRequiredService(GetViewModelType(route)),
            Dispatch)
    {
    }

    internal AvaloniaNavigationService(
        Func<AppRoute, object> viewModelFactory,
        Action<Action> dispatch)
    {
        _viewModelFactory = viewModelFactory ?? throw new ArgumentNullException(nameof(viewModelFactory));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    public event EventHandler<AppNavigationChangedEventArgs>? NavigationChanged;

    public void Navigate(AppNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _dispatch(() => NavigateCore(
            AppNavigationRegion.Main,
            request.Route,
            request.Parent ?? AppRoute.Index,
            request.Parameter,
            null,
            keepHistory: true));
    }

    public void NavigateRegion(
        AppNavigationRegion region,
        AppRoute route,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        _dispatch(() => NavigateCore(
            region,
            route,
            AppRoute.Index,
            null,
            parameters,
            keepHistory: false));
    }

    public void ClearRegion(AppNavigationRegion region)
    {
        _dispatch(() => ClearRegionCore(region));
    }

    public object? GetActiveView(AppNavigationRegion region)
    {
        return _entries.TryGetValue(region, out var entries) && entries.Count > 0
            ? entries[^1].ViewModel
            : null;
    }

    public bool CanGoBack(AppNavigationRegion region)
    {
        return _entries.TryGetValue(region, out var entries) && entries.Count > 1;
    }

    public void GoBack(AppNavigationRegion region)
    {
        _dispatch(() => GoBackCore(region));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var region in _entries.Keys.ToArray())
        {
            ClearRegionCore(region, publish: false);
        }

        _entries.Clear();
    }

    private void NavigateCore(
        AppNavigationRegion region,
        AppRoute route,
        AppRoute parentRoute,
        object? parameter,
        IReadOnlyDictionary<string, object?>? parameters,
        bool keepHistory)
    {
        ThrowIfDisposed();
        var entries = GetEntries(region);
        if (entries.Count > 0)
        {
            NotifyNavigatedFrom(entries[^1]);
            if (!keepHistory)
            {
                DisposeEntry(entries[^1]);
                entries.Clear();
            }
        }

        var contextValues = parameters == null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
        if (parameter != null)
        {
            contextValues["Parameter"] = parameter;
        }

        var context = new AppNavigationContext(
            region,
            route,
            parentRoute,
            parameter,
            new AppNavigationParameters(contextValues));
        var viewModel = _viewModelFactory(route);
        var entry = new NavigationEntry(viewModel, context);
        entries.Add(entry);
        TrimMainHistory(region, entries);
        NotifyNavigatedTo(entry);
        Publish(region, route, viewModel);
    }

    private void GoBackCore(AppNavigationRegion region)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(region, out var entries) || entries.Count <= 1)
        {
            return;
        }

        var current = entries[^1];
        NotifyNavigatedFrom(current);
        entries.RemoveAt(entries.Count - 1);
        DisposeEntry(current);

        var restored = entries[^1];
        NotifyNavigatedTo(restored);
        Publish(region, restored.Context.Route, restored.ViewModel);
    }

    private void ClearRegionCore(AppNavigationRegion region, bool publish = true)
    {
        if (!_entries.Remove(region, out var entries))
        {
            if (publish)
            {
                Publish(region, null, null);
            }

            return;
        }

        if (entries.Count > 0)
        {
            NotifyNavigatedFrom(entries[^1]);
        }

        foreach (var entry in entries)
        {
            DisposeEntry(entry);
        }

        if (publish)
        {
            Publish(region, null, null);
        }
    }

    private List<NavigationEntry> GetEntries(AppNavigationRegion region)
    {
        if (!_entries.TryGetValue(region, out var entries))
        {
            entries = [];
            _entries.Add(region, entries);
        }

        return entries;
    }

    private static void TrimMainHistory(AppNavigationRegion region, List<NavigationEntry> entries)
    {
        if (region != AppNavigationRegion.Main || entries.Count <= MainHistoryCapacity)
        {
            return;
        }

        var oldest = entries[0];
        entries.RemoveAt(0);
        DisposeEntry(oldest);
    }

    private static void NotifyNavigatedTo(NavigationEntry entry)
    {
        if (entry.ViewModel is IAppNavigationAware aware)
        {
            aware.OnNavigatedTo(entry.Context);
        }
    }

    private static void NotifyNavigatedFrom(NavigationEntry entry)
    {
        if (entry.ViewModel is IAppNavigationAware aware)
        {
            aware.OnNavigatedFrom(entry.Context);
        }
    }

    private static void DisposeEntry(NavigationEntry entry)
    {
        if (entry.ViewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void Publish(AppNavigationRegion region, AppRoute? route, object? content)
    {
        NavigationChanged?.Invoke(this, new AppNavigationChangedEventArgs(region, route, content));
    }

    private static void Dispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal static Type GetViewModelType(AppRoute route)
    {
        return route switch
        {
            AppRoute.Index => typeof(ViewIndexViewModel),
            AppRoute.Login => typeof(ViewLoginViewModel),
            AppRoute.VideoDetail => typeof(ViewVideoDetailViewModel),
            AppRoute.Settings => typeof(ViewSettingsViewModel),
            AppRoute.Toolbox => typeof(ViewToolboxViewModel),
            AppRoute.DownloadManager => typeof(ViewDownloadManagerViewModel),
            AppRoute.PublicFavorites => typeof(ViewPublicFavoritesViewModel),
            AppRoute.UserSpace => typeof(ViewUserSpaceViewModel),
            AppRoute.Publication => typeof(ViewPublicationViewModel),
            AppRoute.SeasonsSeries => typeof(RootSeasonsSeriesViewModel),
            AppRoute.Friends => typeof(ViewFriendsViewModel),
            AppRoute.MySpace => typeof(ViewMySpaceViewModel),
            AppRoute.MyFavorites => typeof(ViewMyFavoritesViewModel),
            AppRoute.MyBangumiFollow => typeof(ViewMyBangumiFollowViewModel),
            AppRoute.MyToViewVideo => typeof(ViewMyToViewVideoViewModel),
            AppRoute.MyHistory => typeof(ViewMyHistoryViewModel),
            AppRoute.Downloading => typeof(ViewDownloadingViewModel),
            AppRoute.DownloadFinished => typeof(ViewDownloadFinishedViewModel),
            AppRoute.Following => typeof(ViewFollowingViewModel),
            AppRoute.Follower => typeof(ViewFollowerViewModel),
            AppRoute.SettingsBasic => typeof(ViewBasicViewModel),
            AppRoute.SettingsNetwork => typeof(ViewNetworkViewModel),
            AppRoute.SettingsVideo => typeof(ViewVideoViewModel),
            AppRoute.SettingsDanmaku => typeof(ViewDanmakuViewModel),
            AppRoute.SettingsAbout => typeof(ViewAboutViewModel),
            AppRoute.BiliHelper => typeof(ViewBiliHelperViewModel),
            AppRoute.Delogo => typeof(ViewDelogoViewModel),
            AppRoute.ExtractMedia => typeof(ViewExtractMediaViewModel),
            AppRoute.Archive => typeof(ViewArchiveViewModel),
            AppRoute.UserSpaceChannel => typeof(ViewChannelViewModel),
            AppRoute.UserSpaceSeasonsSeries => typeof(UserSpaceSeasonsSeriesViewModel),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null)
        };
    }

    private sealed record NavigationEntry(object ViewModel, AppNavigationContext Context);
}
