using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadTaskStagingSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "downkyi-staging-safety", Guid.NewGuid().ToString("N"));

    [Fact]
    public void StartupPreservesUnfinishedStagingAndPublishedFile()
    {
        var outputBase = Path.Combine(_directory, "output");
        var published = Path.Combine(_directory, "output.mp4");
        var task = CreateTask(outputBase, published);
        var stale = Path.Combine(_directory, ".downkyi", "staging", task.Output.StagingToken, "data");
        Directory.CreateDirectory(stale);
        File.WriteAllBytes(Path.Combine(stale, "partial.m4s"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(stale)!, ".session-lock"), []);
        var foreignDirectory = Path.Combine(_directory, ".downkyi", "staging", "foreign");
        Directory.CreateDirectory(foreignDirectory);
        File.WriteAllBytes(Path.Combine(foreignDirectory, "user-file.mp4"), [5, 6]);
        File.WriteAllBytes(published, [91, 0, 255, 17]);
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);

        staging.CleanupStale([task]);

        Assert.True(Directory.Exists(stale));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(stale, "partial.m4s")));
        Assert.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(Path.Combine(foreignDirectory, "user-file.mp4")));
        Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(published));
        Assert.Equal(published, task.Output.PublishedArtifacts["media"]);
        Assert.Equal(stale, staging.GetDirectory(task.Id, outputBase, task.Output.StagingToken));
        staging.CleanupCurrentSession();
    }

    [Fact]
    public void ShutdownPreservesOwnedTransferForNextSession()
    {
        var outputBase = Path.Combine(_directory, "output");
        var task = CreateTask(outputBase, Path.Combine(_directory, "published.mp4"));
        var first = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        var directory = first.GetDirectory(task.Id, outputBase, task.Output.StagingToken);
        File.WriteAllBytes(Path.Combine(directory, "partial.m4s"), [1, 2, 3]);

        first.CleanupCurrentSession();

        var reopened = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        Assert.Equal(directory, reopened.GetDirectory(task.Id, outputBase, task.Output.StagingToken));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "partial.m4s")));
        reopened.CleanupCurrentSession();
    }

    [Fact]
    public async Task ConcurrentContextCreationOpensOneTaskLock()
    {
        var outputBase = Path.Combine(_directory, "output");
        var task = CreateTask(outputBase, Path.Combine(_directory, "published.mp4"));
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        using var start = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return staging.GetDirectory(task.Id, outputBase, task.Output.StagingToken);
        })).ToArray();

        start.Set();
        var directories = await Task.WhenAll(workers);

        Assert.All(directories, directory => Assert.Equal(directories[0], directory));
        staging.CleanupCurrentSession();
    }

    [Fact]
    public async Task RecordedLegacyTransferIsCopiedWithoutMutatingOriginal()
    {
        var outputBase = Path.Combine(_directory, "output");
        var name = "legacy-transfer";
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, name), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_directory, name + ".aria2"), [4, 5]);
        var task = CreateTask(outputBase, Path.Combine(_directory, "published.mp4"),
            [KeyValuePair.Create("media", name)]);
        using var settings = new TestSettingsStore();
        var context = new DownloadExecutionContext(task.Id,
            DownloadExecutionContextFactory.CreateInput(task, settings.Store.Current),
            null, static (_, token) => token.ThrowIfCancellationRequested());
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        context.StagingDirectory = staging.GetDirectory(task.Id, outputBase, task.Output.StagingToken);

        await ResolvePlaybackStage.AdoptRecordedTransfersAsync(
            context, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(
            Path.Combine(context.StagingDirectory, name), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(
            Path.Combine(context.StagingDirectory, name + ".aria2"), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(
            Path.Combine(_directory, name), TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(
            Path.Combine(_directory, name + ".aria2"), TestContext.Current.CancellationToken));
        staging.CleanupCurrentSession();
    }

    [Fact]
    public void StartupRemovesOnlyCompletedTasksOwnedStaging()
    {
        var outputBase = Path.Combine(_directory, "output");
        var published = Path.Combine(_directory, "published.mp4");
        var task = CreateTask(outputBase, published).Complete(
            new DownloadCompletion(3, "done", null), DateTimeOffset.UnixEpoch.AddSeconds(3)).RequireValue();
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        var directory = staging.GetDirectory(task.Id, outputBase, task.Output.StagingToken);
        File.WriteAllBytes(Path.Combine(directory, "partial.m4s"), [1, 2, 3]);
        File.WriteAllBytes(published, [91, 0, 255, 17]);
        staging.CleanupCurrentSession();

        var restarted = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        restarted.CleanupStale([task]);

        Assert.False(Directory.Exists(directory));
        Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(published));
    }

    [Fact]
    public void FailedCompletedStagingCleanupCannotCauseNewTaskToReuseOldStaging()
    {
        var outputBase = Path.Combine(_directory, "output");
        var published = Path.Combine(_directory, "output.mp4");
        var task = CreateTask(outputBase, published).Complete(
            new DownloadCompletion(3, "done", null), DateTimeOffset.UnixEpoch.AddSeconds(3)).RequireValue();
        var staleSession = Path.Combine(_directory, ".downkyi", "staging", task.Output.StagingToken);
        var stale = Path.Combine(staleSession, "data");
        Directory.CreateDirectory(stale);
        File.WriteAllBytes(Path.Combine(stale, "partial.m4s"), [1, 2, 3]);
        File.WriteAllBytes(published, [91, 0, 255, 17]);
        using var heldLock = new FileStream(Path.Combine(staleSession, ".session-lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);

        staging.CleanupStale([task]);
        var fresh = staging.GetDirectory(new DownloadTaskId("new-task"),
            outputBase, Guid.NewGuid().ToString("N"));

        Assert.True(Directory.Exists(stale));
        Assert.NotEqual(stale, fresh);
        Assert.False(fresh.StartsWith(staleSession + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(published));
        staging.CleanupCurrentSession();
    }

    [Fact]
    public void TaskCleanupDoesNotFollowDirectorySymlinkInsideStaging()
    {
        var outputBase = Path.Combine(_directory, "output");
        var outside = Path.Combine(_directory, "outside-user-directory");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "DO-NOT-DELETE.txt");
        File.WriteAllBytes(sentinel, [91, 0, 255, 17]);
        var taskId = new DownloadTaskId("linked-child");
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        var taskDirectory = staging.GetDirectory(taskId, outputBase, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskDirectory);
        Directory.CreateSymbolicLink(Path.Combine(taskDirectory, "link"), outside);

        staging.CleanupTask(taskId);

        Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(sentinel));
        staging.CleanupCurrentSession();
    }

    [Fact]
    public void TaskCleanupRefusesRedirectedStagingDirectory()
    {
        var outputBase = Path.Combine(_directory, "output");
        var outside = Path.Combine(_directory, "outside-user-directory");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "DO-NOT-DELETE.txt");
        File.WriteAllBytes(sentinel, [91, 0, 255, 17]);
        var taskId = new DownloadTaskId("linked-staging");
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);
        var taskDirectory = staging.GetDirectory(taskId, outputBase, Guid.NewGuid().ToString("N"));
        Directory.Delete(taskDirectory);
        Directory.CreateSymbolicLink(taskDirectory, outside);

        staging.CleanupTask(taskId);

        Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(sentinel));
        staging.CleanupCurrentSession();
        Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public void StartupCleanupDoesNotFollowRedirectedStagingRoot()
    {
        var outputBase = Path.Combine(_directory, "output", "video");
        var outputDirectory = Path.GetDirectoryName(outputBase)!;
        var outside = Path.Combine(_directory, "outside-user-directory");
        var apparentSession = Path.Combine(outside, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(apparentSession);
        File.WriteAllBytes(Path.Combine(apparentSession, ".session-lock"), []);
        var sentinel = Path.Combine(apparentSession, "DO-NOT-DELETE.txt");
        File.WriteAllBytes(sentinel, [91, 0, 255, 17]);
        var linkParent = Path.Combine(outputDirectory, ".downkyi");
        Directory.CreateDirectory(linkParent);
        Directory.CreateSymbolicLink(Path.Combine(linkParent, "staging"), outside);
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);

        staging.CleanupStale([CreateTask(outputBase, Path.Combine(outputDirectory, "video.mp4"))]);

        Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(sentinel));
        Assert.Throws<IOException>(() => staging.GetDirectory(new DownloadTaskId("new"),
            outputBase, Guid.NewGuid().ToString("N")));
    }

    private static DownloadTask CreateTask(
        string outputBase,
        string published,
        IEnumerable<KeyValuePair<string, string>>? transferFiles = null)
    {
        var now = DateTimeOffset.UnixEpoch;
        var task = DownloadTask.Create(
            new DownloadTaskId("prior-task"),
            new DownloadTaskMetadata(
                new DownloadMediaIdentity("BV1TEST", 1, 2, 3, 1, 1),
                "Collection", "Task", "00:01", "avc1",
                new DownloadQuality(80, "1080P"),
                new DownloadQuality(30280, "AAC"),
                string.Empty, string.Empty, 0),
            new DownloadPlan(DownloadContentSelection.None, transferFiles ?? [], 0, nfoRequest: null),
            new DownloadOutput(outputBase, null),
            now);
        task = task.Start(now.AddSeconds(1)).RequireValue();
        return task.RecordPublishedArtifact("media", published, now.AddSeconds(2)).RequireValue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
