using DownKyi.Application.Downloads;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.FFmpeg;
using DownKyi.Infrastructure.Storage;
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
        services.AddSingleton<IFfmpegMediaMuxer>(provider =>
            provider.GetRequiredService<FfmpegProcessor>());
        services.AddSingleton<IPhysicalOutputPathResolver, FileSystemPhysicalOutputPathResolver>();
        services.AddSingleton<SqliteDownloadTaskStore>();
        services.AddSingleton<IDownloadTaskStore>(provider =>
            provider.GetRequiredService<SqliteDownloadTaskStore>());
        services.AddSingleton<IDownloadHistoryStore>(provider =>
            provider.GetRequiredService<SqliteDownloadTaskStore>());
        services.AddSingleton<IDownloadCompletionStore>(provider =>
            provider.GetRequiredService<SqliteDownloadTaskStore>());
        services.AddSingleton<IDownloadStoreRecovery>(provider =>
            provider.GetRequiredService<SqliteDownloadTaskStore>());
        services.AddSingleton<IDownloadHistoryService, DownloadHistoryService>();
        services.AddSingleton<DownloadTaskApplicationService>();
        services.AddSingleton<IDownloadTaskApplicationService>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadTaskLookup>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadTaskCatalog>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadTaskSchedulerService>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadTaskStateService>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());
        services.AddSingleton<IDownloadOutputReservationService>(provider =>
            provider.GetRequiredService<DownloadTaskApplicationService>());

        services.AddSingleton<DownloadTaskProjectionStore>();
        services.AddSingleton<DownloadTaskStateWriter>();
        services.AddSingleton<DownloadTransferArtifactInvalidator>();
        services.AddSingleton<DownloadTaskQueueGateway>();
        services.AddSingleton<IDownloadTaskQueue>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<IDownloadSchedulerControl>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<IDownloadRuntimeAvailability>(provider =>
            provider.GetRequiredService<DownloadTaskQueueGateway>());
        services.AddSingleton<DownloadCommittedTaskDispatcher>();
        services.AddSingleton<DownloadStoreRecoveryPresenter>();
        services.AddSingleton<DownloadTaskAdmissionService>();
        services.AddSingleton(provider => new DownloadListState(
            provider.GetRequiredService<ISettingsStore>().Current.Basic.DownloadFinishedSort));
        services.AddSingleton<IDownloadedListProjection>(provider =>
            provider.GetRequiredService<DownloadListState>());
        services.AddSingleton<DownloadTaskFileService>();
        services.AddSingleton<DownloadTaskStaging>();
        services.AddSingleton<IDownloadManagerCoordinator, DownloadManagerCoordinator>();
        services.AddSingleton<DownloadMovieMetadataBuilder>();
        services.AddSingleton<DownloadContentConflictResolver>();
        services.AddSingleton<DownloadActionPlanner>();
        services.AddSingleton<IAddToDownloadServiceFactory, AddToDownloadServiceFactory>();
        services.AddSingleton<ISubtitleTrackLoader, SubtitleTrackLoader>();

        services.AddSingleton<IDownloadEmergencyCleanup, BuiltinDownloadEmergencyCleanup>();
        services.AddSingleton<DownloadDiagnosticLogger>();
        services.AddSingleton<IDownloadRuntimeFactory, DownloadRuntimeFactory>();
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<DownloadBootstrapHostedService>();
        services.AddSingleton<IHostedService>(provider =>
            provider.GetRequiredService<DownloadBootstrapHostedService>());
        return services;
    }
}
