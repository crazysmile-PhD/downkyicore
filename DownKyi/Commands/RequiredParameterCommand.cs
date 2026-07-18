using System;
using CommunityToolkit.Mvvm.Input;

namespace DownKyi.Commands;

internal static class RequiredParameterCommand
{
    public static RelayCommand<T> Create<T>(Action<T> execute)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(execute);
        return new RelayCommand<T>(parameter =>
        {
            if (parameter != null)
            {
                execute(parameter);
            }
        });
    }

    public static RelayCommand<T> Create<T>(Action<T> execute, Predicate<T> canExecute)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(canExecute);
        return new RelayCommand<T>(
            parameter =>
            {
                if (parameter != null)
                {
                    execute(parameter);
                }
            },
            parameter => parameter != null && canExecute(parameter));
    }
}
