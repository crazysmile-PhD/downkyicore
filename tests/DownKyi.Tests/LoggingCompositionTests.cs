using DownKyi.Core.Logging;
using DownKyi.Desktop.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownKyi.Tests;

public sealed class LoggingCompositionTests
{
    [Fact]
    public async Task HostResolvesTypedLoggersFromTheSharedFactory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-logging-composition",
            Guid.NewGuid().ToString("N"));
        var provider = new ApplicationLogProvider(new ApplicationLogOptions(logDirectory));
        var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var host = DownKyiHost.Create(services =>
        {
            services.AddSingleton<ILoggerFactory>(factory);
        });

        try
        {
            Assert.NotNull(host.Services.GetRequiredService<ILogger<LoggingCompositionTests>>());
            Assert.Same(factory, host.Services.GetRequiredService<ILoggerFactory>());
            await host.StartAsync(cancellationToken).ConfigureAwait(true);
            await host.StopAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            host.Dispose();
            factory.Dispose();
            await provider.DisposeAsync().ConfigureAwait(true);
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, true);
            }
        }
    }
}
