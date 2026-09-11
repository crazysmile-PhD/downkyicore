using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using DownKyi.Application.Desktop;

namespace DownKyi.Platform;

internal sealed class AvaloniaNavigationService : IAppNavigationService, IDisposable
{
    private const int MainHistoryCapacity = 32;
    private readonly Func<AppRoute, object> _viewModelFactory;
    private readonly Action<Action> _dispatch;
    private readonly Dictionary<AppNavigationRegion, List<NavigationEntry>> _entries = [];
    private bool _disposed;

    public AvaloniaNavigationService(NavigationViewModelFactory viewModelFactory)
        : this((viewModelFactory ?? throw new ArgumentNullException(nameof(viewModelFactory))).Create, Dispatch)
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

    private sealed record NavigationEntry(object ViewModel, AppNavigationContext Context);
}
