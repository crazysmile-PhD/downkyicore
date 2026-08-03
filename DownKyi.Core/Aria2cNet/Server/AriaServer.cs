using System.Diagnostics;
using System.Net.Http;
using System.Text;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Storage;
using Microsoft.Extensions.Logging;

namespace DownKyi.Core.Aria2cNet.Server
{
    public sealed class AriaServer
    {
        private readonly ILogger<AriaServer> _logger;
        private readonly AriaProcessSupervisor _processSupervisor;
        private AriaRpcSecretFile? _startupSecret;

        public AriaServer(ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);
            _logger = loggerFactory.CreateLogger<AriaServer>();
            _processSupervisor = new AriaProcessSupervisor(
                loggerFactory.CreateLogger<AriaProcessSupervisor>());
        }

        public int ListenPort { get; private set; } // 服务器端口

        /// <summary>
        /// 启动aria2c服务器
        /// </summary>
        /// <param name="config"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public async Task<bool> StartServerAsync(AriaConfig config, Action<string> action)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(action);

            // aria端口
            ListenPort = config.ListenPort;
            // aria目录
            var ariaDir = ApplicationStorage.GetAriaDir();
            // 会话文件
#if DEBUG
            var sessionFile = Path.Combine(ariaDir, "aira.session");

#else
            var sessionFile = Path.Combine(ariaDir, "aira.session.gz");
#endif
            // 日志文件
            var logFile = Path.Combine(ariaDir, "aira.log");
            // 自动保存会话文件的时间间隔
            var saveSessionInterval = 120;
            var startupSecret = AriaRpcSecretFile.Create(
                ariaDir,
                config.Token,
                _logger);
            Interlocked.Exchange(ref _startupSecret, startupSecret)?.Dispose();

            // The packaged runtime is process-local; custom remote aria2 endpoints are not started here.
            try
            {
                await Task.Run(() =>
                {
                    // 创建目录和文件
                    if (!Directory.Exists(ariaDir))
                    {
                        Directory.CreateDirectory(ariaDir);
                    }

                    if (!File.Exists(sessionFile))
                    {
                        using var stream = File.Create(sessionFile);
                    }

                    if (!File.Exists(logFile))
                    {
                        using var stream = File.Create(logFile);
                    }
                    else
                    {
                        // 日志文件存在，如果大于1M，则截断
                        try
                        {
                            using var stream = File.Open(logFile, FileMode.Open);
                            if (stream.Length >= 1 * 1024 * 1024L)
                            {
                                stream.SetLength(0);
                            }
                        }
                        catch (IOException e)
                        {
                            _logger.LogErrorMessage("aria2 log rotation failed.", e);
                        }
                        catch (UnauthorizedAccessException e)
                        {
                            _logger.LogErrorMessage("aria2 log rotation was denied.", e);
                        }
                        catch (InvalidOperationException e)
                        {
                            _logger.LogErrorMessage("aria2 log rotation encountered an invalid stream state.", e);
                        }
                    }

                    var executeName = "aria2c";

                    if (OperatingSystem.IsWindows())
                    {
                        executeName += ".exe";
                    }

                    var executablePath = Path.Combine(
                        AppContext.BaseDirectory,
                        "aria2",
                        executeName);
                    AriaBinaryIntegrityVerifier.Verify(executablePath);

                    ExecuteProcess(executablePath,
                        BuildArguments(
                            config,
                            startupSecret.Path,
                            sessionFile,
                            logFile,
                            saveSessionInterval,
                            Environment.ProcessId),
                        null, (s, e) =>
                        {
                            if (string.IsNullOrWhiteSpace(e.Data))
                            {
                                return;
                            }

                            _logger.LogDebugMessage(e.Data);

                            action.Invoke(e.Data);
                        });
                }).ConfigureAwait(false);
            }
            catch
            {
                KillTrackedServer("aria2 process startup failed.");
                throw;
            }

            return true;
        }

        public void ReleaseStartupSecrets()
        {
            Interlocked.Exchange(ref _startupSecret, null)?.Dispose();
        }

        public bool IsTrackedServerRunning()
        {
            return _processSupervisor.HasRunningProcess;
        }

        /// <summary>
        /// 关闭aria2c服务器，异步方法
        /// </summary>
        /// <returns></returns>
        public async Task<bool> CloseServerAsync(AriaClient ariaClient, TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(ariaClient);
            var waitTimeout = timeout ?? TimeSpan.FromSeconds(5);
            try
            {
                var shutdown = ariaClient.ShutdownAsync();
                var completed = await Task.WhenAny(shutdown, Task.Delay(waitTimeout)).ConfigureAwait(false);
                if (completed != shutdown)
                {
                    return KillTrackedServer("aria2 shutdown rpc timed out.");
                }

                var result = await shutdown.ConfigureAwait(false);
                if (result?.Result != "OK")
                {
                    return KillTrackedServer("aria2 shutdown rpc failed.");
                }

                var exited = await WaitForExitOrKillAsync(waitTimeout).ConfigureAwait(false);
                if (exited)
                {
                    ReleaseStartupSecrets();
                }

                return exited;
            }
            catch (HttpRequestException e)
            {
                _logger.LogErrorMessage("aria2 shutdown request failed.", e);
                return KillTrackedServer("aria2 shutdown failed.");
            }
            catch (InvalidOperationException e)
            {
                _logger.LogErrorMessage("aria2 shutdown encountered an invalid process state.", e);
                return KillTrackedServer("aria2 shutdown failed.");
            }
            catch (Newtonsoft.Json.JsonException e)
            {
                _logger.LogErrorMessage("aria2 shutdown returned an invalid response.", e);
                return KillTrackedServer("aria2 shutdown response was invalid.");
            }
        }

        private async Task<bool> WaitForExitOrKillAsync(TimeSpan timeout)
        {
            return await _processSupervisor.WaitForExitOrKillAsync(timeout).ConfigureAwait(false);
        }

        /// <summary>
        /// 强制关闭aria2c服务器，异步方法
        /// </summary>
        /// <returns></returns>
        public async Task<bool> ForceCloseServerAsync(AriaClient ariaClient, TimeSpan? timeout = null)
        {
            ArgumentNullException.ThrowIfNull(ariaClient);
            try
            {
                var waitTimeout = timeout ?? TimeSpan.FromSeconds(3);
                var shutdown = ariaClient.ForceShutdownAsync();
                var completed = await Task.WhenAny(shutdown, Task.Delay(waitTimeout)).ConfigureAwait(false);
                if (completed != shutdown)
                {
                    return KillTrackedServer("aria2 force shutdown rpc timed out.");
                }

                var result = await shutdown.ConfigureAwait(false);
                if (result?.Result != "OK")
                {
                    return KillTrackedServer("aria2 force shutdown rpc failed.");
                }

                var exited = await WaitForExitOrKillAsync(waitTimeout).ConfigureAwait(false);
                if (exited)
                {
                    ReleaseStartupSecrets();
                }

                return exited;
            }
            catch (HttpRequestException e)
            {
                _logger.LogErrorMessage("aria2 force-shutdown request failed.", e);
                return KillTrackedServer("aria2 force shutdown failed.");
            }
            catch (InvalidOperationException e)
            {
                _logger.LogErrorMessage("aria2 force shutdown encountered an invalid process state.", e);
                return KillTrackedServer("aria2 force shutdown failed.");
            }
            catch (Newtonsoft.Json.JsonException e)
            {
                _logger.LogErrorMessage("aria2 force shutdown returned an invalid response.", e);
                return KillTrackedServer("aria2 force shutdown response was invalid.");
            }
        }

        public bool KillTrackedServer(string reason)
        {
            var exited = _processSupervisor.Kill(reason);
            if (exited)
            {
                ReleaseStartupSecrets();
            }

            return exited;
        }

        internal void SetTrackedServerForTests(Process? process)
        {
            _processSupervisor.SetTrackedProcessForTests(process);
        }

        internal bool HasTrackedServerForTests()
        {
            return _processSupervisor.HasTrackedProcess;
        }

        internal void SetStartupSecretForTests(AriaRpcSecretFile? secretFile)
        {
            Interlocked.Exchange(ref _startupSecret, secretFile)?.Dispose();
        }

        internal static string GetLogLevelArgument(AriaConfigLogLevel logLevel)
        {
            return logLevel switch
            {
                AriaConfigLogLevel.NotSet => "notset",
                AriaConfigLogLevel.DEBUG => "debug",
                AriaConfigLogLevel.INFO => "info",
                AriaConfigLogLevel.NOTICE => "notice",
                AriaConfigLogLevel.WARN => "warn",
                AriaConfigLogLevel.ERROR => "error",
                _ => throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, "Unsupported aria2 log level.")
            };
        }

        internal static string GetFileAllocationArgument(AriaConfigFileAllocation fileAllocation)
        {
            return fileAllocation switch
            {
                AriaConfigFileAllocation.NotSet => "notset",
                AriaConfigFileAllocation.NONE => "none",
                AriaConfigFileAllocation.PREALLOC => "prealloc",
                AriaConfigFileAllocation.FALLOC => "falloc",
                _ => throw new ArgumentOutOfRangeException(nameof(fileAllocation), fileAllocation, "Unsupported aria2 file allocation mode.")
            };
        }

        internal static IReadOnlyList<string> BuildArguments(
            AriaConfig config,
            string secretConfigFile,
            string sessionFile,
            string logFile,
            int saveSessionInterval,
            int parentProcessId)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentException.ThrowIfNullOrWhiteSpace(secretConfigFile);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionFile);
            ArgumentException.ThrowIfNullOrWhiteSpace(logFile);
            ArgumentOutOfRangeException.ThrowIfNegative(saveSessionInterval);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(parentProcessId, 0);

            return
            [
                $"--conf-path={secretConfigFile}",
                "--enable-rpc",
                "--rpc-listen-all=false",
                "--rpc-allow-origin-all=false",
                $"--stop-with-process={parentProcessId}",
                $"--rpc-listen-port={config.ListenPort}",
                $"--input-file={sessionFile}",
                $"--save-session={sessionFile}",
                $"--save-session-interval={saveSessionInterval}",
                $"--log={logFile}",
                $"--log-level={GetLogLevelArgument(config.LogLevel)}",
                $"--max-concurrent-downloads={config.MaxConcurrentDownloads}",
                $"--max-connection-per-server={config.MaxConnectionPerServer}",
                $"--split={config.Split}",
                $"--min-split-size={config.MinSplitSize}M",
                $"--max-overall-download-limit={config.MaxOverallDownloadLimit}",
                $"--max-download-limit={config.MaxDownloadLimit}",
                $"--continue={(config.ContinueDownload ? "true" : "false")}",
                "--allow-overwrite=true",
                "--auto-file-renaming=false",
                $"--file-allocation={GetFileAllocationArgument(config.FileAllocation)}"
            ];
        }

        private void ExecuteProcess(
            string exe,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            DataReceivedEventHandler output)
        {
            var p = new Process();
            _processSupervisor.Track(p);

            p.StartInfo.FileName = exe;
            foreach (var argument in arguments)
            {
                p.StartInfo.ArgumentList.Add(argument);
            }

            // 工作目录
            if (workingDirectory != null)
            {
                p.StartInfo.WorkingDirectory = workingDirectory;
            }

            // 输出信息重定向
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.RedirectStandardOutput = true;

            // 将 StandardErrorEncoding 改为 UTF-8 才不会出现中文乱码
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            p.StartInfo.StandardErrorEncoding = Encoding.UTF8;

            p.OutputDataReceived += output;
            p.ErrorDataReceived += output;

            try
            {
                p.Start();
                _processSupervisor.BindToParentLifetime(p);
            }
            catch
            {
                _processSupervisor.SetTrackedProcessForTests(null);
                p.Dispose();
                throw;
            }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
    }
}
