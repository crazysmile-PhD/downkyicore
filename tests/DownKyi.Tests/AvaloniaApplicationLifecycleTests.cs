using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Core.Settings;
using DownKyi.Desktop.Composition;
using DownKyi.Platform;
using DownKyi.Services.Download;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class AvaloniaApplicationLifecycleTests
{
    [Fact]
    public void DownloadWorkerDeadlinePrecedesApplicationCleanupDeadline()
    {
        Assert.True(
            DownloadOrchestrator.WorkerShutdownTimeout <
            AvaloniaApplicationLifecycle.DefaultCleanupTimeout);
    }

    [Fact]
    public async Task RequestShutdownIsIdempotentAndFlushesOwnedState()
    {
        var directory = CreateTemporaryDirectory();
        var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
        using var host = DownKyiHost.Create();
        var logService = new RecordingLogService();
        var lifecycle = CreateLifecycle(settingsStore, logService, new StubRestartLauncher(false));
        lifecycle.AttachHost(host);

        try
        {
            await lifecycle.StartHostAsync().ConfigureAwait(true);

            var firstShutdown = lifecycle.RequestShutdownAsync(TestContext.Current.CancellationToken);
            var secondShutdown = lifecycle.RequestShutdownAsync(TestContext.Current.CancellationToken);

            Assert.Same(firstShutdown, secondShutdown);
            await firstShutdown.ConfigureAwait(true);
            Assert.True(host.Services
                .GetRequiredService<ApplicationCancellation>()
                .ShutdownToken
                .IsCancellationRequested);
            Assert.Equal(1, logService.FlushCount);
        }
        finally
        {
            await settingsStore.DisposeAsync().ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RequestShutdownWaitsForHostedServiceQuiescence()
    {
        var directory = CreateTemporaryDirectory();
        var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
        var hostedService = new BlockingStopHostedService();
        using var host = DownKyiHost.Create(services =>
            services.AddSingleton<IHostedService>(hostedService));
        var lifecycle = CreateLifecycle(
            settingsStore,
            new RecordingLogService(),
            new StubRestartLauncher(false));
        lifecycle.AttachHost(host);

        try
        {
            await lifecycle.StartHostAsync().ConfigureAwait(true);
            var shutdownTask = lifecycle.RequestShutdownAsync(TestContext.Current.CancellationToken);
            await hostedService.StopEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.False(shutdownTask.IsCompleted);
            Assert.False(hostedService.IsQuiescent);

            hostedService.AllowStop.TrySetResult();
            await shutdownTask.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.True(hostedService.IsQuiescent);
        }
        finally
        {
            hostedService.AllowStop.TrySetResult();
            await settingsStore.DisposeAsync().ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RequestShutdownFailsClosedWhenHostedServiceDoesNotBecomeQuiescent()
    {
        var directory = CreateTemporaryDirectory();
        var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
        var hostedService = new BlockingStopHostedService();
        using var host = DownKyiHost.Create(services =>
            services.AddSingleton<IHostedService>(hostedService));
        var logService = new RecordingLogService();
        var lifecycle = CreateLifecycle(
            settingsStore,
            logService,
            new StubRestartLauncher(false),
            TimeSpan.Zero);
        lifecycle.AttachHost(host);

        try
        {
            await lifecycle.StartHostAsync().ConfigureAwait(true);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                lifecycle.RequestShutdownAsync(TestContext.Current.CancellationToken));

            Assert.False(hostedService.IsQuiescent);
            Assert.Equal(1, logService.FlushCount);
        }
        finally
        {
            hostedService.AllowStop.TrySetResult();
            await host.StopAsync(CancellationToken.None).ConfigureAwait(true);
            await settingsStore.DisposeAsync().ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestartCleanupTimeoutTerminatesDesktopAndStillFailsClosed()
    {
        var directory = CreateTemporaryDirectory();
        var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
        var hostedService = new BlockingStopHostedService();
        using var host = DownKyiHost.Create(services =>
            services.AddSingleton<IHostedService>(hostedService));
        var restartLauncher = new StubRestartLauncher(true);
        var desktopShutdownCount = 0;
        var lifecycle = CreateLifecycle(
            settingsStore,
            new RecordingLogService(),
            restartLauncher,
            TimeSpan.Zero,
            () =>
            {
                desktopShutdownCount++;
                return Task.CompletedTask;
            });
        lifecycle.AttachHost(host);

        try
        {
            await lifecycle.StartHostAsync().ConfigureAwait(true);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                lifecycle.RestartAsync(TestContext.Current.CancellationToken));

            Assert.Equal(1, restartLauncher.StartCount);
            Assert.Equal(1, desktopShutdownCount);
            Assert.False(hostedService.IsQuiescent);
        }
        finally
        {
            hostedService.AllowStop.TrySetResult();
            await host.StopAsync(CancellationToken.None).ConfigureAwait(true);
            await settingsStore.DisposeAsync().ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestartCleanupFailureTerminatesDesktopAndPreservesFailure()
    {
        var directory = CreateTemporaryDirectory();
        var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
        using var host = DownKyiHost.Create(services =>
            services.AddSingleton<IHostedService>(new FatalStopHostedService()));
        var restartLauncher = new StubRestartLauncher(true);
        var desktopShutdownCount = 0;
        var lifecycle = CreateLifecycle(
            settingsStore,
            new RecordingLogService(),
            restartLauncher,
            desktopShutdown: () =>
            {
                desktopShutdownCount++;
                return Task.CompletedTask;
            });
        lifecycle.AttachHost(host);

        try
        {
            await lifecycle.StartHostAsync().ConfigureAwait(true);

            var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
                lifecycle.RestartAsync(TestContext.Current.CancellationToken));

            Assert.Equal("fatal stop failure", exception.Message);
            Assert.Equal(1, restartLauncher.StartCount);
            Assert.Equal(1, desktopShutdownCount);
        }
        finally
        {
            await settingsStore.DisposeAsync().ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestartHelperFailureKeepsRunningApplicationAlive()
    {
        var directory = CreateTemporaryDirectory();
        var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
        using var host = DownKyiHost.Create();
        var logService = new RecordingLogService();
        var restartLauncher = new StubRestartLauncher(false);
        var lifecycle = CreateLifecycle(settingsStore, logService, restartLauncher);
        lifecycle.AttachHost(host);

        try
        {
            await lifecycle.StartHostAsync().ConfigureAwait(true);

            var restarted = await lifecycle
                .RestartAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.False(restarted);
            Assert.Equal(1, restartLauncher.StartCount);
            Assert.False(host.Services
                .GetRequiredService<ApplicationCancellation>()
                .ShutdownToken
                .IsCancellationRequested);
            Assert.Equal(0, logService.FlushCount);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await settingsStore.DisposeAsync().ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HostedServiceStopFailureDoesNotBlockShutdownCompletion()
    {
        var directory = CreateTemporaryDirectory();
        var settingsStore = new SettingsStore(Path.Combine(directory, "settings.json"));
        using var host = DownKyiHost.Create(services =>
            services.AddSingleton<IHostedService>(new FailingStopHostedService()));
        var logService = new RecordingLogService();
        var lifecycle = CreateLifecycle(settingsStore, logService, new StubRestartLauncher(false));
        lifecycle.AttachHost(host);

        try
        {
            await lifecycle.StartHostAsync().ConfigureAwait(true);

            await lifecycle
                .RequestShutdownAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(1, logService.FlushCount);
            Assert.True(host.Services
                .GetRequiredService<ApplicationCancellation>()
                .ShutdownToken
                .IsCancellationRequested);
        }
        finally
        {
            await settingsStore.DisposeAsync().ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AvaloniaApplicationLifecycle CreateLifecycle(
        ISettingsStore settingsStore,
        IApplicationLogService logService,
        IProcessRestartLauncher restartLauncher,
        TimeSpan? cleanupTimeout = null,
        Func<Task>? desktopShutdown = null)
    {
        return cleanupTimeout != null || desktopShutdown != null
            ? new AvaloniaApplicationLifecycle(
                new AvaloniaDesktopContext(),
                restartLauncher,
                settingsStore,
                logService,
                NullLogger<AvaloniaApplicationLifecycle>.Instance,
                cleanupTimeout ?? AvaloniaApplicationLifecycle.DefaultCleanupTimeout,
                desktopShutdown)
            : new AvaloniaApplicationLifecycle(
                new AvaloniaDesktopContext(),
                restartLauncher,
                settingsStore,
                logService,
                NullLogger<AvaloniaApplicationLifecycle>.Instance);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StubRestartLauncher(bool result) : IProcessRestartLauncher
    {
        public int StartCount { get; private set; }

        public bool TryStartHelper(int parentProcessId)
        {
            Assert.True(parentProcessId > 0);
            StartCount++;
            return result;
        }
    }

    private sealed class RecordingLogService : IApplicationLogService
    {
        public int FlushCount { get; private set; }

        public string LogDirectory => string.Empty;

        public IReadOnlyList<ApplicationLogRecord> GetRecentEvents()
        {
            return [];
        }

        public ApplicationLogMetrics GetMetrics()
        {
            return new ApplicationLogMetrics(0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return Task.CompletedTask;
        }

        public Task<string> ExportDiagnosticLogAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FailingStopHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromException(new InvalidOperationException("stop failed"));
        }
    }

    private sealed class FatalStopHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromException(new NotSupportedException("fatal stop failure"));
        }
    }

    private sealed class BlockingStopHostedService : IHostedService
    {
        public TaskCompletionSource StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsQuiescent { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopEntered.TrySetResult();
            await AllowStop.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            IsQuiescent = true;
        }
    }
}
