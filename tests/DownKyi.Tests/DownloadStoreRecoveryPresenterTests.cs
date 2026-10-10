using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadStoreRecoveryPresenterTests
{
    [Fact]
    public async Task ConfirmedSchemaRecoveryBacksUpResetsAndRestarts()
    {
        var dialogs = new DialogStub(AppDialogOutcome.Accepted);
        var recovery = new RecoveryStub();
        var lifecycle = new LifecycleStub(restartSucceeded: true);
        var presenter = CreatePresenter(dialogs, recovery, lifecycle);

        await presenter.ConfirmResetAndRestartAsync(
            new DownloadStoreSchemaMismatchException("unsupported schema"),
            TestContext.Current.CancellationToken);

        Assert.True(recovery.Called);
        Assert.True(lifecycle.RestartCalled);
        Assert.Single(dialogs.Requests);
        Assert.Equal(AppDialog.Alert, dialogs.Requests[0].Dialog);
    }

    [Fact]
    public async Task RejectedRecoveryLeavesDatabaseAndProcessUntouched()
    {
        var dialogs = new DialogStub(AppDialogOutcome.Canceled);
        var recovery = new RecoveryStub();
        var lifecycle = new LifecycleStub(restartSucceeded: true);
        var presenter = CreatePresenter(dialogs, recovery, lifecycle);

        await presenter.ConfirmResetAndRestartAsync(
            new DownloadStoreSchemaMismatchException("unsupported schema"),
            TestContext.Current.CancellationToken);

        Assert.False(recovery.Called);
        Assert.False(lifecycle.RestartCalled);
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task NonSchemaFailureNeverOffersDestructiveRecovery()
    {
        var dialogs = new DialogStub(AppDialogOutcome.Accepted);
        var recovery = new RecoveryStub();
        var lifecycle = new LifecycleStub(restartSucceeded: true);
        var presenter = CreatePresenter(dialogs, recovery, lifecycle);

        await presenter.ConfirmResetAndRestartAsync(
            new InvalidOperationException("aria2 startup failed"),
            TestContext.Current.CancellationToken);

        Assert.False(recovery.Called);
        Assert.False(lifecycle.RestartCalled);
        Assert.Empty(dialogs.Requests);
    }

    private static DownloadStoreRecoveryPresenter CreatePresenter(
        IAppDialogService dialogs,
        IDownloadStoreRecovery recovery,
        IApplicationLifecycle lifecycle) =>
        new(
            dialogs,
            recovery,
            lifecycle,
            NullLogger<DownloadStoreRecoveryPresenter>.Instance);

    private sealed class DialogStub(AppDialogOutcome outcome) : IAppDialogService
    {
        public List<AppDialogRequest> Requests { get; } = [];

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new AppDialogResult(
                outcome,
                new Dictionary<string, object?>(StringComparer.Ordinal)));
        }
    }

    private sealed class RecoveryStub : IDownloadStoreRecovery
    {
        public bool Called { get; private set; }

        public Task BackupAndResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleStub(bool restartSucceeded) : IApplicationLifecycle
    {
        public bool RestartCalled { get; private set; }

        public Task RequestShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExitAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestartCalled = true;
            return Task.FromResult(restartSucceeded);
        }
    }
}
