using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Settings;
using DownKyi.ViewModels.PageViewModels;

namespace DownKyi.Services.Video;

internal readonly record struct VideoDetailOperation(int Version, CancellationToken CancellationToken);

internal interface IVideoDetailWorkflowCoordinator : IDisposable
{
    string CurrentInput { get; }

    VideoDetailOperation StartOperation();

    bool IsCurrent(VideoDetailOperation operation);

    void Cancel();

    void Reset();

    string SetInput(string requestedInput);

    void ApplySearch(string? searchText);

    Task<VideoDetailParseResult> LoadDetailAsync(VideoDetailOperation operation);

    Task<VideoStreamParseResult?> LoadPageStreamAsync(
        VideoPage page,
        VideoDetailOperation operation);

    Task<IReadOnlyList<VideoStreamParseResult>> LoadPageStreamsAsync(
        IEnumerable<VideoSection> sections,
        ParseScope parseScope,
        VideoDetailOperation operation);
}

internal sealed class VideoDetailWorkflowCoordinator : IVideoDetailWorkflowCoordinator
{
    private readonly VideoParseCoordinator _parseCoordinator;
    private readonly VideoSearchState _searchState;
    private CancellationTokenSource? _operationCancellation;
    private int _operationVersion;

    public VideoDetailWorkflowCoordinator(ISettingsStore settingsStore, IVideoTagProvider tagProvider)
        : this(new VideoParseCoordinator(settingsStore, tagProvider), new VideoSearchState())
    {
    }

    internal VideoDetailWorkflowCoordinator(
        VideoParseCoordinator parseCoordinator,
        VideoSearchState searchState)
    {
        _parseCoordinator = parseCoordinator ?? throw new ArgumentNullException(nameof(parseCoordinator));
        _searchState = searchState ?? throw new ArgumentNullException(nameof(searchState));
    }

    public string CurrentInput { get; private set; } = string.Empty;

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
        CurrentInput = string.Empty;
        _searchState.Clear();
        _parseCoordinator.Reset();
    }

    public string SetInput(string requestedInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedInput);
        CurrentInput = Regex.Replace(requestedInput, @"[【]*[^【]*[^】]*[】 ]", string.Empty);
        return CurrentInput;
    }

    public void ApplySearch(string? searchText)
    {
        _searchState.Apply(searchText);
    }

    public async Task<VideoDetailParseResult> LoadDetailAsync(VideoDetailOperation operation)
    {
        EnsureCurrent(operation);
        var result = await _parseCoordinator.LoadDetailAsync(
            CurrentInput,
            refresh: true,
            operation.CancellationToken).ConfigureAwait(false);
        EnsureCurrent(operation);
        _searchState.Reset(result.VideoSections);
        return result;
    }

    public Task<VideoStreamParseResult?> LoadPageStreamAsync(
        VideoPage page,
        VideoDetailOperation operation)
    {
        ArgumentNullException.ThrowIfNull(page);
        EnsureCurrent(operation);
        return _parseCoordinator.LoadPageStreamAsync(
            CurrentInput,
            page,
            refresh: true,
            operation.CancellationToken);
    }

    public Task<IReadOnlyList<VideoStreamParseResult>> LoadPageStreamsAsync(
        IEnumerable<VideoSection> sections,
        ParseScope parseScope,
        VideoDetailOperation operation)
    {
        ArgumentNullException.ThrowIfNull(sections);
        EnsureCurrent(operation);
        var pages = VideoSelectionState.GetPagesForScope(sections, parseScope);
        return _parseCoordinator.LoadPageStreamsAsync(
            CurrentInput,
            pages,
            operation.CancellationToken);
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
