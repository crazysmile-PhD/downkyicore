using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadTaskFileServiceTests : IDisposable
{
    private static readonly string[] GeneratedFileNames = { "video-stream.mp4", "audio-stream.aac" };
    private readonly DownloadTaskFileService _service = new(
        new AriaRuntimeClientRegistry(),
        NullLogger<DownloadTaskFileService>.Instance);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-file-lifecycle-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetGeneratedFilesIncludesMediaAssetsAndResumeSidecars()
    {
        Directory.CreateDirectory(_directory);
        var basePath = Path.Combine(_directory, "episode-01");

        var files = _service.GetGeneratedFiles(
            basePath,
            GeneratedFileNames);

        Assert.Contains(Path.GetFullPath(Path.Combine(_directory, "video-stream.mp4.aria2")), files);
        Assert.Contains(Path.GetFullPath(Path.Combine(_directory, "audio-stream.aac.download")), files);
        Assert.Contains(Path.GetFullPath(basePath + ".mp4"), files);
        Assert.Contains(Path.GetFullPath(basePath + ".srt"), files);
        Assert.Contains(Path.GetFullPath(basePath + ".Cover.jpg"), files);
    }

    [Fact]
    public async Task DeleteFilesAsyncRemovesPartialFilesAndResumeSidecars()
    {
        Directory.CreateDirectory(_directory);
        var files = new[]
        {
            CreateFile("video.mp4", "partial video"),
            CreateFile("video.mp4.aria2", "resume metadata"),
            CreateFile("audio.aac.download", "partial audio")
        };

        var result = await _service.DeleteFilesAsync(
            files,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(files.Length, result.AttemptedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.All(files, file => Assert.False(File.Exists(file), file));
    }

    [Fact]
    public async Task DeleteFilesAsyncDoesNotDeleteWhenAlreadyCanceled()
    {
        Directory.CreateDirectory(_directory);
        var file = CreateFile("video.mp4.aria2", "resume metadata");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.DeleteFilesAsync(new[] { file }, cancellation.Token));

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void GetGeneratedFilesRejectsNullTask()
    {
        Assert.Throws<ArgumentNullException>(() => _service.GetGeneratedFiles(null!));
    }

    [Fact]
    public async Task DeleteGeneratedFilesAsyncDeletesOnlyDurablyProvenArtifacts()
    {
        Directory.CreateDirectory(_directory);
        var durableOutput = CreateFile("renamed-output.mp4", "durably published output");
        var filenameMatchedOutput = CreateFile("episode-01.mp4", "legacy-looking output");
        var transfer = CreateFile("video-stream.mp4", "transfer artifact");
        var sidecar = CreateFile("video-stream.mp4.aria2", "transfer sidecar");
        var taskId = "durable-cleanup";
        var provenance = CreateProvenance(taskId, "media", durableOutput);
        var provenanceService = new FakeOutputProvenanceService([provenance]);
        var ownershipProvider = new RecordingOwnershipProvider((candidate, _) =>
        {
            File.Delete(candidate);
            return OutputArtifactSafeDeleteResult.DeletedResult();
        });
        var service = CreateProvenanceAwareService(provenanceService, ownershipProvider);
        var item = CreateDownloadingItem(taskId, "episode-01");
        item.Downloading.DownloadFiles["video"] = Path.GetFileName(transfer);

        var result = await service.DeleteGeneratedFilesAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(durableOutput));
        Assert.True(File.Exists(filenameMatchedOutput));
        Assert.True(File.Exists(transfer));
        Assert.True(File.Exists(sidecar));
        Assert.Equal([Path.GetFullPath(durableOutput)], ownershipProvider.DeleteCandidates);
        Assert.Equal(
            DownloadOutputArtifactCleanupStatus.Deleted,
            Assert.Single(result.Entries, entry => entry.ArtifactKey == "media").Status);
        Assert.Equal(
            DownloadOutputArtifactCleanupStatus.PreservedUnproven,
            Assert.Single(result.Entries, entry => entry.Path == Path.GetFullPath(filenameMatchedOutput)).Status);
    }

    [Fact]
    public async Task DeleteGeneratedFilesAsyncPreservesLegacyFilenameMatchedOutputWithoutProvenance()
    {
        Directory.CreateDirectory(_directory);
        var output = CreateFile("legacy-task.mp4", "legacy output");
        var taskId = "legacy-task";
        var provenanceService = new FakeOutputProvenanceService([]);
        var ownershipProvider = new RecordingOwnershipProvider((_, _) =>
            throw new InvalidOperationException("Untracked outputs must not reach the ownership provider."));
        var service = CreateProvenanceAwareService(provenanceService, ownershipProvider);

        var result = await service.DeleteGeneratedFilesAsync(
            CreateDownloadingItem(taskId, taskId),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(output));
        Assert.Empty(ownershipProvider.DeleteCandidates);
        Assert.Equal(
            DownloadOutputArtifactCleanupStatus.PreservedUnproven,
            Assert.Single(result.Entries).Status);
    }

    [Fact]
    public async Task DeleteGeneratedFilesAsyncDeletesOnlyIndividuallyProvenSubtitleTracks()
    {
        Directory.CreateDirectory(_directory);
        var englishSubtitle = CreateFile("episode-02_en.srt", "English subtitle");
        var japaneseSubtitle = CreateFile("episode-02_ja.srt", "Japanese subtitle");
        var taskId = "subtitle-provenance";
        var provenance = CreateProvenance(taskId, "subtitle:en", englishSubtitle);
        var provenanceService = new FakeOutputProvenanceService([provenance]);
        var ownershipProvider = new RecordingOwnershipProvider((candidate, _) =>
        {
            File.Delete(candidate);
            return OutputArtifactSafeDeleteResult.DeletedResult();
        });
        var service = CreateProvenanceAwareService(provenanceService, ownershipProvider);

        var result = await service.DeleteGeneratedFilesAsync(
            CreateDownloadingItem(taskId, "episode-02"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(englishSubtitle));
        Assert.True(File.Exists(japaneseSubtitle));
        Assert.Equal([Path.GetFullPath(englishSubtitle)], ownershipProvider.DeleteCandidates);
        Assert.Equal(
            DownloadOutputArtifactCleanupStatus.Deleted,
            Assert.Single(result.Entries, entry => entry.ArtifactKey == "subtitle:en").Status);
        Assert.Equal(
            DownloadOutputArtifactCleanupStatus.PreservedUnproven,
            Assert.Single(result.Entries, entry => entry.Path == Path.GetFullPath(japaneseSubtitle)).Status);
    }

    [Theory]
    [InlineData(OutputArtifactSafeDeleteStatus.Replaced, (int)DownloadOutputArtifactCleanupStatus.PreservedReplaced)]
    [InlineData(OutputArtifactSafeDeleteStatus.Modified, (int)DownloadOutputArtifactCleanupStatus.PreservedModified)]
    [InlineData(OutputArtifactSafeDeleteStatus.Unsupported, (int)DownloadOutputArtifactCleanupStatus.PreservedUnsupported)]
    public async Task DeleteGeneratedFilesAsyncReportsNonDestructiveOwnershipOutcomes(
        OutputArtifactSafeDeleteStatus safeDeleteStatus,
        int expectedCleanupStatus)
    {
        Directory.CreateDirectory(_directory);
        var output = CreateFile("episode-03.mp4", "published output");
        var taskId = "ownership-outcome";
        var provenance = CreateProvenance(taskId, "media", output);
        var provenanceService = new FakeOutputProvenanceService([provenance]);
        var ownershipProvider = new RecordingOwnershipProvider((_, _) => new(safeDeleteStatus));
        var service = CreateProvenanceAwareService(provenanceService, ownershipProvider);

        var result = await service.DeleteGeneratedFilesAsync(
            CreateDownloadingItem(taskId, "episode-03"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(output));
        Assert.Equal(
            (DownloadOutputArtifactCleanupStatus)expectedCleanupStatus,
            Assert.Single(result.Entries, entry => entry.ArtifactKey == "media").Status);
    }

    [Fact]
    public async Task DeleteGeneratedFilesAsyncReportsFailedAndPreservesFilesWhenProvenanceCannotBeLoaded()
    {
        Directory.CreateDirectory(_directory);
        var output = CreateFile("failed-read.mp4", "published output");
        var taskId = "failed-read";
        var provenanceService = new FakeOutputProvenanceService([])
        {
            PublishedResult = OperationResult.Failure<IReadOnlyList<DownloadOutputArtifactProvenance>>(
                new OperationError(
                    "download.output_provenance.read_failed",
                    "Provenance read failed."))
        };
        var ownershipProvider = new RecordingOwnershipProvider((_, _) =>
            throw new InvalidOperationException("Failed provenance reads must not reach the ownership provider."));
        var service = CreateProvenanceAwareService(provenanceService, ownershipProvider);

        var result = await service.DeleteGeneratedFilesAsync(
            CreateDownloadingItem(taskId, taskId),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedCount);
        Assert.True(File.Exists(output));
        Assert.Empty(ownershipProvider.DeleteCandidates);
        Assert.Contains(
            result.Entries,
            entry => entry.Status == DownloadOutputArtifactCleanupStatus.PreservedUnproven
                && entry.Path == Path.GetFullPath(output));
    }

    [Fact]
    public async Task DeleteGeneratedFilesAsyncFailsClosedWhenOwnershipDeletionHasIoUncertainty()
    {
        Directory.CreateDirectory(_directory);
        var output = CreateFile("io-uncertain.mp4", "published output");
        var taskId = "io-uncertain";
        var provenance = CreateProvenance(taskId, "media", output);
        var provenanceService = new FakeOutputProvenanceService([provenance]);
        var ownershipProvider = new RecordingOwnershipProvider((_, _) =>
            throw new IOException("The output could not be opened safely."));
        var service = CreateProvenanceAwareService(provenanceService, ownershipProvider);

        var result = await service.DeleteGeneratedFilesAsync(
            CreateDownloadingItem(taskId, taskId),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(output));
        Assert.Equal(
            DownloadOutputArtifactCleanupStatus.Failed,
            Assert.Single(result.Entries, entry => entry.ArtifactKey == "media").Status);
    }

    private string CreateFile(string name, string contents)
    {
        var file = Path.Combine(_directory, name);
        File.WriteAllText(file, contents);
        return file;
    }

    private static DownloadTaskFileService CreateProvenanceAwareService(
        IDownloadOutputArtifactProvenanceApplicationService provenanceService,
        IOutputArtifactOwnershipProvider ownershipProvider)
    {
        return new DownloadTaskFileService(
            new AriaRuntimeClientRegistry(),
            NullLogger<DownloadTaskFileService>.Instance,
            provenanceService,
            ownershipProvider);
    }

    private DownloadingItem CreateDownloadingItem(string id, string baseFileName)
    {
        return new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = id,
                FilePath = Path.Combine(_directory, baseFileName)
            },
            Downloading = new Downloading
            {
                Id = id
            }
        };
    }

    private static DownloadOutputArtifactProvenance CreateProvenance(
        string taskId,
        string artifactKey,
        string path)
    {
        return new DownloadOutputArtifactProvenance(
            new DownloadTaskId(taskId),
            artifactKey,
            "test-output",
            Path.GetFullPath(path),
            new OutputArtifactPublicationEvidence(
                new FileInfo(path).Length,
                new string('a', 64),
                "test-ownership-provider",
                $"identity:{artifactKey}"),
            DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeOutputProvenanceService(
        IReadOnlyList<DownloadOutputArtifactProvenance> published)
        : IDownloadOutputArtifactProvenanceApplicationService
    {
        public OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>> PublishedResult { get; set; } =
            OperationResult.Success(published);

        public Task<OperationResult> RecordPublishedAsync(
            DownloadTaskId taskId,
            string artifactKey,
            string artifactKind,
            string canonicalPath,
            OutputArtifactPublicationEvidence publicationEvidence,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>>> GetPublishedAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PublishedResult);
        }
    }

    private sealed class RecordingOwnershipProvider(
        Func<string, DownloadOutputArtifactProvenance, OutputArtifactSafeDeleteResult> delete)
        : IOutputArtifactOwnershipProvider
    {
        public List<string> DeleteCandidates { get; } = [];

        public Task<OutputArtifactSafeDeleteResult> DeleteIfOwnedAsync(
            string candidatePath,
            DownloadOutputArtifactProvenance provenance,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCandidates.Add(candidatePath);
            return Task.FromResult(delete(candidatePath, provenance));
        }
    }
}
