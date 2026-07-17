using System;
using System.Threading.Tasks;

namespace DownKyi.Platform;

internal interface IUiDispatcher
{
    Task InvokeAsync(Action action);
}
