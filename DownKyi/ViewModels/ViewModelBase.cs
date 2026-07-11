using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DownKyi.Core.Logging;
using DownKyi.PrismExtension.Dialog;
using Prism.Events;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace DownKyi.ViewModels;

public class ViewModelBase : BindableBase, INavigationAware, IDisposable
{
    private bool _disposed;
    protected readonly IEventAggregator EventAggregator;
    protected IDialogService? DialogService;
    protected IRegionNavigationJournal? Journal;
    protected string ParentView = string.Empty;
    protected virtual Dispatcher UiDispatcher => Dispatcher.UIThread;

    public ViewModelBase(IEventAggregator eventAggregator)
    {
        EventAggregator = eventAggregator;
    }

    public ViewModelBase(IEventAggregator eventAggregator, IDialogService dialogService)
    {
        EventAggregator = eventAggregator;
        DialogService = dialogService;
    }

    public virtual void OnNavigatedTo(NavigationContext navigationContext)
    {
        Journal = navigationContext.NavigationService.Journal;
        var viewName = navigationContext.Parameters.GetValue<string>("Parent");
        if (viewName != null)
        {
            ParentView = viewName;
        }
    }

    protected internal virtual void ExecuteBackSpace()
    {

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

    protected void RunFireAndForget(Task task, string operation)
    {
        _ = RunFireAndForgetAsync(task, $"{GetType().Name}.{operation}");
    }

    private static async Task RunFireAndForgetAsync(Task task, string operation)
    {
        await task.ContinueWith(
            completedTask =>
            {
                if (completedTask.Exception is { } exception)
                {
                    LogManager.Error(operation, exception.GetBaseException());
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
