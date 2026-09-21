using System;
using System.IO;
using System.Net.Http;
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
    private readonly DownKyiAsyncCommandGate _executionGate;
    private readonly Action? _executionRejected;

    public event EventHandler? CanExecuteChanged;

    public DownKyiAsyncDelegateCommand(
        Func<T?, Task> execute,
        ILogger logger,
        Func<T, bool>? canExecute = null,
        Func<bool>? isCancellationExpected = null,
        DownKyiAsyncCommandGate? executionGate = null,
        Action? executionRejected = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canExecute = canExecute;
        _isCancellationExpected = isCancellationExpected;
        _executionGate = executionGate ?? new DownKyiAsyncCommandGate();
        _executionRejected = executionRejected;
        _executionGate.IsExecutingChanged += OnExecutionGateChanged;
    }

    public bool CanExecute(object? parameter)
    {
        if (parameter is null && typeof(T) == typeof(object))
        {
            return CanExecuteWhileGateBusy() && (_canExecute?.Invoke(default!) ?? true);
        }

        if (parameter is not T typedParameter)
        {
            return false;
        }
        return CanExecuteWhileGateBusy() && (_canExecute?.Invoke(typedParameter) ?? true);
    }

    public void Execute(object? parameter)
    {
        if (!CanExecuteParameter(parameter))
        {
            return;
        }

        if (!_executionGate.TryEnter())
        {
            _executionRejected?.Invoke();
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
        try
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
            _executionGate.Exit();
        }
    }

    private bool IsExpectedCancellation(OperationCanceledException exception)
    {
        return _isCancellationExpected?.Invoke()
            ?? exception.CancellationToken.IsCancellationRequested;
    }

    private bool CanExecuteWhileGateBusy()
    {
        return !_executionGate.IsExecuting || _executionRejected != null;
    }

    private bool CanExecuteParameter(object? parameter)
    {
        if (parameter is null && typeof(T) == typeof(object))
        {
            return _canExecute?.Invoke(default!) ?? true;
        }

        return parameter is T typedParameter && (_canExecute?.Invoke(typedParameter) ?? true);
    }

    private void OnCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnExecutionGateChanged(object? sender, EventArgs e)
    {
        OnCanExecuteChanged();
    }
}

internal class DownKyiAsyncDelegateCommand : DownKyiAsyncDelegateCommand<object>
{
    public DownKyiAsyncDelegateCommand(
        Func<object?, Task> execute,
        ILogger logger,
        Func<object, bool>? canExecute = null,
        Func<bool>? isCancellationExpected = null,
        DownKyiAsyncCommandGate? executionGate = null,
        Action? executionRejected = null)
        : base(execute, logger, canExecute, isCancellationExpected, executionGate, executionRejected)
    {
    }

    public DownKyiAsyncDelegateCommand(
        Func<Task> execute,
        ILogger logger,
        Func<bool>? canExecute = null,
        Func<bool>? isCancellationExpected = null,
        DownKyiAsyncCommandGate? executionGate = null,
        Action? executionRejected = null)
        : this(_ => execute(), logger,
            canExecute != null ? _ => canExecute() : null,
            isCancellationExpected,
            executionGate,
            executionRejected)
    {
    }
}
