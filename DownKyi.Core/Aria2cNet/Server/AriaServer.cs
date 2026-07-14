using System.Diagnostics;
using System.Net.Http;
using System.Text;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Logging;
using DownKyi.Core.Storage;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.Aria2cNet.Server
{
    public static class AriaServer
    {
        public static int ListenPort { get; private set; } // 服务器端口
        private static readonly AriaProcessSupervisor ProcessSupervisor = new();

        /// <summary>
        /// 启动aria2c服务器
        /// </summary>
        /// <param name="config"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public static async Task<bool> StartServerAsync(AriaConfig config, Action<string> action)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(action);

            // aria端口
            ListenPort = config.ListenPort;
            // aria目录
            // var ariaDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aria");
            var ariaDir = StorageManager.GetAriaDir();
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

            // --enable-rpc --rpc-listen-all=true --rpc-allow-origin-all=true --continue=true
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
                        Console.PrintLine("StartServerAsync()发生IO异常: {0}", e);
                        LogManager.Error("AriaServer", e);
                    }
                    catch (UnauthorizedAccessException e)
                    {
                        Console.PrintLine("StartServerAsync()没有文件权限: {0}", e);
                        LogManager.Error("AriaServer", e);
                    }
                    catch (InvalidOperationException e)
                    {
                        Console.PrintLine("StartServerAsync()进程状态无效: {0}", e);
                        LogManager.Error("AriaServer", e);
                    }
                }

                // header 解析
                var headers = string.Empty;
                if (config.Headers != null)
                {
                    headers = config.Headers.Aggregate(headers,
                        (current, header) => current + $"--header=\"{header}\" ");
                }

                var executeName = "aria2c";

                if (OperatingSystem.IsWindows())
                {
                    executeName += ".exe";
                }

                ExecuteProcess($"aria2/{executeName}",
                    $"--enable-rpc --rpc-listen-all=true --rpc-allow-origin-all=true " +
                    $"--check-certificate=false " + // 解决问题 SSL/TLS handshake failure
                    $"--rpc-listen-port={config.ListenPort} " +
                    $"--rpc-secret={config.Token} " +
                    $"--input-file=\"{sessionFile}\" --save-session=\"{sessionFile}\" " +
                    $"--save-session-interval={saveSessionInterval} " +
                    $"--log=\"{logFile}\" --log-level={GetLogLevelArgument(config.LogLevel)} " + // log-level: 'debug' 'info' 'notice' 'warn' 'error'
                    $"--max-concurrent-downloads={config.MaxConcurrentDownloads} " + // 最大同时下载数(任务数)
                    $"--max-connection-per-server={config.MaxConnectionPerServer} " + // 同服务器连接数
                    $"--split={config.Split} " + // 单文件最大线程数
                                                 //$"--max-tries={config.MaxTries} retry-wait=3 " + // 尝试重连次数
                    $"--min-split-size={config.MinSplitSize}M " + // 最小文件分片大小, 下载线程数上限取决于能分出多少片, 对于小文件重要
                    $"--max-overall-download-limit={config.MaxOverallDownloadLimit} " + // 下载速度限制
                    $"--max-download-limit={config.MaxDownloadLimit} " + // 下载单文件速度限制
                    $"--continue={(config.ContinueDownload ? "true" : "false")} " + // 断点续传
                    $"--allow-overwrite=true " + // 允许复写文件
                    $"--auto-file-renaming=false " +
                    $"--file-allocation={GetFileAllocationArgument(config.FileAllocation)} " + // 文件预分配, none prealloc
                    $"{headers}" + // header
                    "",
                    null, (s, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data))
                        {
                            return;
                        }

                        Console.PrintLine(e.Data);
                        LogManager.Debug("AriaServer", e.Data);

                        action.Invoke(e.Data);
                    });
            }).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// 关闭aria2c服务器，异步方法
        /// </summary>
        /// <returns></returns>
        public static async Task<bool> CloseServerAsync(TimeSpan? timeout = null)
        {
            var waitTimeout = timeout ?? TimeSpan.FromSeconds(5);
            try
            {
                var shutdown = AriaClient.ShutdownAsync();
                var completed = await Task.WhenAny(shutdown, Task.Delay(waitTimeout)).ConfigureAwait(false);
                if (completed != shutdown)
                {
                    KillTrackedServer("aria2 shutdown rpc timed out.");
                    return false;
                }

                var result = await shutdown.ConfigureAwait(false);
                if (result?.Result != "OK")
                {
                    KillTrackedServer("aria2 shutdown rpc failed.");
                    return false;
                }

                return await WaitForExitOrKillAsync(waitTimeout).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                Console.PrintLine("CloseServerAsync()发生异常: {0}", e);
                LogManager.Error("AriaServer", e);
                KillTrackedServer("aria2 shutdown failed.");
                return false;
            }
            catch (InvalidOperationException e)
            {
                Console.PrintLine("CloseServerAsync()进程状态无效: {0}", e);
                LogManager.Error("AriaServer", e);
                KillTrackedServer("aria2 shutdown failed.");
                return false;
            }
            catch (Newtonsoft.Json.JsonException e)
            {
                Console.PrintLine("CloseServerAsync()响应格式无效: {0}", e);
                LogManager.Error("AriaServer", e);
                KillTrackedServer("aria2 shutdown response was invalid.");
                return false;
            }
        }

        private static async Task<bool> WaitForExitOrKillAsync(TimeSpan timeout)
        {
            return await ProcessSupervisor.WaitForExitOrKillAsync(timeout).ConfigureAwait(false);
        }

        /// <summary>
        /// 强制关闭aria2c服务器，异步方法
        /// </summary>
        /// <returns></returns>
        public static async Task<bool> ForceCloseServerAsync(TimeSpan? timeout = null)
        {
            try
            {
                var waitTimeout = timeout ?? TimeSpan.FromSeconds(3);
                var shutdown = AriaClient.ForceShutdownAsync();
                var completed = await Task.WhenAny(shutdown, Task.Delay(waitTimeout)).ConfigureAwait(false);
                if (completed != shutdown)
                {
                    KillTrackedServer("aria2 force shutdown rpc timed out.");
                    return false;
                }

                var result = await shutdown.ConfigureAwait(false);
                if (result?.Result != "OK")
                {
                    KillTrackedServer("aria2 force shutdown rpc failed.");
                    return false;
                }

                return await WaitForExitOrKillAsync(waitTimeout).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                Console.PrintLine("ForceCloseServerAsync()发生异常: {0}", e);
                LogManager.Error("AriaServer", e);
                KillTrackedServer("aria2 force shutdown failed.");
                return false;
            }
            catch (InvalidOperationException e)
            {
                Console.PrintLine("ForceCloseServerAsync()进程状态无效: {0}", e);
                LogManager.Error("AriaServer", e);
                KillTrackedServer("aria2 force shutdown failed.");
                return false;
            }
            catch (Newtonsoft.Json.JsonException e)
            {
                Console.PrintLine("ForceCloseServerAsync()响应格式无效: {0}", e);
                LogManager.Error("AriaServer", e);
                KillTrackedServer("aria2 force shutdown response was invalid.");
                return false;
            }
        }

        public static bool KillTrackedServer(string reason)
        {
            return ProcessSupervisor.Kill(reason);
        }

        internal static void SetTrackedServerForTests(Process? process)
        {
            ProcessSupervisor.SetTrackedProcessForTests(process);
        }

        internal static bool HasTrackedServerForTests()
        {
            return ProcessSupervisor.HasTrackedProcess;
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

        private static void ExecuteProcess(string exe, string arg, string? workingDirectory,
            DataReceivedEventHandler output)
        {
            var p = new Process();
            ProcessSupervisor.Track(p);

            p.StartInfo.FileName = exe;
            p.StartInfo.Arguments = arg;

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
            }
            catch
            {
                ProcessSupervisor.SetTrackedProcessForTests(null);
                p.Dispose();
                throw;
            }

            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
    }
}
