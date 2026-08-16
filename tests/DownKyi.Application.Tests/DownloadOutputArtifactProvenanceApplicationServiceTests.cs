using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Tests;

public sealed class DownloadOutputArtifactProvenanceApplicationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task RecordPublishedBuildsDurableProvenanceWithTheApplicationClock()
    {
        var store = new RecordingStore();
        var service = new DownloadOutputArtifactProvenanceApplicationService(
            store,
            new FixedClock(Now));
        var taskId = new DownloadTaskId("provenance-task");
        var path = Path.Combine(Path.GetTempPath(), "provenance-output.mp4");

        var result = await service.RecordPublishedAsync(
            taskId,
            "media-dash",
            "media-dash",
            path,
            Evidence("media-object"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var recorded = Assert.IsType<DownloadOutputArtifactProvenance>(store.Recorded);
        Assert.Equal(taskId, recorded.TaskId);
        Assert.Equal("media-dash", recorded.ArtifactKey);
        Assert.Equal("media-dash", recorded.ArtifactKind);
        Assert.Equal(Path.GetFullPath(path), recorded.CanonicalPath);
        Assert.Equal(42, recorded.ByteLength);
        Assert.Equal(new string('a', 64), recorded.Sha256);
        Assert.Equal("windows.file-id", recorded.IdentityProvider);
        Assert.Equal("media-object", recorded.FilesystemIdentity);
        Assert.Equal(Now, recorded.PublishedAtUtc);
    }

    [Fact]
    public async Task ReadFailureRemainsDistinctFromAnEmptyProvenanceSet()
    {
        var expectedError = new OperationError(
            "download.output_provenance.read_failed",
            "Read failed.");
        var store = new RecordingStore
        {
            ReadResult = OperationResult.Failure<IReadOnlyList<DownloadOutputArtifactProvenance>>(expectedError)
        };
        var service = new DownloadOutputArtifactProvenanceApplicationService(
            store,
            new FixedClock(Now));

        var result = await service.GetPublishedAsync(
            new DownloadTaskId("provenance-task"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Same(expectedError, result.Error);
    }

    [Fact]
    public async Task RecordFailureIsPropagatedWithoutManufacturingProvenance()
    {
        var expectedError = new OperationError(
            "download.output_provenance.write_failed",
            "Write failed.");
        var store = new RecordingStore
        {
            WriteResult = OperationResult.Failure(expectedError)
        };
        var service = new DownloadOutputArtifactProvenanceApplicationService(
            store,
            new FixedClock(Now));

        var result = await service.RecordPublishedAsync(
            new DownloadTaskId("provenance-task"),
            "cover",
            "cover",
            Path.Combine(Path.GetTempPath(), "cover.jpg"),
            Evidence("cover-object"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Same(expectedError, result.Error);
        Assert.Null(store.Recorded);
    }

    [Fact]
    public void ProvenanceRejectsNonCanonicalHashesBeforeTheyReachTheDurableStore()
    {
        var taskId = new DownloadTaskId("provenance-task");

        Assert.Throws<ArgumentException>(() => new DownloadOutputArtifactProvenance(
            taskId,
            "cover",
            "cover",
            Path.Combine(Path.GetTempPath(), "cover.jpg"),
            new OutputArtifactPublicationEvidence(
                42,
                new string('A', 64),
                "windows.file-id",
                "cover-object"),
            Now));
    }

    private static OutputArtifactPublicationEvidence Evidence(string filesystemIdentity)
    {
        return new OutputArtifactPublicationEvidence(
            42,
            new string('a', 64),
            "windows.file-id",
            filesystemIdentity);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    private sealed class RecordingStore : IDownloadOutputArtifactProvenanceStore
    {
        public DownloadOutputArtifactProvenance? Recorded { get; private set; }

        public OperationResult WriteResult { get; set; } = OperationResult.Success();

        public OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>> ReadResult { get; set; } =
            OperationResult.Success<IReadOnlyList<DownloadOutputArtifactProvenance>>([]);

        public Task<OperationResult> RecordPublishedAsync(
            DownloadOutputArtifactProvenance provenance,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WriteResult.IsSuccess)
            {
                return Task.FromResult(WriteResult);
            }

            Recorded = provenance;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>>> GetPublishedAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadResult);
        }
    }
}
