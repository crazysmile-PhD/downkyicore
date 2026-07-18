using System.Text.Json;
using DownKyi.Core.FileName;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed class SettingsStoreTests
{
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
    public void SynchronousDisposeStopsPendingDebounceWithoutPersisting()
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
            Assert.True(delay.Started.IsCompletedSuccessfully);

            manager.Dispose();

            Assert.True(delay.Canceled.IsCompletedSuccessfully);
            Assert.False(File.Exists(settingsPath));
        }
        finally
        {
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

    private sealed class ControlledDelay
    {
        private readonly TaskCompletionSource _canceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task Canceled => _canceled.Task;

        public async Task WaitAsync(TimeSpan _, CancellationToken cancellationToken)
        {
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
        }
    }
}
