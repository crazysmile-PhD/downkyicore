using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.Aria2cNet.Server;

namespace DownKyi.Tests;

internal sealed class Aria2TlsTestRuntime : IAsyncDisposable
{
    public const string SecureRedirectFeature = "downkyi-secure-redirect-v2";
    private readonly Action<string> _deleteDirectory;
    private readonly object _disposeSync = new();
    private readonly Aria2TlsProcessLifetime _processLifetime;
    private readonly IAria2TlsTrustedRoot _trustedRoot;
    private readonly string _workingDirectory;
    private Task? _disposeTask;

    internal Aria2TlsTestRuntime(
        Aria2TlsProcessLifetime processLifetime,
        AriaClient client,
        IAria2TlsTrustedRoot trustedRoot,
        string workingDirectory,
        string ariaVersion,
        string binarySha256,
        Action<string>? deleteDirectory = null)
    {
        _processLifetime = processLifetime;
        Client = client;
        _trustedRoot = trustedRoot;
        _workingDirectory = workingDirectory;
        AriaVersion = ariaVersion;
        BinarySha256 = binarySha256;
        _deleteDirectory = deleteDirectory ?? DeleteDirectory;
    }

    public AriaClient Client { get; }

    public string AriaVersion { get; }

    public string BinarySha256 { get; }

    public string CertificateAuthoritySource => _trustedRoot.Source;

    public static async Task<Aria2TlsTestRuntime> StartAsync(
        string binaryPath,
        X509Certificate2 trustedRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        ArgumentNullException.ThrowIfNull(trustedRoot);
        if (!File.Exists(binaryPath))
        {
            throw new FileNotFoundException("The aria2 integration binary was not found.", binaryPath);
        }

        AriaBinaryIntegrityVerifier.Verify(binaryPath);
        string binarySha256;
        var binary = File.OpenRead(binaryPath);
        await using (binary.ConfigureAwait(false))
        {
            binarySha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(binary, cancellationToken).ConfigureAwait(false));
        }

        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-aria2-tls-{Guid.NewGuid():N}");
        var rootPath = Path.Combine(workingDirectory, "trusted-root.pem");
        var rootCertificatePath = Path.Combine(workingDirectory, "trusted-root.cer");
        var port = GetAvailablePort();
        var rpcToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var secretFile = Path.Combine(workingDirectory, $".rpc-{Guid.NewGuid():N}.conf");
        var client = new AriaClient("http://127.0.0.1", port, rpcToken);
        TrustedRootScope? trustedRootScope = null;
        Aria2TlsProcessLifetime? processLifetime = null;
        Process? startedProcess = null;
        string? version = null;
        await Aria2TlsRuntimeStartup.RunAsync(
        [
            new Aria2TlsStartupStep(
                "working-directory",
                _ =>
                {
                    Directory.CreateDirectory(workingDirectory);
                    return Task.CompletedTask;
                },
                () =>
                {
                    DeleteDirectory(workingDirectory);
                    return Task.CompletedTask;
                }),
            new Aria2TlsStartupStep(
                "trusted-root-files",
                async cancellation =>
                {
                    await File.WriteAllTextAsync(
                        rootPath,
                        trustedRoot.ExportCertificatePem(),
                        cancellation).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(
                        rootCertificatePath,
                        trustedRoot.Export(X509ContentType.Cert),
                        cancellation).ConfigureAwait(false);
                }),
            new Aria2TlsStartupStep(
                "trusted-root-install",
                async cancellation =>
                {
                    trustedRootScope = await TrustedRootScope.InstallAsync(
                        trustedRoot,
                        rootPath,
                        rootCertificatePath,
                        cancellation).ConfigureAwait(false);
                },
                () => trustedRootScope!.DisposeAsync().AsTask()),
            new Aria2TlsStartupStep(
                "rpc-secret",
                cancellation => Aria2TlsRuntimeStartup.AcquireWithPartialRollbackAsync(
                    "rpc-secret",
                    async acquisitionCancellation =>
                    {
                        await File.WriteAllTextAsync(
                            secretFile,
                            $"rpc-secret={rpcToken}{Environment.NewLine}",
                            acquisitionCancellation).ConfigureAwait(false);
                        RestrictSecretFile(secretFile);
                    },
                    () =>
                    {
                        DeleteSecretFile(secretFile);
                        return Task.CompletedTask;
                    },
                    cancellation),
                () =>
                {
                    DeleteSecretFile(secretFile);
                    return Task.CompletedTask;
                }),
            new Aria2TlsStartupStep(
                "process",
                async _ =>
                {
                    var startInfo = CreateStartInfo(
                        binaryPath,
                        workingDirectory,
                        secretFile,
                        port);
                    var process = new Process { StartInfo = startInfo };
                    var startFailure = Record.Exception(() =>
                    {
                        if (!process.Start())
                        {
                            throw new InvalidOperationException(
                                "The aria2 TLS test process did not start.");
                        }
                    });
                    if (startFailure != null)
                    {
                        var failures = new Aria2TlsFailureCollector();
                        failures.Capture("runtime-startup/process", startFailure);
                        failures.Run(
                            "runtime-startup-rollback/process-dispose",
                            process.Dispose);
                        failures.ThrowIfAny();
                        throw new InvalidOperationException(
                            "Unreachable process startup failure path.");
                    }

                    startedProcess = process;
                    processLifetime = await Aria2TlsProcessLifetime.CreateAsync(
                        process,
                        () => client.ForceShutdownAsync(),
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                },
                () => processLifetime!.DisposeAsync().AsTask()),
            new Aria2TlsStartupStep(
                "rpc-ready",
                async cancellation =>
                {
                    version = await WaitForReadyAsync(
                        startedProcess!,
                        client,
                        cancellation).ConfigureAwait(false);
                }),
            new Aria2TlsStartupStep(
                "rpc-secret-delete",
                _ =>
                {
                    DeleteSecretFile(secretFile);
                    return Task.CompletedTask;
                })
        ], cancellationToken).ConfigureAwait(false);

        return new Aria2TlsTestRuntime(
            processLifetime!,
            client,
            trustedRootScope!,
            workingDirectory,
            version!,
            binarySha256);
    }

    public async Task<string> AddDownloadAsync(
        Uri url,
        string outputName,
        int split,
        int maximumTries,
        IReadOnlyList<string>? headers,
        CancellationToken cancellationToken)
    {
        return await AddDownloadAsync(
            url,
            outputName,
            split,
            maximumTries,
            headers,
            httpsProxy: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> AddDownloadAsync(
        Uri url,
        string outputName,
        int split,
        int maximumTries,
        IReadOnlyList<string>? headers,
        string? httpsProxy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        var result = await Client.AddUriAsync(
            [url.AbsoluteUri],
            new AriaSendOption
            {
                Dir = _workingDirectory,
                Out = outputName,
                Continue = "true",
                AllowOverwrite = "true",
                AutoFileRenaming = "false",
                Split = split.ToString(CultureInfo.InvariantCulture),
                MaxConnectionPerServer = split.ToString(CultureInfo.InvariantCulture),
                MinSplitSize = "1M",
                MaxTries = maximumTries.ToString(CultureInfo.InvariantCulture),
                RetryWait = "0",
                AlwaysResume = "false",
                MaxResumeFailureTries = "0",
                Headers = headers ?? [],
                HttpsProxy = httpsProxy ?? string.Empty
            }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Result
            ?? throw new InvalidOperationException("aria2 did not return a download identifier.");
    }

    public string GetOutputPath(string outputName)
    {
        return Path.Combine(_workingDirectory, outputName);
    }

    public async Task<AriaTellStatusResult> WaitForTerminalStatusAsync(
        string gid,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await Client.TellStatus(gid).ConfigureAwait(false);
            if (status.Result is { } result
                && (string.Equals(result.Status, "complete", StringComparison.Ordinal)
                    || string.Equals(result.Status, "error", StringComparison.Ordinal)
                    || string.Equals(result.Status, "removed", StringComparison.Ordinal)))
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("aria2 did not reach a terminal status before the test deadline.");
    }

    private static ProcessStartInfo CreateStartInfo(
        string binaryPath,
        string workingDirectory,
        string secretFile,
        int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        var arguments = new List<string>
        {
            $"--conf-path={secretFile}",
            "--enable-rpc=true",
            "--rpc-listen-all=false",
            "--rpc-allow-origin-all=false",
            $"--rpc-listen-port={port}",
            "--disable-ipv6=true",
            "--check-certificate=true",
            "--file-allocation=none",
            "--allow-overwrite=true",
            "--auto-file-renaming=false",
            "--continue=true",
            "--max-concurrent-downloads=4",
            "--max-connection-per-server=4",
            "--split=4",
            "--min-split-size=1M",
            "--max-tries=1",
            "--retry-wait=0",
            "--console-log-level=warn",
            "--summary-interval=0"
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<string> WaitForReadyAsync(
        Process process,
        AriaClient client,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The aria2 TLS test process exited before RPC became ready (code {process.ExitCode}).");
            }

            try
            {
                var response = await client
                    .GetAriaVersionAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(response.Result?.Version)
                    && response.Result.EnabledFeatures.Contains(
                        SecureRedirectFeature,
                        StringComparer.Ordinal))
                {
                    return response.Result.Version;
                }
            }
            catch (HttpRequestException error)
            {
                lastError = error;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "aria2 RPC did not become ready before the test deadline.",
            lastError);
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void RestrictSecretFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void DeleteSecretFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var length = new FileInfo(path).Length;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(length);
            stream.Write(new byte[length]);
            stream.Flush(flushToDisk: true);
        }

        File.Delete(path);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        await disposeTask.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new Aria2TlsFailureCollector();
        await _processLifetime.CleanupAsync(failures).ConfigureAwait(false);
        await failures.RunAsync(
            "trusted-root",
            () => _trustedRoot.DisposeAsync().AsTask()).ConfigureAwait(false);
        failures.Run("filesystem", () => _deleteDirectory(_workingDirectory));
        failures.ThrowIfAny();
    }
}

internal interface IAria2TlsTrustedRoot : IAsyncDisposable
{
    string Source { get; }
}

internal enum Aria2TlsHostPlatform
{
    Linux,
    Windows,
    MacOS
}

internal delegate Task Aria2TlsTrustCommand(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken);

internal interface IWindowsTrustedRootRegistration : IDisposable
{
    string Source { get; }
}

internal sealed class TrustedRootScope : IAria2TlsTrustedRoot
{
    private static readonly TimeSpan CleanupCommandTimeout = TimeSpan.FromSeconds(15);
    private const string MacSystemKeychain = "/Library/Keychains/System.keychain";
    private readonly string? _linuxCertificatePath;
    private readonly string? _macCommonName;
    private readonly Aria2TlsTrustCommand _runCommand;
    private readonly IWindowsTrustedRootRegistration? _windowsRoot;

    private TrustedRootScope(
        string source,
        Aria2TlsTrustCommand runCommand,
        IWindowsTrustedRootRegistration? windowsRoot = null,
        string? linuxCertificatePath = null,
        string? macCommonName = null)
    {
        Source = source;
        _runCommand = runCommand;
        _windowsRoot = windowsRoot;
        _linuxCertificatePath = linuxCertificatePath;
        _macCommonName = macCommonName;
    }

    public string Source { get; }

    public static async Task<TrustedRootScope> InstallAsync(
        X509Certificate2 root,
        string rootPemPath,
        string rootCertificatePath,
        CancellationToken cancellationToken)
    {
        return await InstallAsync(
            root,
            rootPemPath,
            rootCertificatePath,
            GetHostPlatform(),
            RunBoundedProcessAsync,
            InstallWindowsRoot,
            cancellationToken).ConfigureAwait(false);
    }

    private static IWindowsTrustedRootRegistration InstallWindowsRoot(byte[] certificateBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Windows trusted-root store is only available on Windows.");
        }

        return WindowsTrustedRootRegistration.Install(certificateBytes);
    }

    internal static async Task<TrustedRootScope> InstallAsync(
        X509Certificate2 root,
        string rootPemPath,
        string rootCertificatePath,
        Aria2TlsHostPlatform platform,
        Aria2TlsTrustCommand runCommand,
        Func<byte[], IWindowsTrustedRootRegistration> installWindowsRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPemPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootCertificatePath);
        ArgumentNullException.ThrowIfNull(runCommand);
        ArgumentNullException.ThrowIfNull(installWindowsRoot);

        if (platform == Aria2TlsHostPlatform.Linux)
        {
            var installedPath = Path.Combine(
                "/usr/local/share/ca-certificates",
                $"downkyi-aria2-{root.Thumbprint}.crt");
            var copyException = await Record.ExceptionAsync(
                () => runCommand(
                    "sudo",
                    ["-n", "install", "-m", "0644", "--", rootPemPath, installedPath],
                    cancellationToken)).ConfigureAwait(false);
            if (copyException != null)
            {
                await RollBackLinuxInstallAsync(
                    "trusted-root-install/copy",
                    copyException,
                    installedPath,
                    runCommand).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable trusted-root copy rollback path.");
            }

            var exception = await Record.ExceptionAsync(
                () => runCommand(
                    "sudo",
                    ["-n", "update-ca-certificates"],
                    cancellationToken)).ConfigureAwait(false);
            if (exception != null)
            {
                await RollBackLinuxInstallAsync(
                    "trusted-root-install",
                    exception,
                    installedPath,
                    runCommand).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable trusted-root rollback path.");
            }

            return new TrustedRootScope(
                "linux-system-ca-store",
                runCommand,
                linuxCertificatePath: installedPath);
        }

        if (platform == Aria2TlsHostPlatform.Windows)
        {
            var registration = installWindowsRoot(root.RawData);
            return new TrustedRootScope(
                registration.Source,
                runCommand,
                windowsRoot: registration);
        }

        var commonName = root.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var addException = await Record.ExceptionAsync(
            () => runCommand(
                "sudo",
                [
                    "-n",
                    "security",
                    "add-trusted-cert",
                    "-d",
                    "-r",
                    "trustRoot",
                    "-k",
                    MacSystemKeychain,
                    rootCertificatePath
                ],
                cancellationToken)).ConfigureAwait(false);
        if (addException != null)
        {
            var failures = new Aria2TlsFailureCollector();
            failures.Capture("trusted-root-install/macos-add", addException);
            await failures.RunAsync(
                "trusted-root-install-rollback/macos-delete",
                () => RunCleanupCommandAsync(
                    runCommand,
                    "sudo",
                    CreateMacDeleteArguments(commonName))).ConfigureAwait(false);
            failures.ThrowIfAny();
            throw new InvalidOperationException("Unreachable macOS trusted-root rollback path.");
        }

        return new TrustedRootScope(
            "macos-system-keychain",
            runCommand,
            macCommonName: commonName);
    }

    private static Aria2TlsHostPlatform GetHostPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return Aria2TlsHostPlatform.Linux;
        }

        return OperatingSystem.IsWindows()
            ? Aria2TlsHostPlatform.Windows
            : Aria2TlsHostPlatform.MacOS;
    }

    private static async Task RollBackLinuxInstallAsync(
        string primaryStage,
        Exception primaryException,
        string installedPath,
        Aria2TlsTrustCommand runCommand)
    {
        var failures = new Aria2TlsFailureCollector();
        failures.Capture(primaryStage, primaryException);
        await failures.RunAsync(
            "trusted-root-install-rollback/remove",
            () => RunCleanupCommandAsync(
                runCommand,
                "sudo",
                ["-n", "rm", "-f", "--", installedPath])).ConfigureAwait(false);
        await failures.RunAsync(
            "trusted-root-install-rollback/update",
            () => RunCleanupCommandAsync(
                runCommand,
                "sudo",
                ["-n", "update-ca-certificates"])).ConfigureAwait(false);
        failures.ThrowIfAny();
    }

    private static async Task RunCleanupCommandAsync(
        Aria2TlsTrustCommand runCommand,
        string fileName,
        IReadOnlyList<string> arguments)
    {
        using var deadline = new CancellationTokenSource(CleanupCommandTimeout);
        var command = runCommand(fileName, arguments, deadline.Token);
        try
        {
            await command.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            ObserveLateCommandFault(command);
            throw;
        }
    }

    private static void ObserveLateCommandFault(Task command)
    {
        _ = command.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static IReadOnlyList<string> CreateMacDeleteArguments(string commonName)
    {
        return
        [
            "-n",
            "security",
            "delete-certificate",
            "-c",
            commonName,
            MacSystemKeychain
        ];
    }

    private static async Task RunBoundedProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Process? process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The certificate trust tool did not start.");
        using var outputCancellation = new CancellationTokenSource();
        Task<string>? standardError = null;
        Task<string>? standardOutput = null;
        var failures = new Aria2TlsFailureCollector();
        try
        {
            var outputSetupFailure = Record.Exception(() =>
            {
                standardError = process.StandardError.ReadToEndAsync(outputCancellation.Token);
                standardOutput = process.StandardOutput.ReadToEndAsync(outputCancellation.Token);
            });
            if (outputSetupFailure != null)
            {
                failures.Capture("trusted-root-command-output-start", outputSetupFailure);
                await TerminateTrustCommandProcessAsync(process, failures).ConfigureAwait(false);
                await ObserveTrustCommandOutputAsync(
                    standardOutput ?? Task.FromResult(string.Empty),
                    standardError ?? Task.FromResult(string.Empty),
                    outputCancellation,
                    failures).ConfigureAwait(false);
            }
            else
            {
                var waitFailure = await Record.ExceptionAsync(
                    () => process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                if (waitFailure != null)
                {
                    if (waitFailure is OperationCanceledException
                        && timeout.IsCancellationRequested)
                    {
                        failures.Capture(
                            "trusted-root-command",
                            cancellationToken.IsCancellationRequested
                                ? waitFailure
                                : new TimeoutException(
                                    "The certificate trust tool did not finish in time.",
                                    waitFailure));
                    }
                    else
                    {
                        failures.Capture("trusted-root-command-wait", waitFailure);
                    }

                    await TerminateTrustCommandProcessAsync(process, failures)
                        .ConfigureAwait(false);
                }

                await ObserveTrustCommandOutputAsync(
                    standardOutput!,
                    standardError!,
                    outputCancellation,
                    failures).ConfigureAwait(false);
                if (waitFailure == null)
                {
                    failures.Run("trusted-root-command-exit", () =>
                    {
                        if (process.ExitCode != 0)
                        {
                            throw new InvalidOperationException(
                                $"The certificate trust command failed with code {process.ExitCode}.");
                        }
                    });
                }
            }
        }
        finally
        {
            failures.Run("trusted-root-command-process-dispose", process.Dispose);
            process = null;
            failures.Run("trusted-root-command-output-dispose", outputCancellation.Dispose);
        }

        failures.ThrowIfAny();
    }

    private static async Task TerminateTrustCommandProcessAsync(
        Process process,
        Aria2TlsFailureCollector failures)
    {
        failures.Run("trusted-root-command-kill", () =>
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        });
        await failures.RunAsync(
            "trusted-root-command-reap",
            async () =>
            {
                using var reapDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(reapDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                    when (reapDeadline.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "The certificate trust tool did not exit after termination.",
                        exception);
                }
            }).ConfigureAwait(false);
    }

    private static async Task ObserveTrustCommandOutputAsync(
        Task<string> standardOutput,
        Task<string> standardError,
        CancellationTokenSource outputCancellation,
        Aria2TlsFailureCollector failures)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outputObservation = ObserveTrustCommandStreamAsync(
            standardOutput,
            "trusted-root-command-stdout",
            deadline.Token);
        var errorObservation = ObserveTrustCommandStreamAsync(
            standardError,
            "trusted-root-command-stderr",
            deadline.Token);
        var observations = await Task.WhenAll(outputObservation, errorObservation)
            .ConfigureAwait(false);
        foreach (var observation in observations)
        {
            if (observation != null)
            {
                failures.Capture(observation.Value.Stage, observation.Value.Exception);
            }
        }

        if (observations.Any(observation => observation?.Exception is TimeoutException))
        {
            await failures.RunAsync(
                "trusted-root-command-output-cancel",
                () => outputCancellation.CancelAsync()).ConfigureAwait(false);
        }
    }

    private static async Task<(string Stage, Exception Exception)?> ObserveTrustCommandStreamAsync(
        Task<string> stream,
        string stage,
        CancellationToken cancellationToken)
    {
        var exception = await Record.ExceptionAsync(
            () => stream.WaitAsync(cancellationToken)).ConfigureAwait(false);
        if (exception == null)
        {
            return null;
        }

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return (stage, new TimeoutException(
                $"{stage} did not complete before the cleanup deadline."));
        }

        return (stage, exception);
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new Aria2TlsFailureCollector();
        if (_linuxCertificatePath != null)
        {
            await failures.RunAsync(
                "trusted-root-remove/linux-remove",
                () => RunCleanupCommandAsync(
                    _runCommand,
                    "sudo",
                    ["-n", "rm", "-f", "--", _linuxCertificatePath])).ConfigureAwait(false);
            await failures.RunAsync(
                "trusted-root-remove/linux-update",
                () => RunCleanupCommandAsync(
                    _runCommand,
                    "sudo",
                    ["-n", "update-ca-certificates"])).ConfigureAwait(false);
        }

        if (_windowsRoot != null)
        {
            failures.Run("trusted-root-remove/windows", _windowsRoot.Dispose);
        }

        if (_macCommonName != null)
        {
            await failures.RunAsync(
                "trusted-root-remove/macos",
                () => RunCleanupCommandAsync(
                    _runCommand,
                    "sudo",
                    CreateMacDeleteArguments(_macCommonName))).ConfigureAwait(false);
        }

        failures.ThrowIfAny();
    }
}

internal sealed class WindowsTrustedRootRegistration : IWindowsTrustedRootRegistration
{
    private const uint CertificateEncoding = 0x00000001;
    private const uint CurrentUserStore = 0x00010000;
    private const uint LocalMachineStore = 0x00020000;
    private const uint StoreAddUseExisting = 2;
    private readonly Func<IntPtr, bool> _closeStore;
    private readonly Func<IntPtr, bool> _deleteCertificate;
    private readonly Func<int> _getLastError;
    private IntPtr _certificateContext;
    private IntPtr _store;

    private WindowsTrustedRootRegistration(
        IntPtr store,
        IntPtr certificateContext,
        string source,
        Func<IntPtr, bool>? deleteCertificate = null,
        Func<IntPtr, bool>? closeStore = null,
        Func<int>? getLastError = null)
    {
        _store = store;
        _certificateContext = certificateContext;
        Source = source;
        _deleteCertificate = deleteCertificate ?? CertDeleteCertificateFromStore;
        _closeStore = closeStore ?? (handle => CertCloseStore(handle, flags: 0));
        _getLastError = getLastError ?? Marshal.GetLastPInvokeError;
    }

    public string Source { get; }

    internal static WindowsTrustedRootRegistration CreateForTest(
        Func<IntPtr, bool> deleteCertificate,
        Func<IntPtr, bool> closeStore,
        Func<int>? getLastError = null)
    {
        return new WindowsTrustedRootRegistration(
            new IntPtr(1),
            new IntPtr(2),
            "windows-test-root-store",
            deleteCertificate,
            closeStore,
            getLastError ?? (() => 5));
    }

    [SupportedOSPlatform("windows")]
    public static WindowsTrustedRootRegistration Install(byte[] certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
        var storeLocation = elevated ? LocalMachineStore : CurrentUserStore;
        var source = elevated
            ? "windows-local-machine-root-store"
            : "windows-current-user-root-store";
        var registration = TryInstall(
            certificate,
            storeLocation,
            source,
            out var error);
        return registration
               ?? throw CreateNativeError(
                   "The selected Windows root certificate store could not be updated.",
                   error);
    }

    private static WindowsTrustedRootRegistration? TryInstall(
        byte[] certificate,
        uint storeLocation,
        string source,
        out int error)
    {
        var store = CertOpenStore(
            new IntPtr(10),
            encodingType: 0,
            cryptographicProvider: IntPtr.Zero,
            storeLocation,
            "Root");
        if (store == IntPtr.Zero)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        if (CertAddEncodedCertificateToStore(
                store,
                CertificateEncoding,
                certificate,
                certificate.Length,
                StoreAddUseExisting,
                out var context))
        {
            error = 0;
            return new WindowsTrustedRootRegistration(store, context, source);
        }

        error = Marshal.GetLastPInvokeError();
        CertCloseStore(store, flags: 0);
        return null;
    }

    private static InvalidOperationException CreateNativeError(string message)
    {
        return CreateNativeError(message, Marshal.GetLastPInvokeError());
    }

    private static InvalidOperationException CreateNativeError(string message, int error)
    {
        return new InvalidOperationException($"{message} Native error code: {error}.");
    }

    public void Dispose()
    {
        var context = Interlocked.Exchange(ref _certificateContext, IntPtr.Zero);
        var store = Interlocked.Exchange(ref _store, IntPtr.Zero);
        var failures = new Aria2TlsFailureCollector();
        if (context != IntPtr.Zero)
        {
            failures.Run("trusted-root-remove/windows-delete", () =>
            {
                if (!_deleteCertificate(context))
                {
                    throw CreateNativeError(
                        "The Windows test root certificate could not be removed.",
                        _getLastError());
                }
            });
        }

        if (store != IntPtr.Zero)
        {
            failures.Run("trusted-root-remove/windows-close", () =>
            {
                if (!_closeStore(store))
                {
                    throw CreateNativeError(
                        "The Windows test root certificate store could not be closed.",
                        _getLastError());
                }
            });
        }

        failures.ThrowIfAny();
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CertOpenStore(
        IntPtr storeProvider,
        uint encodingType,
        IntPtr cryptographicProvider,
        uint flags,
        string storeName);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertAddEncodedCertificateToStore(
        IntPtr certificateStore,
        uint certificateEncodingType,
        byte[] certificate,
        int certificateLength,
        uint addDisposition,
        out IntPtr certificateContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertDeleteCertificateFromStore(IntPtr certificateContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CertCloseStore(IntPtr certificateStore, uint flags);
}
