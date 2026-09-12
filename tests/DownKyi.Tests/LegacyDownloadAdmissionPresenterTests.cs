using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class LegacyDownloadAdmissionPresenterTests
{
    [Fact]
    public async Task CancelLeavesGateBlockedWithoutCallingConfirmation()
    {
        var store = new GateStore();
        using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
        var dialogs = new RecordingDialogService(AppDialogOutcome.Canceled);
        var presenter = new LegacyDownloadAdmissionPresenter(tasks, dialogs);

        var allowed = await presenter.EnsureAdmissionAsync(TestContext.Current.CancellationToken);

        Assert.False(allowed);
        Assert.True(store.Blocked);
        Assert.Equal(0, store.ConfirmationCount);
        var request = Assert.Single(dialogs.Requests);
        Assert.Equal(AppDialog.Alert, request.Dialog);
        Assert.Equal(2, request.Parameters?["button_number"]);
    }

    [Fact]
    public async Task ConfirmationOnlyReleasesGateAndAllowsSameSessionWithoutAnotherDialog()
    {
        var store = new GateStore();
        using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
        var dialogs = new RecordingDialogService(AppDialogOutcome.Accepted);
        var presenter = new LegacyDownloadAdmissionPresenter(tasks, dialogs);

        Assert.True(await presenter.EnsureAdmissionAsync(TestContext.Current.CancellationToken));
        Assert.True(await presenter.EnsureAdmissionAsync(TestContext.Current.CancellationToken));

        Assert.False(store.Blocked);
        Assert.Equal(1, store.ConfirmationCount);
        Assert.Single(dialogs.Requests);
        Assert.Equal(0, store.TaskAddCount);
        Assert.Equal(0, store.QuarantineQueryCount);
    }

    [Fact]
    public async Task FailedConfirmationShowsExistingErrorAndLeavesGateBlocked()
    {
        var store = new GateStore
        {
            ConfirmationResult = OperationResult.Failure(new OperationError(
                "download.store.confirmation_failed",
                "The confirmation could not be persisted."))
        };
        using var tasks = new DownloadTaskApplicationService(store, new SystemClock());
        var dialogs = new RecordingDialogService(
            AppDialogOutcome.Accepted,
            AppDialogOutcome.Accepted);
        var presenter = new LegacyDownloadAdmissionPresenter(tasks, dialogs);

        var allowed = await presenter.EnsureAdmissionAsync(TestContext.Current.CancellationToken);

        Assert.False(allowed);
        Assert.True(store.Blocked);
        Assert.Equal(1, store.ConfirmationCount);
        Assert.Equal(2, dialogs.Requests.Count);
        Assert.Equal(
            "The confirmation could not be persisted.",
            dialogs.Requests[1].Parameters?["message"]);
        Assert.Equal(0, store.TaskAddCount);
        Assert.Equal(0, store.QuarantineQueryCount);
    }

    private sealed class RecordingDialogService(params AppDialogOutcome[] outcomes)
        : IAppDialogService
    {
        private int _nextOutcome;

        public List<AppDialogRequest> Requests { get; } = [];

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var outcome = outcomes[Math.Min(_nextOutcome, outcomes.Length - 1)];
            _nextOutcome++;
            return Task.FromResult(new AppDialogResult(
                outcome,
                new Dictionary<string, object?>()));
        }
    }

    private sealed class GateStore : IDownloadTaskStore
    {
        public bool Blocked { get; private set; } = true;

        public OperationResult ConfirmationResult { get; init; } = OperationResult.Success();

        public int ConfirmationCount { get; private set; }

        public int TaskAddCount { get; private set; }

        public int QuarantineQueryCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            TaskAddCount++;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult<DownloadTask?>(null);

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>([]);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> IsLegacyUpgradeAdmissionBlockedAsync(
            CancellationToken cancellationToken) => Task.FromResult(Blocked);

        public Task<OperationResult> ConfirmLegacyRemoteTasksStoppedAsync(
            CancellationToken cancellationToken)
        {
            ConfirmationCount++;
            if (ConfirmationResult.IsSuccess)
            {
                Blocked = false;
            }

            return Task.FromResult(ConfirmationResult);
        }

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadHistoryPage([], null));

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken)
        {
            QuarantineQueryCount++;
            return Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);
        }
    }
}
