using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.Sign;
using DownKyi.Presentation;

namespace DownKyi.Services.Video;

internal readonly record struct VideoDetailOperation(int Version, CancellationToken CancellationToken);

internal interface IVideoDetailWorkflowCoordinator : IDisposable
{
    BilibiliMediaInput? CurrentInput { get; }

    VideoDetailOperation StartOperation();

    bool IsCurrent(VideoDetailOperation operation);

    void Cancel();

    void Reset();

    void SetInput(BilibiliMediaInput input);

    void ApplySearch(string? searchText);

    Task<VideoDetailParseResult> LoadDetailAsync(VideoDetailOperation operation);

}

internal sealed class VideoDetailWorkflowCoordinator : IVideoDetailWorkflowCoordinator
{
    private readonly VideoParseCoordinator _parseCoordinator;
    private readonly VideoSearchState _searchState;
    private CancellationTokenSource? _operationCancellation;
    private int _operationVersion;

    public VideoDetailWorkflowCoordinator(
        ISettingsStore settingsStore,
        IVideoTagProvider tagProvider,
        IWbiKeyProvider wbiKeyProvider,
        IBilibiliApiClient client)
        : this(
            new VideoParseCoordinator(settingsStore, tagProvider, wbiKeyProvider, client),
            new VideoSearchState())
    {
    }

    internal VideoDetailWorkflowCoordinator(
        VideoParseCoordinator parseCoordinator,
        VideoSearchState searchState)
    {
        _parseCoordinator = parseCoordinator ?? throw new ArgumentNullException(nameof(parseCoordinator));
        _searchState = searchState ?? throw new ArgumentNullException(nameof(searchState));
    }

    public BilibiliMediaInput? CurrentInput { get; private set; }

    public VideoDetailOperation StartOperation()
    {
        var version = Interlocked.Increment(ref _operationVersion);
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _operationCancellation, replacement);
        previous?.Cancel();
        previous?.Dispose();
        return new VideoDetailOperation(version, replacement.Token);
    }

    public bool IsCurrent(VideoDetailOperation operation)
    {
        return operation.Version == Volatile.Read(ref _operationVersion);
    }

    public void Cancel()
    {
        Interlocked.Increment(ref _operationVersion);
        _operationCancellation?.Cancel();
    }

    public void Reset()
    {
        CurrentInput = null;
        _searchState.Clear();
    }

    public void SetInput(BilibiliMediaInput input)
    {
        CurrentInput = input ?? throw new ArgumentNullException(nameof(input));
    }

    public void ApplySearch(string? searchText)
    {
        _searchState.Apply(searchText);
    }

    public async Task<VideoDetailParseResult> LoadDetailAsync(VideoDetailOperation operation)
    {
        EnsureCurrent(operation);
        var input = CurrentInput
            ?? throw new InvalidOperationException("A parsed media input is required before loading details.");
        var result = await _parseCoordinator.LoadDetailAsync(
            input,
            operation.CancellationToken).ConfigureAwait(false);
        EnsureCurrent(operation);
        _searchState.Reset(result.VideoSections);
        return result;
    }

    private void EnsureCurrent(VideoDetailOperation operation)
    {
        operation.CancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(operation))
        {
            throw new OperationCanceledException(operation.CancellationToken);
        }
    }

    public void Dispose()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        GC.SuppressFinalize(this);
    }
}
