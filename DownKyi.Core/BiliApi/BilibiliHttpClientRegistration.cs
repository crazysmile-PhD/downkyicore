using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Core.BiliApi;

public static class BilibiliHttpClientRegistration
{
    public static IServiceCollection AddDownKyiBilibiliHttpClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpClient<BilibiliHttpClient>(WebClient.ConfigureDefaults)
            .ConfigurePrimaryHttpMessageHandler(WebClient.CreateSocketsHandler);
        return services;
    }
}
