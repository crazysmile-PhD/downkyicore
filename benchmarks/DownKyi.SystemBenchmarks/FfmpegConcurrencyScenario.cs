using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using DownKyi.Core.FFMpeg;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.SystemBenchmarks;

internal static class FfmpegConcurrencyScenario
{
    public static async Task<IReadOnlyList<SystemBenchmarkResult>> RunAsync(
        int jobCount,
        int maximumConcurrency,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var results = new List<SystemBenchmarkResult>(2);
        var cpuArguments = CreateArguments(
            ["-c:v", "libx264", "-preset", "ultrafast"],
            duration);
        results.Add(await RunModeAsync(
            "cpu",
            "ffmpeg-libx264",
            cpuArguments,
            jobCount,
            maximumConcurrency,
            duration,
            cancellationToken).ConfigureAwait(false));

        var detector = new FfmpegHardwareEncoderDetector(
            NullLogger<FfmpegHardwareEncoderDetector>.Instance);
        var profile = await detector
            .SelectAsync(FfmpegHardwareAcceleration.Auto, cancellationToken)
            .ConfigureAwait(false);
        if (profile == null)
        {
            results.Add(UnavailableHardwareResult(jobCount, maximumConcurrency, duration));
        }
        else
        {
            var hardwareResult = await RunModeAsync(
                "gpu",
                $"ffmpeg-{profile.EncoderName}",
                CreateArguments(profile.OutputArguments, duration),
                jobCount,
                maximumConcurrency,
                duration,
                cancellationToken).ConfigureAwait(false);
            if (!hardwareResult.Available)
            {
                hardwareResult = hardwareResult with
                {
                    Notes = "The encoder is present in the FFmpeg build but the runtime hardware/driver probe failed; production must fall back to CPU encoding."
                };
            }

            results.Add(hardwareResult);
        }

        return results;
    }

    private static async Task<SystemBenchmarkResult> RunModeAsync(
        string mode,
        string backend,
        IReadOnlyList<string> arguments,
        int jobCount,
        int maximumConcurrency,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        var processes = new ConcurrentDictionary<int, Process>();
        var running = 0;
        var peakRunning = 0;
        var peakWorkingSet = 0L;
        var stopwatch = Stopwatch.StartNew();
        var jobs = Enumerable.Range(0, jobCount)
            .Select(_ => RunGatedProcessAsync())
            .ToArray();
        var completion = Task.WhenAll(jobs);
        while (!completion.IsCompleted)
        {
            var workingSet = 0L;
            foreach (var process in processes.Values)
            {
                try
                {
                    process.Refresh();
                    workingSet = checked(workingSet + process.WorkingSet64);
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                }
            }

            peakWorkingSet = Math.Max(peakWorkingSet, workingSet);
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }

        var succeeded = (await completion.ConfigureAwait(false)).Count(static result => result);
        stopwatch.Stop();
        return new SystemBenchmarkResult(
            $"ffmpeg_{mode}_concurrency",
            $"jobs={jobCount}; configured_concurrency={maximumConcurrency}; synthetic_duration_seconds={duration.TotalSeconds:0.###}; frame=640x360@30",
            backend,
            Available: succeeded == jobCount,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["requested_jobs"] = jobCount,
                ["successful_jobs"] = succeeded,
                ["configured_concurrency"] = maximumConcurrency,
                ["peak_concurrent_processes"] = peakRunning,
                ["peak_child_working_set_bytes"] = peakWorkingSet,
                ["elapsed_milliseconds"] = stopwatch.Elapsed.TotalMilliseconds
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requested_jobs"] = "count",
                ["successful_jobs"] = "count",
                ["configured_concurrency"] = "count",
                ["peak_concurrent_processes"] = "count",
                ["peak_child_working_set_bytes"] = "bytes",
                ["elapsed_milliseconds"] = "ms"
            },
            succeeded == jobCount
                ? "Actual FFmpeg child processes encoded a synthetic source; peak memory is the sampled sum of active child working sets and excludes the benchmark host."
                : "FFmpeg was missing or one or more synthetic encode processes failed; no successful hardware claim is inferred from encoder-list presence alone.");

        async Task<bool> RunGatedProcessAsync()
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var current = Interlocked.Increment(ref running);
            UpdateMaximum(ref peakRunning, current);
            try
            {
                return await RunProcessAsync(arguments, processes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref running);
                gate.Release();
            }
        }
    }

    private static async Task<bool> RunProcessAsync(
        IReadOnlyList<string> arguments,
        ConcurrentDictionary<int, Process> processes,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegExecutableLocator.Ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        int? processId = null;
        try
        {
            if (!process.Start())
            {
                return false;
            }

            processId = process.Id;
            processes[processId.Value] = process;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                      && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return false;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
        finally
        {
            if (processId is { } identifier)
            {
                processes.TryRemove(identifier, out _);
            }
        }
    }

    private static List<string> CreateArguments(
        IEnumerable<string> encoderArguments,
        TimeSpan duration)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", "testsrc2=size=640x360:rate=30",
            "-t", duration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        };
        arguments.AddRange(encoderArguments);
        arguments.AddRange(["-f", "null", "-"]);
        return arguments;
    }

    private static SystemBenchmarkResult UnavailableHardwareResult(
        int jobCount,
        int maximumConcurrency,
        TimeSpan duration)
    {
        return new SystemBenchmarkResult(
            "ffmpeg_gpu_concurrency",
            $"jobs={jobCount}; configured_concurrency={maximumConcurrency}; synthetic_duration_seconds={duration.TotalSeconds:0.###}; frame=640x360@30",
            "ffmpeg-hardware-unavailable",
            Available: false,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["requested_jobs"] = jobCount,
                ["successful_jobs"] = 0,
                ["configured_concurrency"] = maximumConcurrency,
                ["peak_concurrent_processes"] = 0,
                ["peak_child_working_set_bytes"] = 0,
                ["elapsed_milliseconds"] = 0
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requested_jobs"] = "count",
                ["successful_jobs"] = "count",
                ["configured_concurrency"] = "count",
                ["peak_concurrent_processes"] = "count",
                ["peak_child_working_set_bytes"] = "bytes",
                ["elapsed_milliseconds"] = "ms"
            },
            "No supported hardware encoder was exposed by the current FFmpeg build and OS; production fallback remains CPU encoding.");
    }

    private static void UpdateMaximum(ref int location, int candidate)
    {
        var current = Volatile.Read(ref location);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref location, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }
}
