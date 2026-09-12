using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class ResolvePlaybackStage : IDownloadPipelineStage
{
    private readonly IUserNotificationService _notificationService;
    private readonly DownloadActivityPresenter _presenter;
    private readonly DownloadPlaybackResolver _playbackResolver;
    private readonly ILogger _logger;

    public ResolvePlaybackStage(
        IUserNotificationService notificationService,
        DownloadActivityPresenter presenter,
        DownloadPlaybackResolver playbackResolver,
        ILogger logger)
    {
        _notificationService = notificationService
            ?? throw new ArgumentNullException(nameof(notificationService));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _playbackResolver = playbackResolver
            ?? throw new ArgumentNullException(nameof(playbackResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => nameof(ResolvePlaybackStage);

    public async Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var playbackBasePath = context.Input.OutputBasePath
            .Replace("\\", "/", StringComparison.Ordinal);

        string path;
        try
        {
            path = GetDownloadDirectoryPath(playbackBasePath);
            Directory.CreateDirectory(path);
            if (context.StagingDirectory != null)
            {
                Directory.CreateDirectory(context.StagingDirectory);
                await AdoptRecordedTransfersAsync(context, cancellationToken).ConfigureAwait(true);
                path = context.StagingDirectory;
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            _logger.LogWarningMessage("Download directory could not be prepared.", exception);
            _notificationService.Show(DownloadActivityPresenter.CreateDirectoryError(
                Path.GetDirectoryName(context.Input.OutputBasePath) ?? string.Empty));
            return DownloadStageResult.Failure(
                "download.resolve.directory",
                "Download directory could not be prepared.");
        }

        context.DownloadDirectory = path;
        _presenter.Reset(context);
        await _presenter.ShowParsingAsync(context, cancellationToken).ConfigureAwait(true);

        if (context.NeedsMedia && context.TryReuseStagedMedia())
        {
            return DownloadStageResult.Success(Name);
        }

        if (context.PlayUrl != null)
        {
            return DownloadStageResult.Success(Name);
        }

        var playUrl = await _playbackResolver.ResolveAsync(
            context,
            cancellationToken).ConfigureAwait(true);
        if (playUrl == null)
        {
            return DownloadStageResult.Failure(
                "download.resolve.playback",
                "Playback data could not be resolved.");
        }

        context.PlayUrl = playUrl;
        return DownloadStageResult.Success(Name);
    }

    internal static string GetDownloadDirectoryPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Path.GetDirectoryName(filePath)
               ?? throw new ArgumentException(
                   "Download file path must include a directory.",
                   nameof(filePath));
    }

    internal static async Task AdoptRecordedTransfersAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stagingDirectory = context.StagingDirectory
            ?? throw new InvalidOperationException("Task staging is unavailable.");
        var legacyDirectory = GetDownloadDirectoryPath(context.Input.OutputBasePath);
        foreach (var fileName in context.Input.TransferFiles.Values)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName != Path.GetFileName(fileName))
            {
                continue;
            }

            await CopyIfMissingAsync(fileName, legacyDirectory, stagingDirectory, cancellationToken)
                .ConfigureAwait(false);
            await CopyIfMissingAsync(fileName + ".aria2", legacyDirectory, stagingDirectory,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyIfMissingAsync(
        string fileName,
        string sourceDirectory,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var source = Path.Combine(sourceDirectory, fileName);
        var destination = Path.Combine(stagingDirectory, fileName);
        if (!File.Exists(source) || File.Exists(destination))
        {
            return;
        }

        var temporary = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".adopting");
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                   FileShare.Read, 81920, FileOptions.Asynchronous))
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 81920, FileOptions.Asynchronous))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, destination, overwrite: false);
    }

}
