using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DownKyi.Application.Desktop;
using DownKyi.Core.Logging;
using Microsoft.Extensions.Logging;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels;

internal class ViewModelBase : BindableBase, INavigationAware, IDisposable
{
    private bool _disposed;
    protected IUserNotificationService Notifications { get; }
    protected IAppNavigationService Navigation { get; }
    protected IAppDialogService AppDialogs { get; }
    protected IRegionNavigationJournal? Journal { get; set; }
    protected string ParentView { get; set; } = string.Empty;
    protected AppRoute ParentRoute { get; set; } = AppRoute.Index;
    protected virtual Dispatcher UiDispatcher => Dispatcher.UIThread;

    public ViewModelBase(IDesktopInteractionContext desktopInteractions)
    {
        ArgumentNullException.ThrowIfNull(desktopInteractions);
        Notifications = desktopInteractions.Notifications;
        Navigation = desktopInteractions.Navigation;
        AppDialogs = desktopInteractions.Dialogs;
    }

    public virtual void OnNavigatedTo(NavigationContext navigationContext)
    {
        ArgumentNullException.ThrowIfNull(navigationContext);

        Journal = navigationContext.NavigationService.Journal;
        var viewName = navigationContext.Parameters.GetValue<string>("Parent");
        if (viewName != null)
        {
            ParentView = viewName;
        }

        var parentRoute = navigationContext.Parameters
            .FirstOrDefault(parameter => string.Equals(
                parameter.Key,
                "ParentRoute",
                StringComparison.Ordinal))
            .Value;
        if (parentRoute is AppRoute route)
        {
            ParentRoute = route;
        }
    }

    protected internal virtual void ExecuteBackSpace()
    {

    }

    protected bool TryNavigateBack()
    {
        if (Journal?.CanGoBack != true)
        {
            return false;
        }

        Journal.GoBack();
        return true;
    }

    protected void NavigateToParent(object? parameter = null)
    {
        Navigation.Navigate(new AppNavigationRequest(ParentRoute, Parameter: parameter));
    }

    public bool IsNavigationTarget(NavigationContext navigationContext)
    {
        return true;
    }

    public virtual void OnNavigatedFrom(NavigationContext navigationContext)
    {
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
        _disposed = true;
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

}
