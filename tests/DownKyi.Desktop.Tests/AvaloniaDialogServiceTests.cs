using Avalonia.Controls;
using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Platform;
using DownKyi.Services.Migration;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Desktop.Tests;

public sealed class AvaloniaDialogServiceTests
{
    [AvaloniaFact]
    public async Task LegacyDialogIsNotConstructedWithoutMigrationInput()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();
        await using var providerScope = provider.ConfigureAwait(true);
        var dialogService = new AvaloniaDialogService(
            new DialogContentFactory(provider),
            new AvaloniaDesktopContext(),
            new NoMigrationCoordinator());

        var result = await dialogService.ShowAsync(
            new AppDialogRequest(AppDialog.LegacyUpgrade),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(AppDialogOutcome.Canceled, result.Outcome);
    }

    [AvaloniaFact]
    public async Task SynchronousCloseDuringOpenReturnsWithoutShowingAClosedWindow()
    {
        DesktopTestResources.EnsureDownloadProjectionResources();
        DesktopTestResources.EnsureProductThemeResources();
        var failure = new InvalidOperationException("migration failed");
        var services = new ServiceCollection();
        services.AddSingleton<ILegacyUpgradeCoordinator>(
            new FailedMigrationCoordinator(failure));
        services.AddSingleton<IApplicationLifecycle>(new ApplicationLifecycleStub());
        services.AddSingleton<ILogger<ViewUpgradingDialogViewModel>>(
            NullLogger<ViewUpgradingDialogViewModel>.Instance);
        services.AddTransient<ViewUpgradingDialog>();
        services.AddTransient<ViewUpgradingDialogViewModel>();
        var provider = services.BuildServiceProvider();
        await using var providerScope = provider.ConfigureAwait(true);
        var context = new AvaloniaDesktopContext();
        var owner = new Window();
        context.AttachMainWindow(owner);
        var dialogService = new AvaloniaDialogService(
            new DialogContentFactory(provider),
            context,
            provider.GetRequiredService<ILegacyUpgradeCoordinator>());

        try
        {
            owner.Show();

            var result = await dialogService.ShowAsync(
                new AppDialogRequest(AppDialog.LegacyUpgrade),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(AppDialogOutcome.Rejected, result.Outcome);
            var failureResult = Assert.IsType<LegacyUpgradeResult>(
                result.Parameters[ViewUpgradingDialogViewModel.FailureResultParameter]);
            Assert.Same(failure, failureResult.Failure);
        }
        finally
        {
            owner.Close();
        }
    }

    private sealed class FailedMigrationCoordinator(Exception failure)
        : ILegacyUpgradeCoordinator
    {
        public Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LegacyUpgradeResult(
                LegacyUpgradeOutcome.Failed,
                "migration failed",
                failure));
        }
    }

    private sealed class NoMigrationCoordinator : ILegacyUpgradeCoordinator
    {
        public Task<bool> HasMigrationSourceAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Migration must not start without an input source.");
    }

    private sealed class ApplicationLifecycleStub : IApplicationLifecycle
    {
        public Task RequestShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ExitAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
