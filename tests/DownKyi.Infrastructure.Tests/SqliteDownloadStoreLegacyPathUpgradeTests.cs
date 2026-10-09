using System.Collections.Immutable;
using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using Microsoft.Data.Sqlite;

namespace DownKyi.Infrastructure.Tests;

public sealed class SqliteDownloadStoreLegacyPathUpgradeTests : IDisposable
{
    private readonly SqliteDownloadStoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task VersionThreeEquivalentPhysicalPathRemainsAvailableWithoutBlockingAdmission()
    {
        var originalPath = Path.Combine(_fixture.TempDirectory, "equivalent", "video");
        var equivalentPath = Path.Combine(originalPath, ".") + Path.DirectorySeparatorChar;
        if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar)
        {
            equivalentPath = equivalentPath.Replace(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        if (DownloadOutputPathKey.UsesCaseInsensitiveComparison)
        {
            equivalentPath = equivalentPath.ToUpperInvariant();
        }

        await _fixture.CreateVersionThreeDatabaseAsync(_fixture.CreatePausedTask("equivalent", originalPath));
        var resolutionCalls = 0;
        using var store = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(_ =>
        {
            resolutionCalls++;
            return equivalentPath;
        }));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, resolutionCalls);
        Assert.Equal(
            "equivalent",
            Assert.Single(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Id.Value);
        Assert.Empty(await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.False(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(originalPath, (await _fixture.ReadStoredStateAsync("equivalent")).Path);
    }

    [Fact]
    public async Task VersionThreePhysicalPathChangeQuarantinesWithoutMutatingTaskOrFiles()
    {
        var originalPath = Path.Combine(_fixture.TempDirectory, "logical", "video");
        var physicalPath = Path.Combine(_fixture.TempDirectory, "physical", "video");
        var partialPath = Path.Combine(_fixture.TempDirectory, "logical", "video.download");
        var ariaPath = Path.Combine(_fixture.TempDirectory, "logical", "video.aria2");
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllTextAsync(partialPath, "partial", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(ariaPath, "aria", TestContext.Current.CancellationToken);
        await _fixture.CreateVersionThreeDatabaseAsync(_fixture.CreatePausedTask("changed", originalPath));
        var before = await _fixture.ReadStoredStateAsync("changed");
        using var store = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(_ => physicalPath));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("changed", quarantine.RecordId);
        Assert.Equal("downloading", quarantine.SourceTable);
        Assert.Equal("file_path", quarantine.FieldName);
        Assert.Equal("legacy-output-path-unverified", quarantine.Reason);
        Assert.True(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(before, await _fixture.ReadStoredStateAsync("changed"));
        Assert.True(File.Exists(partialPath));
        Assert.True(File.Exists(ariaPath));
    }

    [Fact]
    public async Task VersionThreePhysicalAliasCollisionQuarantinesEntireGroupOnly()
    {
        var physicalPath = Path.Combine(_fixture.TempDirectory, "physical", "video");
        var aliasPath = Path.Combine(_fixture.TempDirectory, "alias", "video");
        var otherPath = Path.Combine(_fixture.TempDirectory, "other", "video");
        await _fixture.CreateVersionThreeDatabaseAsync(
            _fixture.CreatePausedTask("physical", physicalPath),
            _fixture.CreatePausedTask("alias", aliasPath),
            _fixture.CreatePausedTask("other", otherPath));
        var physicalBefore = await _fixture.ReadStoredStateAsync("physical");
        var aliasBefore = await _fixture.ReadStoredStateAsync("alias");
        var otherBefore = await _fixture.ReadStoredStateAsync("other");
        var resolutionCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        using var store = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(path =>
        {
            resolutionCalls[path] = resolutionCalls.GetValueOrDefault(path) + 1;
            return path == aliasPath ? physicalPath : path;
        }));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, resolutionCalls.Count);
        Assert.All(resolutionCalls.Values, count => Assert.Equal(1, count));
        Assert.Equal(
            "other",
            Assert.Single(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Id.Value);
        Assert.Equal(
            ["alias", "physical"],
            (await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken))
                .Select(record => record.RecordId));
        Assert.True(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(physicalBefore, await _fixture.ReadStoredStateAsync("physical"));
        Assert.Equal(aliasBefore, await _fixture.ReadStoredStateAsync("alias"));
        Assert.Equal(otherBefore, await _fixture.ReadStoredStateAsync("other"));
    }

    [Fact]
    public async Task VersionThreePhysicalResolverFailureQuarantinesAndBlocksAdmission()
    {
        var originalPath = Path.Combine(_fixture.TempDirectory, "unresolved", "video");
        var safePath = Path.Combine(_fixture.TempDirectory, "safe", "video");
        await _fixture.CreateVersionThreeDatabaseAsync(
            _fixture.CreatePausedTask("unresolved", originalPath),
            _fixture.CreatePausedTask("safe", safePath));
        var before = await _fixture.ReadStoredStateAsync("unresolved");
        using var store = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(
            path => path == originalPath
                ? throw new IOException("Synthetic resolver failure.")
                : path));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "safe",
            Assert.Single(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken)).Id.Value);
        Assert.Equal(
            "unresolved",
            Assert.Single(
                await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken)).RecordId);
        Assert.True(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(before, await _fixture.ReadStoredStateAsync("unresolved"));
    }

    [Fact]
    public async Task VersionThreeCompletedTaskIsSkippedAndExistingQuarantineStillBlocksPathRisk()
    {
        var completed = _fixture.CreateCompletedTask(
            "completed",
            123,
            Path.Combine(_fixture.TempDirectory, "completed", "video"));
        var quarantined = _fixture.CreatePausedTask(
            "already-quarantined",
            Path.Combine(_fixture.TempDirectory, "quarantined", "video"));
        await _fixture.CreateVersionThreeDatabaseAsync(completed, quarantined);
        await _fixture.InsertPreexistingQuarantineAsync("already-quarantined");
        var quarantinedBefore = await _fixture.ReadStoredStateAsync("already-quarantined");
        var resolvedPaths = new List<string>();
        using var store = _fixture.CreateStore(new SqliteDownloadStoreFixture.StubPhysicalOutputPathResolver(path =>
        {
            resolvedPaths.Add(path);
            return path + "-physical";
        }));

        await store.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal([quarantined.Output.BasePath], resolvedPaths);
        Assert.Equal(
            "completed",
            Assert.Single((await store.GetHistoryPageAsync(
                null,
                10,
                TestContext.Current.CancellationToken)).Items).Id.Value);
        Assert.Empty(await store.GetUnfinishedAsync(TestContext.Current.CancellationToken));
        var quarantine = Assert.Single(
            await store.GetQuarantinedRecordsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("already-quarantined", quarantine.RecordId);
        Assert.Equal("preexisting-corrupt-record", quarantine.Reason);
        Assert.True(await store.IsLegacyUpgradeAdmissionBlockedAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(completed.Metadata.Name, Assert.Single((await store.GetHistoryPageAsync(
            null,
            10,
            TestContext.Current.CancellationToken)).Items).Name);
        Assert.Equal(quarantinedBefore, await _fixture.ReadStoredStateAsync("already-quarantined"));
    }
}
