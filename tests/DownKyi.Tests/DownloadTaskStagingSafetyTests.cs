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
    public void StartupRemovesStaleStagingButPreservesPublishedFile()
    {
        var outputBase = Path.Combine(_directory, "output");
        var published = Path.Combine(_directory, "output.mp4");
        var stale = Path.Combine(_directory, ".downkyi", "staging", Guid.NewGuid().ToString("N"), "prior-task");
        Directory.CreateDirectory(stale);
        File.WriteAllBytes(Path.Combine(stale, "partial.m4s"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(stale)!, ".session-lock"), []);
        var foreignDirectory = Path.Combine(_directory, ".downkyi", "staging", "foreign");
        Directory.CreateDirectory(foreignDirectory);
        File.WriteAllBytes(Path.Combine(foreignDirectory, "user-file.mp4"), [5, 6]);
        File.WriteAllBytes(published, [91, 0, 255, 17]);
        var task = CreateTask(outputBase, published);
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);

        staging.CleanupStale([task]);

        Assert.False(Directory.Exists(stale));
        Assert.Equal(new byte[] { 5, 6 }, File.ReadAllBytes(Path.Combine(foreignDirectory, "user-file.mp4")));
        Assert.Equal(new byte[] { 91, 0, 255, 17 }, File.ReadAllBytes(published));
        Assert.Equal(published, task.Output.PublishedArtifacts["media"]);
    }

    [Fact]
    public void FailedStaleCleanupCannotCauseNewTaskToReuseOldStaging()
    {
        var outputBase = Path.Combine(_directory, "output");
        var published = Path.Combine(_directory, "output.mp4");
        var staleSession = Path.Combine(_directory, ".downkyi", "staging", Guid.NewGuid().ToString("N"));
        var stale = Path.Combine(staleSession, "prior-task");
        Directory.CreateDirectory(stale);
        File.WriteAllBytes(Path.Combine(stale, "partial.m4s"), [1, 2, 3]);
        File.WriteAllBytes(published, [91, 0, 255, 17]);
        using var heldLock = new FileStream(Path.Combine(staleSession, ".session-lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var task = CreateTask(outputBase, published);
        var staging = new DownloadTaskStaging(NullLogger<DownloadTaskStaging>.Instance);

        staging.CleanupStale([task]);
        var fresh = staging.GetDirectory(new DownloadTaskId("new-task"), outputBase);

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
        var taskDirectory = staging.GetDirectory(taskId, outputBase);
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
        var taskDirectory = staging.GetDirectory(taskId, outputBase);
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
        Assert.Throws<IOException>(() => staging.GetDirectory(new DownloadTaskId("new"), outputBase));
    }

    private static DownloadTask CreateTask(string outputBase, string published)
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
            new DownloadPlan(DownloadContentSelection.None, [], 0, nfoRequest: null),
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
