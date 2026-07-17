using DownKyi.Core.Logging;
using DownKyi.Desktop.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prism.Container.DryIoc;
using Prism.Ioc;

namespace DownKyi.Tests;

public sealed class LoggingCompositionTests
{
    [Fact]
    public async Task PrismAndHostResolveTypedLoggersFromTheSharedFactory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logDirectory = Path.Combine(Path.GetTempPath(), "downkyi-logging-composition", Guid.NewGuid().ToString("N"));
        var provider = new ApplicationLogProvider(new ApplicationLogOptions(logDirectory));
        var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var prismContainer = new DryIocContainerExtension();
        prismContainer.RegisterInstance<ILoggerFactory>(factory);
        prismContainer.Register(typeof(ILogger<>), typeof(Logger<>));
        using var host = DownKyiHost.Create(services => services.AddSingleton<ILoggerFactory>(factory));

        try
        {
            Assert.NotNull(prismContainer.Resolve<ILogger<LoggingCompositionTests>>());
            Assert.NotNull(host.Services.GetRequiredService<ILogger<LoggingCompositionTests>>());
            Assert.Same(factory, host.Services.GetRequiredService<ILoggerFactory>());
            await host.StartAsync(cancellationToken).ConfigureAwait(true);
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            prismContainer.Instance.Dispose();
            factory.Dispose();
            await provider.DisposeAsync().ConfigureAwait(true);
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, true);
            }
        }
    }
}
