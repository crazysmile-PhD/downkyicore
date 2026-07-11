using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using DownKyi.Core.Logging;

namespace DownKyi.Commands;

public class DownKyiAsyncDelegateCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T, bool>? _canExecute;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public DownKyiAsyncDelegateCommand(Func<T?, Task> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
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
        if (parameter is null && typeof(T) == typeof(object))
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();

            try
            {
                await _execute(default!).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException e)
            {
                LogManager.Error(nameof(DownKyiAsyncDelegateCommand), e);
            }
            catch (HttpRequestException e)
            {
                LogManager.Error(nameof(DownKyiAsyncDelegateCommand), e);
            }
            catch (IOException e)
            {
                LogManager.Error(nameof(DownKyiAsyncDelegateCommand), e);
            }
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
            return;
        }

        if (parameter is not T typedParameter)
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(typedParameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException e)
        {
            LogManager.Error(nameof(DownKyiAsyncDelegateCommand), e);
        }
        catch (HttpRequestException e)
        {
            LogManager.Error(nameof(DownKyiAsyncDelegateCommand), e);
        }
        catch (IOException e)
        {
            LogManager.Error(nameof(DownKyiAsyncDelegateCommand), e);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    protected void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public class DownKyiAsyncDelegateCommand : DownKyiAsyncDelegateCommand<object>
{
    public DownKyiAsyncDelegateCommand(Func<object?, Task> execute, Func<object, bool>? canExecute = null)
        : base(execute, canExecute)
    {
    }

    public DownKyiAsyncDelegateCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(),
            canExecute != null ? _ => canExecute() : null)
    {
    }
}
