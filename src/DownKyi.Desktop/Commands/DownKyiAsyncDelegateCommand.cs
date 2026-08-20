using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Commands;

internal class DownKyiAsyncDelegateCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T, bool>? _canExecute;
    private readonly ILogger _logger;
    private readonly Func<bool>? _isCancellationExpected;
    private int _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public DownKyiAsyncDelegateCommand(
        Func<T?, Task> execute,
        ILogger logger,
        Func<T, bool>? canExecute = null,
        Func<bool>? isCancellationExpected = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canExecute = canExecute;
        _isCancellationExpected = isCancellationExpected;
    }

    public bool CanExecute(object? parameter)
    {
        if (parameter is null && typeof(T) == typeof(object))
        {
            return Volatile.Read(ref _isExecuting) == 0 && (_canExecute?.Invoke(default!) ?? true);
        }

        if (parameter is not T typedParameter)
        {
            return false;
        }
        return Volatile.Read(ref _isExecuting) == 0 && (_canExecute?.Invoke(typedParameter) ?? true);
    }

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)
            || Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0)
        {
            return;
        }

        _ = ExecuteAsync(parameter);
    }

    public void NotifyCanExecuteChanged()
    {
        OnCanExecuteChanged();
    }

    private async Task ExecuteAsync(object? parameter)
    {
        T? executionParameter;
        if (parameter is null && typeof(T) == typeof(object))
        {
            executionParameter = default;
        }
        else if (parameter is T typedParameter)
        {
            executionParameter = typedParameter;
        }
        else
        {
            return;
        }

        OnCanExecuteChanged();

        try
        {
            await _execute(executionParameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException e) when (IsExpectedCancellation(e))
        {
            return;
        }
        catch (OperationCanceledException e)
        {
            _logger.LogErrorMessage("UI command was canceled unexpectedly.", e);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
        {
            _logger.LogErrorMessage("UI command execution failed.", e);
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
            OnCanExecuteChanged();
        }
    }

    private bool IsExpectedCancellation(OperationCanceledException exception)
    {
        return _isCancellationExpected?.Invoke()
            ?? exception.CancellationToken.IsCancellationRequested;
    }

    private void OnCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal class DownKyiAsyncDelegateCommand : DownKyiAsyncDelegateCommand<object>
{
    public DownKyiAsyncDelegateCommand(
        Func<object?, Task> execute,
        ILogger logger,
        Func<object, bool>? canExecute = null,
        Func<bool>? isCancellationExpected = null)
        : base(execute, logger, canExecute, isCancellationExpected)
    {
    }

    public DownKyiAsyncDelegateCommand(
        Func<Task> execute,
        ILogger logger,
        Func<bool>? canExecute = null,
        Func<bool>? isCancellationExpected = null)
        : this(_ => execute(), logger,
            canExecute != null ? _ => canExecute() : null,
            isCancellationExpected)
    {
    }
}
