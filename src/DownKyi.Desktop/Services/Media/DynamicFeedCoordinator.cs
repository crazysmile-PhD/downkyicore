using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.Dynamic;
using DownKyi.Core.BiliApi.Dynamic.Models;

namespace DownKyi.Services.Media;

internal interface IDynamicFeedCoordinator
{
    Task<DynamicFeedData> LoadPageAsync(string? offset, CancellationToken cancellationToken);
}

internal sealed class DynamicFeedCoordinator : IDynamicFeedCoordinator
{
    private readonly IBilibiliApiClient _client;

    public DynamicFeedCoordinator(IBilibiliApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<DynamicFeedData> LoadPageAsync(
        string? offset,
        CancellationToken cancellationToken)
    {
        return _client.GetDynamicFeedAsync(offset, cancellationToken);
    }
}
