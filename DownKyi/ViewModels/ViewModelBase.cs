using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DownKyi.Application.Desktop;
using DownKyi.Core.Logging;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels;

internal class ViewModelBase : ObservableObject, IAppNavigationAware, IDisposable
{
    private bool _disposed;
    private AppNavigationRegion? _observedRegion;
    private object? _regionContent;
    protected IUserNotificationService Notifications { get; }
    protected IAppNavigationService Navigation { get; }
    protected IAppDialogService AppDialogs { get; }
    protected AppRoute ParentRoute { get; set; } = AppRoute.Index;
    protected virtual Dispatcher UiDispatcher => Dispatcher.UIThread;

    public object? RegionContent
    {
        get => _regionContent;
        private set => SetProperty(ref _regionContent, value);
    }

    public ViewModelBase(IDesktopInteractionContext desktopInteractions)
    {
        ArgumentNullException.ThrowIfNull(desktopInteractions);
        Notifications = desktopInteractions.Notifications;
        Navigation = desktopInteractions.Navigation;
        AppDialogs = desktopInteractions.Dialogs;
    }

    public virtual void OnNavigatedTo(AppNavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);
        ParentRoute = navigationContext.ParentRoute;
    }

    protected internal virtual void ExecuteBackSpace()
    {

    }

    protected bool TryNavigateBack()
    {
        if (!Navigation.CanGoBack(AppNavigationRegion.Main))
        {
            return false;
        }

        Navigation.GoBack(AppNavigationRegion.Main);
        return true;
    }

    protected void NavigateToParent(object? parameter = null)
    {
        Navigation.Navigate(new AppNavigationRequest(ParentRoute, Parameter: parameter));
    }

    public virtual void OnNavigatedFrom(AppNavigationContext navigationContext)
    {
    }

    protected void ObserveRegion(AppNavigationRegion region)
    {
        if (_observedRegion == region)
        {
            return;
        }

        if (_observedRegion != null)
        {
            Navigation.NavigationChanged -= NavigationOnNavigationChanged;
        }

        _observedRegion = region;
        RegionContent = Navigation.GetActiveView(region);
        Navigation.NavigationChanged += NavigationOnNavigationChanged;
    }

    /// <summary>
    /// 异步修改绑定到UI的属性
    /// </summary>
    /// <param name="callback"></param>
    protected void PropertyChangeAsync(Action callback)
    {
        UiDispatcher.InvokeAsync(callback);
    }

    /// <summary>
    /// 同步修改绑定到UI的属性
    /// </summary>
    /// <param name="callback"></param>
    protected void PropertyChange(Action callback)
    {
        UiDispatcher.Invoke(callback);
    }

    protected void RunFireAndForget(Task task, string operation, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(logger);
        _ = RunFireAndForgetAsync(task, $"{GetType().Name}.{operation}", logger);
    }

    private static async Task RunFireAndForgetAsync(Task task, string operation, ILogger logger)
    {
        await task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Exception is { } exception)
                {
                    logger.LogErrorMessage(operation, exception.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing && _observedRegion != null)
        {
            Navigation.NavigationChanged -= NavigationOnNavigationChanged;
            _observedRegion = null;
        }
    }

    protected bool IsDisposed => _disposed;

    protected static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);
        if (current == null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        finally
        {
            current.Dispose();
        }
    }

    protected static CancellationToken ReplaceCancellationSource(ref CancellationTokenSource? source)
    {
        CancelAndDispose(ref source);
        var replacement = new CancellationTokenSource();
        source = replacement;
        return replacement.Token;
    }

    private void NavigationOnNavigationChanged(object? sender, AppNavigationChangedEventArgs e)
    {
        if (_observedRegion != e.Region)
        {
            return;
        }

        if (UiDispatcher.CheckAccess())
        {
            RegionContent = e.Content;
            return;
        }

        UiDispatcher.Post(() => RegionContent = e.Content);
    }

}
