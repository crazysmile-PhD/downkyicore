using System;
using System.Linq;
using DownKyi.Services.Download;

namespace DownKyi.Services.Startup;

internal enum StartupDiagnosticSource
{
    LegacyMigration,
    DownloadBootstrap
}

internal enum StartupDiagnosticKind
{
    LegacyMigrationFailed,
    DownloadRuntimeMissing,
    DownloadStoreSchemaMismatch,
    DownloadRuntimeFailed
}

internal sealed record StartupDiagnostic(
    StartupDiagnosticKind Kind,
    Exception Failure,
    string? Context = null)
{
    public StartupDiagnosticSource Source => Kind switch
    {
        StartupDiagnosticKind.LegacyMigrationFailed => StartupDiagnosticSource.LegacyMigration,
        StartupDiagnosticKind.DownloadRuntimeMissing
            or StartupDiagnosticKind.DownloadStoreSchemaMismatch
            or StartupDiagnosticKind.DownloadRuntimeFailed =>
            StartupDiagnosticSource.DownloadBootstrap,
        _ => throw new InvalidOperationException($"Unsupported startup diagnostic kind: {Kind}")
    };

    public bool AllowsDownloadStoreReset =>
        Kind == StartupDiagnosticKind.DownloadStoreSchemaMismatch;

    public bool AllowsBuiltInDownloaderFallback =>
        Kind == StartupDiagnosticKind.DownloadRuntimeMissing;

    public static StartupDiagnostic ForLegacyMigration(
        Exception failure,
        string? context = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new StartupDiagnostic(
            StartupDiagnosticKind.LegacyMigrationFailed,
            failure,
            context);
    }

    public static StartupDiagnostic ForDownloadBootstrap(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (DownloadStoreRecoveryPresenter.CanReset(failure))
        {
            return new StartupDiagnostic(
                StartupDiagnosticKind.DownloadStoreSchemaMismatch,
                failure);
        }

        var missingRuntime = Contains<FileNotFoundException>(failure);
        return new StartupDiagnostic(
            missingRuntime
                ? StartupDiagnosticKind.DownloadRuntimeMissing
                : StartupDiagnosticKind.DownloadRuntimeFailed,
            failure);
    }

    private static bool Contains<TException>(Exception failure)
        where TException : Exception
    {
        if (failure is TException)
        {
            return true;
        }

        if (failure is AggregateException aggregate
            && aggregate.InnerExceptions.Any(Contains<TException>))
        {
            return true;
        }

        return failure.InnerException != null && Contains<TException>(failure.InnerException);
    }
}
