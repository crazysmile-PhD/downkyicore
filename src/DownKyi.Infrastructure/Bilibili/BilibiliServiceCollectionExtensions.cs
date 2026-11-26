using System.Net;
using DownKyi.Application.Bilibili;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Infrastructure.Bilibili;

public sealed record BilibiliNetworkOptions(
    string UserAgent,
    bool UseProxy,
    string? ProxyAddress);

public static class BilibiliServiceCollectionExtensions
{
    internal const string HttpClientName = "DownKyi.Bilibili";
    internal const string LoginHttpClientName = "DownKyi.Bilibili.Login";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddDownKyiBilibiliInfrastructure(
        this IServiceCollection services,
        Func<IServiceProvider, BilibiliNetworkOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton(optionsFactory);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBilibiliLoginSessionFactory>(static provider =>
            new BilibiliLoginSessionFactory(
                provider.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton(static provider => new BilibiliHttpTransport(
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IBuvidProvider>(static provider =>
            new BilibiliBuvidProvider(
                provider.GetRequiredService<BilibiliHttpTransport>()));
        services.AddSingleton<IBilibiliApiClient>(static provider =>
            new BilibiliApiClient(
                provider.GetRequiredService<BilibiliHttpTransport>(),
                provider.GetRequiredService<IBilibiliCookieProvider>(),
                provider.GetRequiredService<IBuvidProvider>()));
        services.AddHttpClient(HttpClientName, ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(provider =>
                CreateHandler(provider.GetRequiredService<BilibiliNetworkOptions>()));
        services.AddHttpClient(LoginHttpClientName, ConfigureClient)
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var handler = CreateHandler(
                    provider.GetRequiredService<BilibiliNetworkOptions>());
                handler.AllowAutoRedirect = false;
                handler.UseCookies = false;
                return handler;
            });
        return services;
    }

    private static void ConfigureClient(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<BilibiliNetworkOptions>();
        // The transport owns the deadline for both headers and buffered response bodies.
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        client.DefaultRequestHeaders.Add(
            "accept-language",
            "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    internal static SocketsHttpHandler CreateHandler(BilibiliNetworkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            UseProxy = options.UseProxy
        };
        if (!options.UseProxy)
        {
            handler.Proxy = null;
        }
        else if (string.IsNullOrWhiteSpace(options.ProxyAddress))
        {
            handler.Proxy = HttpClient.DefaultProxy;
        }
        else if (Uri.TryCreate(options.ProxyAddress, UriKind.Absolute, out var proxyUri))
        {
            handler.Proxy = new WebProxy(proxyUri);
        }
        else
        {
            handler.UseProxy = false;
            handler.Proxy = null;
        }

        return handler;
    }
}
