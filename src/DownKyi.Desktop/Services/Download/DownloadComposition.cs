using DownKyi.Application.Downloads;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.FFmpeg;
using DownKyi.Core.Storage;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DownKyi.Services.Download;

internal static class DownloadComposition
{
    public static IServiceCollection AddDownloadModule(this IServiceCollection services)
    {
        services.AddSingleton(new SqliteDownloadTaskStoreOptions(ApplicationStorage.GetDbPath()));
        services.AddSingleton<FfmpegProcessor>();
        services.AddSingleton<IPhysicalOutputPathResolver, FileSystemPhysicalOutputPathResolver>();
        services.AddSingleton<IDownloadTaskStore, SqliteDownloadTaskStore>();
        services.AddSingleton<IDownloadTaskApplicationService, DownloadTaskApplicationService>();

        services.AddSingleton<DownloadTaskProjectionStore>();
        services.AddSingleton<DownloadTaskStateWriter>();
        services.AddSingleton<DownloadTaskQueueGateway>();
        services.AddSingleton<IDownloadTaskQueue>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<IDownloadRuntimeAvailability>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<DownloadTaskAdmissionService>();
        services.AddSingleton<LegacyDownloadAdmissionPresenter>();
        services.AddSingleton<DownloadListState>();
        services.AddSingleton<DownloadTaskFileService>();
        services.AddSingleton<DownloadTaskStaging>();
        services.AddSingleton<AriaRuntimeClientRegistry>();
        services.AddSingleton<IDownloadManagerCoordinator, DownloadManagerCoordinator>();
        services.AddSingleton<DownloadDuplicatePolicy>();
        services.AddSingleton<DownloadMovieMetadataBuilder>();
        services.AddSingleton<IAddToDownloadServiceFactory, AddToDownloadServiceFactory>();

        services.AddSingleton<AriaServer>();
        services.AddSingleton<IDownloadEmergencyCleanup, AriaDownloadEmergencyCleanup>();
        services.AddSingleton<DownloadDiagnosticLogger>();
        services.AddSingleton<IDownloadRuntimeFactory, DownloadRuntimeFactory>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<DownloadBootstrapHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<DownloadBootstrapHostedService>());
        return services;
    }
}
