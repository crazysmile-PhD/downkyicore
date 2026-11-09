using System.Text.Json;
using DownKyi.Application.FileName;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Infrastructure.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task RemovedParsingSettingsAreIgnoredWhenLegacyJsonIsSaved()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        const string source =
            """
            {"SchemaVersion":1,"Basic":{"IsAutoParseVideo":2,"IsAutoDownloadAll":2,"ParseScope":2,"IsListenClipboard":2}}
            """;

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                source,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);
            Assert.Equal(AllowStatus.Yes, store.Current.Basic.IsListenClipboard);

            store.Update(current => current with
            {
                Basic = current.Basic with { IsListenClipboard = AllowStatus.No }
            });
            await store.FlushAsync(TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken));
            var basic = document.RootElement.GetProperty("Basic");
            Assert.False(basic.TryGetProperty("IsAutoParseVideo", out _));
            Assert.False(basic.TryGetProperty("IsAutoDownloadAll", out _));
            Assert.False(basic.TryGetProperty("ParseScope", out _));
            Assert.Equal((int)AllowStatus.No, basic.GetProperty("IsListenClipboard").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LegacyDanmakuSettingsWithoutOutputFormatDefaultToAss()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        const string source = """
            {
              "Danmaku": {
                "DanmakuFontSize": 42
              }
            }
            """;

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                source,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);

            Assert.Equal(DanmakuOutputFormat.Ass, store.Current.Danmaku.OutputFormat);
            Assert.Equal(42, store.Current.Danmaku.FontSize);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SkipVersionAcceptsPrereleaseAndBuildMetadata()
    {
        var directory = CreateTestDirectory();
        try
        {
            using var store = new SettingsStore(Path.Combine(directory, "settings.json"));

            var updated = store.Update(settings => settings with
            {
                About = settings.About with
                {
                    SkipVersionOnLaunch = "v1.2.3-beta.1+build.9"
                }
            });

            Assert.Equal("1.2.3-beta.1+build.9", updated.About.SkipVersionOnLaunch);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LegacyPlainHttpSettingIsMigratedToRequiredHttps()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        const string source = """
            {
              "Network": {
                "UseSsl": 1
              }
            }
            """;

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                source,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);

            Assert.Null(typeof(NetworkApplicationSettings).GetProperty("UseSsl"));

            await store.FlushAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken));
            Assert.False(document.RootElement.GetProperty("Network").TryGetProperty(
                "UseSsl",
                out _));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task NewStoreDoesNotPersistDefaultsUntilASettingChanges()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using var store = new SettingsStore(settingsPath);
            await store.FlushAsync(TestContext.Current.CancellationToken);

            Assert.False(File.Exists(settingsPath));

            store.Update(settings => settings with
            {
                Basic = settings.Basic with { ThemeMode = ThemeMode.Dark }
            });
            await store.FlushAsync(TestContext.Current.CancellationToken);
            Assert.True(File.Exists(settingsPath));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task FlushAsyncPersistsChangesToTheOwnedSettingsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-settings-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using var store = new SettingsStore(settingsPath);

            store.Update(settings => settings with
            {
                Basic = settings.Basic with { ThemeMode = ThemeMode.Dark }
            });
            await store.FlushAsync(TestContext.Current.CancellationToken);

            await using var stream = File.OpenRead(settingsPath);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: TestContext.Current.CancellationToken);
            var persistedTheme = document.RootElement
                .GetProperty("Basic")
                .GetProperty("ThemeMode")
                .GetInt32();

            Assert.Equal((int)ThemeMode.Dark, persistedTheme);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SchemaZeroMigratesWithoutRenamingPersistedFields()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        var source = $$"""
            {
              "Basic": {
                "ThemeMode": {{(int)ThemeMode.Dark}}
              }
            }
            """;

        try
        {
            await File.WriteAllTextAsync(settingsPath, source, TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);

            Assert.Equal(ApplicationSettingsValidator.CurrentSchemaVersion, store.Current.SchemaVersion);
            Assert.Equal(ThemeMode.Dark, store.Current.Basic.ThemeMode);
            Assert.Equal(UiLanguage.ChineseSimplified, store.Current.Basic.UiLanguage);

            await store.FlushAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                ApplicationSettingsValidator.CurrentSchemaVersion,
                document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(
                (int)ThemeMode.Dark,
                document.RootElement.GetProperty("Basic").GetProperty("ThemeMode").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LegacyEncryptedSettingsMigrateToCurrentJson()
    {
        const string encryptedSettings =
            "UDJ8AeXk3SkFzjqDeR9NN7ixdDzvB+v4aiJqhU5G/JMye2ppve6HqFafbJsSlZWP6MppHjFhjSWbAe6zdh/FLopv05Gc9EFV";
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                encryptedSettings,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);

            Assert.Equal(ThemeMode.Dark, store.Current.Basic.ThemeMode);
            Assert.Equal(42, store.Current.User.Mid);
            Assert.Equal("legacy-user", store.Current.User.Name);

            await store.FlushAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                ApplicationSettingsValidator.CurrentSchemaVersion,
                document.RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LegacyAriaRateLimitsAreOwnedByBuiltInDownloaderSettings()
    {
        const string legacySettings =
            """{"Network":{"AriaMaxOverallDownloadLimit":321,"AriaMaxDownloadLimit":123}}""";
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                legacySettings,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);

            Assert.Equal(321, store.Current.Network.MaxOverallDownloadLimit);
            Assert.Equal(123, store.Current.Network.MaxDownloadLimit);

            store.Update(settings => settings with
            {
                Network = settings.Network with
                {
                    MaxOverallDownloadLimit = 654,
                    MaxDownloadLimit = 456
                }
            });
            await store.FlushAsync(TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken));
            var network = document.RootElement.GetProperty("Network");
            Assert.Equal(654, network.GetProperty("AriaMaxOverallDownloadLimit").GetInt32());
            Assert.Equal(456, network.GetProperty("AriaMaxDownloadLimit").GetInt32());
            Assert.False(network.TryGetProperty("MaxOverallDownloadLimit", out _));
            Assert.False(network.TryGetProperty("MaxDownloadLimit", out _));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task InvalidSettingsArePreservedBeforeDefaultsAreWritten()
    {
        const string invalidSettings = "not-json-or-legacy-settings";
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                invalidSettings,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);
            await store.FlushAsync(TestContext.Current.CancellationToken);

            var backup = Assert.Single(Directory.GetFiles(directory, "settings.json.invalid-*"));
            Assert.Equal(
                invalidSettings,
                await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                ApplicationSettingsValidator.CurrentSchemaVersion,
                document.RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task FutureSchemaIsNeverOverwrittenByAnOlderApplication()
    {
        const string futureSettings = "{\"SchemaVersion\":99,\"Basic\":{\"ThemeMode\":2}}";
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                futureSettings,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);
            store.Update(settings => settings with
            {
                Basic = settings.Basic with { ThemeMode = ThemeMode.Light }
            });
            await store.FlushAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                futureSettings,
                await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task UpdatesPublishImmutableValidatedSnapshots()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using var store = new SettingsStore(settingsPath);
            var original = store.Current;

            var updated = store.Update(settings => settings with
            {
                Network = settings.Network with
                {
                    MaxCurrentDownloads = 0,
                    NetworkProxy = NetworkProxy.Custom,
                    CustomNetworkProxy = "not-a-proxy"
                }
            });

            Assert.Equal(3, original.Network.MaxCurrentDownloads);
            Assert.Equal(3, updated.Network.MaxCurrentDownloads);
            Assert.Equal(NetworkProxy.None, updated.Network.NetworkProxy);
            Assert.Empty(updated.Network.CustomNetworkProxy);
            Assert.Same(updated, store.Current);

            await store.FlushAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task PublishedSnapshotsKeepNestedCollectionsImmutableAcrossUpdates()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using var store = new SettingsStore(settingsPath);
            var original = store.Current;

            var updated = store.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    HistoryVideoRootPaths = ["D:/DownKyi"],
                    FileNameParts = [FileNamePart.MainTitle]
                }
            });

            Assert.Empty(original.Video.HistoryVideoRootPaths);
            Assert.NotEqual(["D:/DownKyi"], original.Video.HistoryVideoRootPaths);
            Assert.Equal(["D:/DownKyi"], updated.Video.HistoryVideoRootPaths);
            Assert.Equal([FileNamePart.MainTitle], updated.Video.FileNameParts);
            Assert.NotEqual(updated.Video.FileNameParts, original.Video.FileNameParts);

            await store.FlushAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task TemporarySettingsValidationRejectsInvalidPayloads(string payload)
    {
        var directory = CreateTestDirectory();
        var temporaryPath = Path.Combine(directory, "settings.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                payload,
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<Newtonsoft.Json.JsonSerializationException>(() =>
                SettingsManager.ValidateTemporarySettingsFileAsync(
                    temporaryPath,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LegacySettersCannotPersistValuesRejectedByTheSnapshotContract()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using var manager = new SettingsManager(settingsPath);
            using var store = new SettingsStore(manager);
            manager.SetMaxCurrentDownloads(0);

            Assert.Equal(3, store.Current.Network.MaxCurrentDownloads);
            Assert.Equal(3, manager.GetMaxCurrentDownloads());

            await store.FlushAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CanceledFlushDoesNotLoseTheNextSuccessfulWrite()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using var store = new SettingsStore(settingsPath);
            store.Update(settings => settings with
            {
                Basic = settings.Basic with { ThemeMode = ThemeMode.Dark }
            });
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.FlushAsync(cancellation.Token));
            await store.FlushAsync(TestContext.Current.CancellationToken);

            Assert.True(File.Exists(settingsPath));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task AsyncDisposeFlushesPendingChanges()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            var store = new SettingsStore(settingsPath);
            store.Update(settings => settings with
            {
                Basic = settings.Basic with { ThemeMode = ThemeMode.Dark }
            });

            await store.DisposeAsync().ConfigureAwait(true);

            Assert.True(File.Exists(settingsPath));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                (int)ThemeMode.Dark,
                document.RootElement.GetProperty("Basic").GetProperty("ThemeMode").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task AsyncDisposeCancelsPendingDebounceAndFlushesWithoutWaitingForDelay()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        var delay = new ControlledDelay();

        try
        {
            var manager = new SettingsManager(
                settingsPath,
                NullLogger<SettingsManager>.Instance,
                TimeSpan.FromDays(1),
                delay.WaitAsync);
            manager.SetThemeMode(ThemeMode.Dark);
            await delay.Started.WaitAsync(TestContext.Current.CancellationToken);

            await manager.DisposeAsync().ConfigureAwait(true);

            await delay.Canceled.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(File.Exists(settingsPath));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
                settingsPath,
                TestContext.Current.CancellationToken));
            Assert.Equal(
                (int)ThemeMode.Dark,
                document.RootElement.GetProperty("Basic").GetProperty("ThemeMode").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SynchronousDisposeStopsPendingDebounceWithoutPersisting()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        var delay = new ControlledDelay();

        try
        {
            var manager = new SettingsManager(
                settingsPath,
                NullLogger<SettingsManager>.Instance,
                TimeSpan.FromDays(1),
                delay.WaitAsync);
            manager.SetThemeMode(ThemeMode.Dark);
            await delay.Started.WaitAsync(TestContext.Current.CancellationToken);

            DisposeSynchronously(manager);

            Assert.True(delay.CancellationRequested);
            await delay.Stopped.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(File.Exists(settingsPath));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitAsyncCanBeReopenedImmediatelyWithoutAnAdditionalFlush()
    {
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        const string selectedDirectory = "D:/DownKyi/Committed";

        try
        {
            using var store = new SettingsStore(settingsPath);

            var result = await store.CommitAsync(
                settings => settings with
                {
                    Video = settings.Video with { SaveVideoRootPath = selectedDirectory }
                },
                TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
            Assert.Equal(selectedDirectory, store.Current.Video.SaveVideoRootPath);
            using var reopened = new SettingsStore(settingsPath);
            Assert.Equal(selectedDirectory, reopened.Current.Video.SaveVideoRootPath);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitAsyncAccessDeniedKeepsThePublishedAndPersistedSettings()
    {
        const string source = """
            {"SchemaVersion":1,"Video":{"SaveVideoRootPath":"D:/DownKyi/Original"}}
            """;
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(settingsPath, source, TestContext.Current.CancellationToken);
            using var manager = new SettingsManager(
                settingsPath,
                NullLogger<SettingsManager>.Instance,
                TimeSpan.FromDays(1),
                static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
                static _ => throw new UnauthorizedAccessException("Simulated settings write denial."));
            using var store = new SettingsStore(manager);

            var result = await store.CommitAsync(
                settings => settings with
                {
                    Video = settings.Video with { SaveVideoRootPath = "D:/DownKyi/Denied" }
                },
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Equal(SettingsCommitFailure.WriteFailed, result.Failure);
            Assert.Equal("D:/DownKyi/Original", result.Settings.Video.SaveVideoRootPath);
            Assert.Equal("D:/DownKyi/Original", store.Current.Video.SaveVideoRootPath);
            Assert.Equal(
                source,
                await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitAsyncReportsDisabledPersistenceWithoutOverwritingFutureSettings()
    {
        const string futureSettings =
            "{\"SchemaVersion\":99,\"Video\":{\"SaveVideoRootPath\":\"D:/DownKyi/Future\"}}";
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                futureSettings,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);

            var result = await store.CommitAsync(
                settings => settings with
                {
                    Video = settings.Video with { SaveVideoRootPath = "D:/DownKyi/Rejected" }
                },
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Equal(SettingsCommitFailure.PersistenceDisabled, result.Failure);
            Assert.Equal("D:/DownKyi/Future", store.Current.Video.SaveVideoRootPath);
            Assert.Equal(
                futureSettings,
                await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitAsyncFailureBeforeAtomicReplaceLeavesOnlyTheCompletePreviousFile()
    {
        const string source = """
            {"SchemaVersion":1,"Video":{"SaveVideoRootPath":"D:/DownKyi/Stable"}}
            """;
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(settingsPath, source, TestContext.Current.CancellationToken);
            using var manager = new SettingsManager(
                settingsPath,
                NullLogger<SettingsManager>.Instance,
                TimeSpan.FromDays(1),
                static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
                static _ => throw new IOException("Simulated failure before atomic replacement."));
            using var store = new SettingsStore(manager);

            var result = await store.CommitAsync(
                settings => settings with
                {
                    Video = settings.Video with { SaveVideoRootPath = "D:/DownKyi/Incomplete" }
                },
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Equal(SettingsCommitFailure.WriteFailed, result.Failure);
            Assert.Equal("D:/DownKyi/Stable", store.Current.Video.SaveVideoRootPath);
            Assert.Equal(
                source,
                await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CommitAsyncRejectsSupersededCandidateBeforeWriting()
    {
        const string source =
            "{\"SchemaVersion\":1,\"Video\":{\"Quality\":100,\"AudioQuality\":30280,\"SaveVideoRootPath\":\"D:/DownKyi/Original\"}}";
        const string selectedDirectory = "D:/DownKyi/Committed";
        var directory = CreateTestDirectory();
        var settingsPath = Path.Combine(directory, "settings.json");
        var firstWriteReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var commitCandidateCreated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementAttempt = 0;

        try
        {
            await File.WriteAllTextAsync(settingsPath, source, TestContext.Current.CancellationToken);
            using var manager = new SettingsManager(
                settingsPath,
                NullLogger<SettingsManager>.Instance,
                TimeSpan.FromDays(1),
                static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
                async cancellationToken =>
                {
                    switch (Interlocked.Increment(ref replacementAttempt))
                    {
                        case 1:
                            firstWriteReached.TrySetResult();
                            await releaseFirstWrite.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                            break;
                        case 4:
                            throw new IOException("Simulated retry failure after a stale write.");
                    }
                });
            using var store = new SettingsStore(manager);
            store.Update(settings => settings with
            {
                Video = settings.Video with { AudioQuality = 30232 }
            });
            var flushTask = store.FlushAsync(TestContext.Current.CancellationToken);
            await firstWriteReached.Task.WaitAsync(TestContext.Current.CancellationToken);

            var commitTask = store.CommitAsync(
                settings =>
                {
                    commitCandidateCreated.TrySetResult();
                    return settings with
                    {
                        Video = settings.Video with { SaveVideoRootPath = selectedDirectory }
                    };
                },
                TestContext.Current.CancellationToken);
            await commitCandidateCreated.Task.WaitAsync(TestContext.Current.CancellationToken);
            store.Update(settings => settings with
            {
                Video = settings.Video with { Quality = 80 }
            });
            releaseFirstWrite.TrySetResult();

            await flushTask.ConfigureAwait(true);
            var result = await commitTask.ConfigureAwait(true);
            await store.FlushAsync(TestContext.Current.CancellationToken);
            using var reopened = new SettingsStore(settingsPath);

            Assert.True(result.Succeeded);
            Assert.Equal(80, store.Current.Video.Quality);
            Assert.Equal(selectedDirectory, store.Current.Video.SaveVideoRootPath);
            Assert.Equal(80, reopened.Current.Video.Quality);
            Assert.Equal(selectedDirectory, reopened.Current.Video.SaveVideoRootPath);
            Assert.Equal(3, Volatile.Read(ref replacementAttempt));
        }
        finally
        {
            releaseFirstWrite.TrySetResult();
            DeleteDirectory(directory);
        }
    }

    private static string CreateTestDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DisposeSynchronously(SettingsManager manager)
    {
        manager.Dispose();
    }

    private sealed class ControlledDelay
    {
        private readonly TaskCompletionSource _canceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _cancellationToken;

        public Task Started => _started.Task;

        public Task Canceled => _canceled.Task;

        public bool CancellationRequested => _cancellationToken.IsCancellationRequested;

        public Task Stopped => _stopped.Task;

        public async Task WaitAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _started.TrySetResult();
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _canceled);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                _stopped.TrySetResult();
            }
        }
    }
}
