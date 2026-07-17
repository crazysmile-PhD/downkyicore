using System.Text.Json;
using DownKyi.Core.Settings;

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
}
