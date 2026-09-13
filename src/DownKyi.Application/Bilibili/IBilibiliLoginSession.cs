namespace DownKyi.Application.Bilibili;

public interface IBilibiliLoginSession : IDisposable
{
    Task<BilibiliLoginResponse> GetAsync(
        BilibiliHttpRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BilibiliLoginCookie>> FollowCallbackAsync(
        Uri callbackUri,
        CancellationToken cancellationToken);
}

public interface IBilibiliLoginSessionFactory
{
    IBilibiliLoginSession Create(
        IReadOnlyList<BilibiliLoginCookie>? initialCookies = null);
}
