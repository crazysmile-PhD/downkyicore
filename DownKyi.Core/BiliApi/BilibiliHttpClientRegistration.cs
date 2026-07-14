using DownKyi.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Core.BiliApi;

public static class BilibiliHttpClientRegistration
{
    public static IServiceCollection AddDownKyiBilibiliHttpClient(
        this IServiceCollection services,
        ISettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settingsStore);
        services.AddHttpClient<BilibiliHttpClient>(client => WebClient.ConfigureDefaults(client, settingsStore))
            .ConfigurePrimaryHttpMessageHandler(() => WebClient.CreateSocketsHandler(settingsStore));
        return services;
    }
}
