using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using DownKyi.Core.Logging;
using Microsoft.Extensions.Logging;

namespace DownKyi.Commands;

internal class DownKyiAsyncDelegateCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T, bool>? _canExecute;
    private readonly ILogger _logger;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public DownKyiAsyncDelegateCommand(
        Func<T?, Task> execute,
        ILogger logger,
        Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        if (parameter is null && typeof(T) == typeof(object))
        {
            return !_isExecuting && (_canExecute?.Invoke(default!) ?? true);
        }

        if (parameter is not T typedParameter)
        {
            return false;
        }
        return !_isExecuting && (_canExecute?.Invoke(typedParameter) ?? true);
    }

    public void Execute(object? parameter)
    {
        _ = ExecuteAsync(parameter);
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

        _isExecuting = true;
        OnCanExecuteChanged();

        try
        {
            await _execute(executionParameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
        {
            _logger.LogErrorMessage("UI command execution failed.", e);
        }
        finally
        {
            _isExecuting = false;
            OnCanExecuteChanged();
        }
    }

    protected void OnCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal class DownKyiAsyncDelegateCommand : DownKyiAsyncDelegateCommand<object>
{
    public DownKyiAsyncDelegateCommand(
        Func<object?, Task> execute,
        ILogger logger,
        Func<object, bool>? canExecute = null)
        : base(execute, logger, canExecute)
    {
    }

    public DownKyiAsyncDelegateCommand(
        Func<Task> execute,
        ILogger logger,
        Func<bool>? canExecute = null)
        : this(_ => execute(), logger,
            canExecute != null ? _ => canExecute() : null)
    {
    }
}
