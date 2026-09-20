using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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
    private readonly Process _process;
    private readonly Task<string> _standardError;
    private readonly Task<string> _standardOutput;
    private readonly CancellationTokenSource _outputCancellation;
    private readonly TrustedRootScope _trustedRoot;
    private readonly string _workingDirectory;
    private bool _disposed;

    private Aria2TlsTestRuntime(
        Process process,
        Task<string> standardOutput,
        Task<string> standardError,
        CancellationTokenSource outputCancellation,
        AriaClient client,
        TrustedRootScope trustedRoot,
        string workingDirectory,
        string ariaVersion,
        string binarySha256)
    {
        _process = process;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _outputCancellation = outputCancellation;
        Client = client;
        _trustedRoot = trustedRoot;
        _workingDirectory = workingDirectory;
        AriaVersion = ariaVersion;
        BinarySha256 = binarySha256;
    }

    public AriaClient Client { get; }

    public string AriaVersion { get; }

    public string BinarySha256 { get; }

    public string CertificateAuthoritySource => _trustedRoot.Source;

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Startup must retain the primary failure while every acquired resource is rolled back.")]
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
        Directory.CreateDirectory(workingDirectory);
        var rootPath = Path.Combine(workingDirectory, "trusted-root.pem");
        await File.WriteAllTextAsync(
            rootPath,
            trustedRoot.ExportCertificatePem(),
            cancellationToken).ConfigureAwait(false);
        var rootCertificatePath = Path.Combine(workingDirectory, "trusted-root.cer");
        await File.WriteAllBytesAsync(
            rootCertificatePath,
            trustedRoot.Export(X509ContentType.Cert),
            cancellationToken).ConfigureAwait(false);
        var trustedRootScope = await TrustedRootScope.InstallAsync(
            trustedRoot,
            rootPath,
            rootCertificatePath,
            cancellationToken).ConfigureAwait(false);
        Process? process = null;
        var outputCancellation = new CancellationTokenSource();
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        try
        {
            var port = GetAvailablePort();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var secretFile = Path.Combine(workingDirectory, $".rpc-{Guid.NewGuid():N}.conf");
            await File.WriteAllTextAsync(
                secretFile,
                $"rpc-secret={token}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
            RestrictSecretFile(secretFile);

            var startInfo = CreateStartInfo(
                binaryPath,
                workingDirectory,
                secretFile,
                port);
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("The aria2 TLS test process did not start.");
            }

            standardOutput = process.StandardOutput.ReadToEndAsync(outputCancellation.Token);
            standardError = process.StandardError.ReadToEndAsync(outputCancellation.Token);
            var client = new AriaClient("http://127.0.0.1", port, token);
            var version = await WaitForReadyAsync(
                process,
                client,
                cancellationToken).ConfigureAwait(false);
            DeleteSecretFile(secretFile);
            return new Aria2TlsTestRuntime(
                process,
                standardOutput,
                standardError,
                outputCancellation,
                client,
                trustedRootScope,
                workingDirectory,
                version,
                binarySha256);
        }
        catch (Exception startupFailure)
        {
            var failures = new List<Exception> { startupFailure };
            if (process is not null)
            {
                try
                {
                    await Aria2TlsProcessCleanup.RunAsync(
                        CreateCleanupOperations(
                            process,
                            standardOutput ?? Task.FromResult(string.Empty),
                            standardError ?? Task.FromResult(string.Empty),
                            outputCancellation,
                            requestShutdownAsync: null),
                        TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    failures.Add(new InvalidOperationException(
                        "aria2 startup process cleanup failed.",
                        cleanupFailure));
                }
            }

            CaptureCleanupFailure(() => process?.Dispose(), "aria2 startup process disposal", failures);
            await CaptureCleanupFailureAsync(
                () => trustedRootScope.DisposeAsync().AsTask(),
                "aria2 startup trusted-root cleanup",
                failures).ConfigureAwait(false);
            CaptureCleanupFailure(
                () => DeleteDirectory(workingDirectory),
                "aria2 startup directory cleanup",
                failures);
            outputCancellation.Dispose();
            ThrowFailures(failures);
            throw new InvalidOperationException("Unreachable aria2 startup cleanup path.");
        }
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

    private static Aria2TlsProcessCleanupOperations CreateCleanupOperations(
        Process process,
        Task standardOutput,
        Task standardError,
        CancellationTokenSource outputCancellation,
        Func<CancellationToken, Task>? requestShutdownAsync)
    {
        return new Aria2TlsProcessCleanupOperations(
            () => process.HasExited,
            requestShutdownAsync,
            token => process.WaitForExitAsync(token),
            () => process.Kill(entireProcessTree: true),
            standardOutput,
            standardError,
            () => outputCancellation.CancelAsync());
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every cleanup failure is retained and reported after the remaining owners run.")]
    private static async Task CaptureCleanupFailureAsync(
        Func<Task> cleanup,
        string stage,
        List<Exception> failures)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException($"{stage} failed.", exception));
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every cleanup failure is retained and reported after the remaining owners run.")]
    private static void CaptureCleanupFailure(
        Action cleanup,
        string stage,
        List<Exception> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException($"{stage} failed.", exception));
        }
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("aria2 TLS runtime operation and cleanup failed.", failures);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var failures = new List<Exception>();
        await CaptureCleanupFailureAsync(
            () => Aria2TlsProcessCleanup.RunAsync(
                CreateCleanupOperations(
                    _process,
                    _standardOutput,
                    _standardError,
                    _outputCancellation,
                    token => Client.ForceShutdownAsync(token)),
                TimeSpan.FromSeconds(5)),
            "aria2 process cleanup",
            failures).ConfigureAwait(false);
        CaptureCleanupFailure(_process.Dispose, "aria2 process disposal", failures);
        _outputCancellation.Dispose();
        await CaptureCleanupFailureAsync(
            () => _trustedRoot.DisposeAsync().AsTask(),
            "aria2 trusted-root cleanup",
            failures).ConfigureAwait(false);
        CaptureCleanupFailure(
            () => DeleteDirectory(_workingDirectory),
            "aria2 directory cleanup",
            failures);

        if (failures.Count > 0)
        {
            ThrowFailures(failures);
        }
    }
}

internal sealed class TrustedRootScope : IAsyncDisposable
{
    private const string MacSystemKeychain = "/Library/Keychains/System.keychain";
    private readonly string? _linuxCertificatePath;
    private readonly string? _macCommonName;
    private readonly WindowsTrustedRootRegistration? _windowsRoot;

    private TrustedRootScope(
        string source,
        WindowsTrustedRootRegistration? windowsRoot = null,
        string? linuxCertificatePath = null,
        string? macCommonName = null)
    {
        Source = source;
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
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPemPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootCertificatePath);

        if (OperatingSystem.IsLinux())
        {
            var installedPath = Path.Combine(
                "/usr/local/share/ca-certificates",
                $"downkyi-aria2-{root.Thumbprint}.crt");
            await RunBoundedProcessAsync(
                "sudo",
                ["-n", "install", "-m", "0644", "--", rootPemPath, installedPath],
                cancellationToken).ConfigureAwait(false);
            try
            {
                await RunBoundedProcessAsync(
                    "sudo",
                    ["-n", "update-ca-certificates"],
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await RunBoundedProcessAsync(
                    "sudo",
                    ["-n", "rm", "-f", "--", installedPath],
                    CancellationToken.None).ConfigureAwait(false);
                await RunBoundedProcessAsync(
                    "sudo",
                    ["-n", "update-ca-certificates"],
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            return new TrustedRootScope(
                "linux-system-ca-store",
                linuxCertificatePath: installedPath);
        }

        if (OperatingSystem.IsWindows())
        {
            var registration = WindowsTrustedRootRegistration.Install(root.RawData);
            return new TrustedRootScope(
                registration.Source,
                windowsRoot: registration);
        }

        var commonName = root.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        await RunBoundedProcessAsync(
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
            cancellationToken).ConfigureAwait(false);
        return new TrustedRootScope(
            "macos-system-keychain",
            macCommonName: commonName);
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
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The certificate trust tool did not start.");
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The certificate trust tool did not finish in time.");
        }

        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The certificate trust command failed with code {process.ExitCode}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_linuxCertificatePath != null)
        {
            await RunBoundedProcessAsync(
                "sudo",
                ["-n", "rm", "-f", "--", _linuxCertificatePath],
                CancellationToken.None).ConfigureAwait(false);
            await RunBoundedProcessAsync(
                "sudo",
                ["-n", "update-ca-certificates"],
                CancellationToken.None).ConfigureAwait(false);
        }

        if (_windowsRoot != null)
        {
            _windowsRoot.Dispose();
        }

        if (_macCommonName != null)
        {
            await RunBoundedProcessAsync(
                "sudo",
                [
                    "-n",
                    "security",
                    "delete-certificate",
                    "-c",
                    _macCommonName,
                    MacSystemKeychain
                ],
                CancellationToken.None).ConfigureAwait(false);
        }
    }
}

internal sealed class WindowsTrustedRootRegistration : IDisposable
{
    private const uint CertificateEncoding = 0x00000001;
    private const uint CurrentUserStore = 0x00010000;
    private const uint LocalMachineStore = 0x00020000;
    private const uint StoreAddUseExisting = 2;
    private IntPtr _certificateContext;
    private IntPtr _store;

    private WindowsTrustedRootRegistration(
        IntPtr store,
        IntPtr certificateContext,
        string source)
    {
        _store = store;
        _certificateContext = certificateContext;
        Source = source;
    }

    public string Source { get; }

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
        var store = NativeMethods.CertOpenStore(
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

        if (NativeMethods.CertAddEncodedCertificateToStore(
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
        NativeMethods.CertCloseStore(store, flags: 0);
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
        try
        {
            if (context != IntPtr.Zero && !NativeMethods.CertDeleteCertificateFromStore(context))
            {
                throw CreateNativeError("The Windows test root certificate could not be removed.");
            }
        }
        finally
        {
            if (store != IntPtr.Zero)
            {
                NativeMethods.CertCloseStore(store, flags: 0);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr CertOpenStore(
            IntPtr storeProvider,
            uint encodingType,
            IntPtr cryptographicProvider,
            uint flags,
            string storeName);

        [DllImport("crypt32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CertAddEncodedCertificateToStore(
            IntPtr certificateStore,
            uint certificateEncodingType,
            byte[] certificate,
            int certificateLength,
            uint addDisposition,
            out IntPtr certificateContext);

        [DllImport("crypt32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CertDeleteCertificateFromStore(IntPtr certificateContext);

        [DllImport("crypt32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CertCloseStore(IntPtr certificateStore, uint flags);
    }
}
