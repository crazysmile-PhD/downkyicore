using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;

namespace DownKyi.Presentation;

internal sealed class DynamicCard : ObservableObject
{
    private readonly IAppNavigationService _navigationService;
    private readonly IPlatformLauncher _platformLauncher;

    public DynamicCard(
        IAppNavigationService navigationService,
        IPlatformLauncher platformLauncher)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _platformLauncher = platformLauncher ?? throw new ArgumentNullException(nameof(platformLauncher));
    }

    public string Id { get; set; } = string.Empty;
    public long AuthorMid { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorFace { get; set; } = string.Empty;
    public string PublishText { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CardTitle { get; set; } = string.Empty;
    public string CardDescription { get; set; } = string.Empty;
    public string Cover { get; set; } = string.Empty;
    public string Bvid { get; set; } = string.Empty;
    public string ContentUrl { get; set; } = string.Empty;
    public string ForwardText { get; set; } = string.Empty;
    public string CommentText { get; set; } = string.Empty;
    public string LikeText { get; set; } = string.Empty;
    public bool HasDescription { get; set; }
    public bool HasCard { get; set; }
    public bool HasCover { get; set; }
    public bool HasPictures { get; set; }
    public IReadOnlyList<DynamicPictureView> Pictures { get; set; } = Array.Empty<DynamicPictureView>();

    private RelayCommand? _authorCommand;

    public RelayCommand AuthorCommand => _authorCommand ??= new RelayCommand(
        () => _navigationService.Navigate(new AppNavigationRequest(
            AppRoute.UserSpace,
            AppRoute.MyDynamic,
            AuthorMid)),
        () => AuthorMid > 0);

    private AsyncRelayCommand? _openContentCommand;

    public AsyncRelayCommand OpenContentCommand => _openContentCommand ??= new AsyncRelayCommand(
        OpenContentAsync,
        () => !string.IsNullOrWhiteSpace(Bvid) || !string.IsNullOrWhiteSpace(ContentUrl));

    private async Task OpenContentAsync()
    {
        if (!string.IsNullOrWhiteSpace(Bvid))
        {
            _navigationService.Navigate(new AppNavigationRequest(
                AppRoute.VideoDetail,
                AppRoute.MyDynamic,
                $"{ParseEntrance.VideoUrl}{Bvid}"));
            return;
        }

        if (Uri.TryCreate(ContentUrl, UriKind.Absolute, out var uri))
        {
            _ = await _platformLauncher.OpenUriAsync(uri).ConfigureAwait(true);
        }
    }
}

internal sealed class DynamicPictureView
{
    public string Source { get; init; } = string.Empty;
}
