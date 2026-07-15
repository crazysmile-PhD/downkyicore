using DownKyi.Application.Lifetime;
using DownKyi.Application.Time;
using DownKyi.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DownKyi.Desktop.Composition;

public static class DownKyiHost
{
    public static IHost Create(Action<IServiceCollection>? configureDesktop = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });

        builder.Services.AddLogging();
        builder.Services.AddSingleton<ApplicationCancellation>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IHostedService>(services =>
            new ApplicationLifetimeCoordinator(services.GetRequiredService<ApplicationCancellation>()));
        configureDesktop?.Invoke(builder.Services);
        return builder.Build();
    }

    private sealed class ApplicationLifetimeCoordinator(ApplicationCancellation cancellation) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return cancellation.RequestShutdownAsync();
        }
    }
}
