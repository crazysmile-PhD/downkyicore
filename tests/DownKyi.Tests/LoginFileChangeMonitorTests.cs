using DownKyi.Services.Account;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class LoginFileChangeMonitorTests
{
    public static TheoryData<string> Changes => new()
    {
        "create",
        "modify",
        "delete",
        "replace"
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task LoginFileChangeInvalidatesCookieCache(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-login-watch-{Guid.NewGuid():N}");
        var loginPath = Path.Combine(directory, "Login");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            Directory.CreateDirectory(directory);
            if (change != "create")
            {
                await File.WriteAllTextAsync(
                    loginPath,
                    "initial",
                    TestContext.Current.CancellationToken);
            }

            using var monitor = new LoginFileChangeMonitor(
                loginPath,
                () => invalidated.TrySetResult(),
                NullLogger<LoginFileChangeMonitor>.Instance);
            await monitor.StartAsync(TestContext.Current.CancellationToken);

            await ApplyChangeAsync(change, directory, loginPath);

            await invalidated.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
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
    public void WatcherErrorInvalidatesTheCookieCache()
    {
        var invalidationCount = 0;
        using var monitor = new LoginFileChangeMonitor(
            Path.Combine(Path.GetTempPath(), "Login"),
            () => invalidationCount++,
            NullLogger<LoginFileChangeMonitor>.Instance);

        monitor.OnWatcherError(
            monitor,
            new ErrorEventArgs(new InternalBufferOverflowException()));

        Assert.Equal(1, invalidationCount);
    }

    [Fact]
    public async Task WatcherStartupFailureDoesNotBlockFollowingHostedServices()
    {
        var logger = new RecordingLogger<LoginFileChangeMonitor>();
        var startupAttempts = 0;
        using var monitor = new LoginFileChangeMonitor(
            Path.Combine(Path.GetTempPath(), "Login"),
            () => { },
            logger,
            () =>
            {
                startupAttempts++;
                throw new IOException("watcher unavailable");
            });
        var followingService = new RecordingHostedService();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(monitor);
                services.AddSingleton<IHostedService>(followingService);
            })
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(followingService.Started);
        Assert.Equal(1, startupAttempts);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<IOException>(entry.Exception);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static Task ApplyChangeAsync(string change, string directory, string loginPath)
    {
        return change switch
        {
            "create" => File.WriteAllTextAsync(
                loginPath,
                "created",
                TestContext.Current.CancellationToken),
            "modify" => File.WriteAllTextAsync(
                loginPath,
                "modified",
                TestContext.Current.CancellationToken),
            "delete" => Task.Run(
                () => File.Delete(loginPath),
                TestContext.Current.CancellationToken),
            "replace" => ReplaceAsync(directory, loginPath),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
        };
    }

    private static async Task ReplaceAsync(string directory, string loginPath)
    {
        var replacementPath = Path.Combine(directory, "Login-replacement");
        await File.WriteAllTextAsync(
            replacementPath,
            "replacement",
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        File.Move(replacementPath, loginPath, overwrite: true);
    }

    private sealed class RecordingHostedService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception);
}
