using DownKyi.Application.Desktop;
using DownKyi.Composition;
using DownKyi.Core.Settings;
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Logging;
using DownKyi.Platform;
using DownKyi.Services.Download;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DownKyi.Tests;

public sealed class LocalModuleCompositionTests
{
    [Fact]
    public async Task ProductHostRejectsMissingDialogFactory()
    {
        await AssertInvalidProductCompositionAsync(services =>
            services.RemoveAll<DialogContentFactory>()).ConfigureAwait(true);
    }

    [Fact]
    public async Task ProductHostRejectsSingletonNavigationWithScopedFactory()
    {
        await AssertInvalidProductCompositionAsync(services =>
            services.Replace(ServiceDescriptor.Describe(
                typeof(NavigationViewModelFactory),
                typeof(NavigationViewModelFactory),
                ServiceLifetime.Scoped)))
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task ProductCompositionPreservesInteractionAndDownloadOwnerIdentity()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-local-composition-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(testDirectory, "downkyi.db");
        var settingsStore = new SettingsStore(Path.Combine(testDirectory, "settings.json"));
        var logProvider = new ApplicationLogProvider(
            new ApplicationLogOptions(Path.Combine(testDirectory, "logs")));
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        IHost? host = null;

        try
        {
            host = DownKyiHost.Create(services =>
            {
                services.AddDownKyiDesktop(loggerFactory, logProvider);
                services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(settingsStore));
                services.Replace(ServiceDescriptor.Singleton(
                    new SqliteDownloadTaskStoreOptions(databasePath)));
            });

            var queueGateway = host.Services.GetRequiredService<DownloadTaskQueueGateway>();
            Assert.Same(queueGateway, host.Services.GetRequiredService<IDownloadTaskQueue>());
            Assert.Same(queueGateway, host.Services.GetRequiredService<IDownloadRuntimeAvailability>());

            var bootstrap = host.Services.GetRequiredService<DownloadBootstrapHostedService>();
            var hostedBootstrap = host.Services
                .GetServices<IHostedService>()
                .OfType<DownloadBootstrapHostedService>()
                .Single();
            Assert.Same(bootstrap, hostedBootstrap);

            Assert.Same(
                host.Services.GetRequiredService<IAppNavigationService>(),
                host.Services.GetRequiredService<IAppNavigationService>());
            Assert.Same(
                host.Services.GetRequiredService<IAppDialogService>(),
                host.Services.GetRequiredService<IAppDialogService>());
        }
        finally
        {
            host?.Dispose();
            loggerFactory.Dispose();
            await logProvider.DisposeAsync().ConfigureAwait(true);
            await settingsStore.DisposeAsync().ConfigureAwait(true);
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static async Task AssertInvalidProductCompositionAsync(
        Action<IServiceCollection> invalidate)
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-invalid-composition-{Guid.NewGuid():N}");
        var logProvider = new ApplicationLogProvider(
            new ApplicationLogOptions(Path.Combine(testDirectory, "logs")));
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        try
        {
            var exception = Assert.Throws<AggregateException>(() => DownKyiHost.Create(services =>
            {
                services.AddDownKyiDesktop(loggerFactory, logProvider);
                invalidate(services);
            }));

            Assert.Contains(exception.InnerExceptions, inner => inner is InvalidOperationException);
        }
        finally
        {
            loggerFactory.Dispose();
            await logProvider.DisposeAsync().ConfigureAwait(true);
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
