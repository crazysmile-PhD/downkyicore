using System.Diagnostics;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Settings;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Tests;

public sealed class Aria2RuntimeShutdownTests
{
    [Fact]
    public async Task IdleOwnedStopClosesThirtyConsecutiveProcessesBelowOneSecond()
    {
        var elapsed = new List<TimeSpan>();
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var requests = new List<string>();
            var client = CreateClient((method, _) =>
            {
                requests.Add(method);
                return method == "aria2.getGlobalStat"
                    ? CreateGlobalStat("0", "0")
                    : "OK";
            });
            var server = new AriaServer(NullLoggerFactory.Instance);
            using var process = StartLongRunningProcess();
            server.SetTrackedServerForTests(process);
            using var fixture = new LifecycleFixture(client, server);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await fixture.Lifecycle.StopAsync(TestContext.Current.CancellationToken);
                stopwatch.Stop();
                elapsed.Add(stopwatch.Elapsed);

                Assert.Equal(
                    ["aria2.pauseAll", "aria2.getGlobalStat", "aria2.saveSession"],
                    requests);
                Assert.True(process.HasExited);
                Assert.False(server.HasTrackedServerForTests());
            }
            finally
            {
                CleanupProcess(server, process);
            }
        }

        Assert.All(
            elapsed,
            duration => Assert.True(
                duration < TimeSpan.FromSeconds(1),
                $"Expected shutdown below one second, actual {duration.TotalMilliseconds:F0} ms."));
    }

    [Fact]
    public async Task OwnedStopWaitsForPausedCheckpointThenTerminatesWithoutRpcShutdownDelay()
    {
        var requests = new List<string>();
        var activeChecks = 0;
        var client = CreateClient((method, _) =>
        {
            requests.Add(method);
            return method switch
            {
                "aria2.getGlobalStat" => CreateGlobalStat(
                    activeChecks++ == 0 ? "1" : "0",
                    activeChecks == 1 ? "0" : "1"),
                _ => "OK"
            };
        });
        var server = new AriaServer(NullLoggerFactory.Instance);
        using var process = StartLongRunningProcess();
        server.SetTrackedServerForTests(process);
        using var fixture = new LifecycleFixture(client, server);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await fixture.Lifecycle.StopAsync(TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.Equal(
                [
                    "aria2.pauseAll",
                    "aria2.getGlobalStat",
                    "aria2.getGlobalStat",
                    "aria2.saveSession"
                ],
                requests);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"Expected shutdown below one second, actual {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
            Assert.True(process.HasExited);
            Assert.False(server.HasTrackedServerForTests());
        }
        finally
        {
            CleanupProcess(server, process);
        }
    }

    [Fact]
    public async Task OwnedStopFallsBackToRpcShutdownWhenSessionCheckpointFails()
    {
        var requests = new List<string>();
        Process? process = null;
        var client = CreateClient((method, _) =>
        {
            requests.Add(method);
            if (method == "aria2.getGlobalStat")
            {
                return CreateGlobalStat("0", "1");
            }

            if (method == "aria2.saveSession")
            {
                return "ERROR";
            }

            if (method == "aria2.shutdown" && process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            return "OK";
        });
        var server = new AriaServer(NullLoggerFactory.Instance);
        using var trackedProcess = StartLongRunningProcess();
        process = trackedProcess;
        server.SetTrackedServerForTests(trackedProcess);
        using var fixture = new LifecycleFixture(client, server);

        try
        {
            await fixture.Lifecycle.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                [
                    "aria2.pauseAll",
                    "aria2.getGlobalStat",
                    "aria2.saveSession",
                    "aria2.shutdown"
                ],
                requests);
            Assert.True(trackedProcess.HasExited);
            Assert.False(server.HasTrackedServerForTests());
        }
        finally
        {
            CleanupProcess(server, trackedProcess);
        }
    }

    [Fact]
    public async Task OwnedStopDoesNotTerminateWhenPausedCheckpointStateIsInvalid()
    {
        var requests = new List<string>();
        Process? process = null;
        var client = CreateClient((method, _) =>
        {
            requests.Add(method);
            if (method == "aria2.getGlobalStat")
            {
                return CreateGlobalStat("invalid", "0");
            }

            if (method == "aria2.shutdown" && process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            return "OK";
        });
        var server = new AriaServer(NullLoggerFactory.Instance);
        using var trackedProcess = StartLongRunningProcess();
        process = trackedProcess;
        server.SetTrackedServerForTests(trackedProcess);
        using var fixture = new LifecycleFixture(client, server);

        try
        {
            await fixture.Lifecycle.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                ["aria2.pauseAll", "aria2.getGlobalStat", "aria2.shutdown"],
                requests);
            Assert.True(trackedProcess.HasExited);
            Assert.False(server.HasTrackedServerForTests());
        }
        finally
        {
            CleanupProcess(server, trackedProcess);
        }
    }

    private static object CreateGlobalStat(string numActive, string numWaiting)
    {
        return new
        {
            downloadSpeed = "0",
            numActive,
            numStopped = "0",
            numStoppedTotal = "0",
            numWaiting,
            uploadSpeed = "0"
        };
    }

    private static AriaClient CreateClient(Func<string, JObject, object> resultFactory)
    {
        return new AriaClient(
            "http://localhost",
            6800,
            string.Empty,
            (_, payload, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = JObject.Parse(payload);
                var method = request["method"]?.Value<string>()
                    ?? throw new InvalidOperationException("The aria2 RPC method is missing.");
                return Task.FromResult<string?>(JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    id = request["id"]?.Value<string>(),
                    result = resultFactory(method, request)
                }));
            });
    }

    private static Process StartLongRunningProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(
                "powershell.exe",
                "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
            : new ProcessStartInfo("/bin/sh", "-c \"exec sleep 30\"");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("Could not start the shutdown test process.");
    }

    private static void CleanupProcess(AriaServer server, Process process)
    {
        server.SetTrackedServerForTests(null);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private readonly string _directory;
        private readonly SettingsStore _settings;

        public LifecycleFixture(AriaClient client, AriaServer server)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-aria-shutdown-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            _settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
            Lifecycle = new Aria2RuntimeLifecycle(
                _settings.Current.Network,
                client,
                new DownloadDiagnosticLogger(
                    NullLogger<DownloadDiagnosticLogger>.Instance),
                server,
                NullLogger<Aria2RuntimeLifecycle>.Instance,
                ownsAriaServer: true,
                new LocalAriaRpcEndpoint(6800, string.Empty));
        }

        public Aria2RuntimeLifecycle Lifecycle { get; }

        public void Dispose()
        {
            Lifecycle.Dispose();
            _settings.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
