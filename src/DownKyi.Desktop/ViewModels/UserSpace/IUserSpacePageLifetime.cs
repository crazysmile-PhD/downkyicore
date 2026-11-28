using System.Threading;
using DownKyi.Application.Desktop;

namespace DownKyi.ViewModels.UserSpace;

internal interface IUserSpacePageLifetime
{
    long Mid { get; }
    int Page { get; }
    AppRoute Route { get; }
    void ResumePage(CancellationToken lifetime);
}
