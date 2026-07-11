using System.ComponentModel;
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
        public static int ListenPort; // 服务器端口
        private static Process? Server;

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
                    $"--log=\"{logFile}\" --log-level={config.LogLevel.ToString("G").ToLower()} " + // log-level: 'debug' 'info' 'notice' 'warn' 'error'
                    $"--max-concurrent-downloads={config.MaxConcurrentDownloads} " + // 最大同时下载数(任务数)
                    $"--max-connection-per-server={config.MaxConnectionPerServer} " + // 同服务器连接数
                    $"--split={config.Split} " + // 单文件最大线程数
                                                 //$"--max-tries={config.MaxTries} retry-wait=3 " + // 尝试重连次数
                    $"--min-split-size={config.MinSplitSize}M " + // 最小文件分片大小, 下载线程数上限取决于能分出多少片, 对于小文件重要
                    $"--max-overall-download-limit={config.MaxOverallDownloadLimit} " + // 下载速度限制
                    $"--max-download-limit={config.MaxDownloadLimit} " + // 下载单文件速度限制
                    $"--continue={config.ContinueDownload.ToString().ToLower()} " + // 断点续传
                    $"--allow-overwrite=true " + // 允许复写文件
                    $"--auto-file-renaming=false " +
                    $"--file-allocation={config.FileAllocation.ToString("G").ToLower()} " + // 文件预分配, none prealloc
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
            var server = Server;
            if (server == null)
            {
                return true;
            }

            try
            {
                if (!server.HasExited)
                {
                    await server.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
                }

                Server = null;
                return true;
            }
            catch (TimeoutException)
            {
                KillProcess(server, "aria2c did not exit before timeout.");
                Server = null;
                return false;
            }
        }

        /// <summary>
        /// 强制关闭aria2c服务器，异步方法
        /// </summary>
        /// <returns></returns>
        public static async Task<bool> ForceCloseServerAsync(TimeSpan? timeout = null)
        {
            //await Task.Run(() =>
            //{
            //    if (Server == null) { return; }

            //    Server.Kill();
            //    Server = null; // 将Server指向null
            //});
            //return true;
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

        /// <summary>
        /// 关闭aria2c服务器
        /// </summary>
        /// <returns></returns>
        public static bool CloseServer()
        {
            try
            {
                var result = AriaClient.ShutdownAsync().GetAwaiter().GetResult();
                if (result?.Result != "OK")
                {
                    return false;
                }

                WaitForExitOrKillAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                return true;
            }
            catch (InvalidOperationException e)
            {
                Console.PrintLine("CloseServer()发生异常: {0}", e);
                LogManager.Error("AriaServer", e);
                return false;
            }
            catch (Newtonsoft.Json.JsonException e)
            {
                Console.PrintLine("CloseServer()响应格式无效: {0}", e);
                LogManager.Error("AriaServer", e);
                return false;
            }
        }

        /// <summary>
        /// 强制关闭aria2c服务器
        /// </summary>
        /// <returns></returns>
        public static bool ForceCloseServer()
        {
            try
            {
                var result = AriaClient.ForceShutdownAsync().GetAwaiter().GetResult();
                return result?.Result == "OK";
            }
            catch (InvalidOperationException e)
            {
                Console.PrintLine("ForceCloseServer()发生异常: {0}", e);
                LogManager.Error("AriaServer", e);
                return false;
            }
            catch (Newtonsoft.Json.JsonException e)
            {
                Console.PrintLine("ForceCloseServer()响应格式无效: {0}", e);
                LogManager.Error("AriaServer", e);
                return false;
            }
        }

        /// <summary>
        /// 杀死Aria进程
        /// </summary>
        /// <param name="processName"></param>
        /// <returns></returns>
        public static bool KillServer(string processName = "aria2c")
        {
            Process[] processes = Process.GetProcessesByName(processName);
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException e)
                {
                    Console.PrintLine("KillServer()发生异常: {0}", e);
                    LogManager.Error("AriaServer", e);
                }
                catch (Win32Exception e)
                {
                    Console.PrintLine("KillServer()进程终止失败: {0}", e);
                    LogManager.Error("AriaServer", e);
                }
            }

            return true;
        }

        private static void KillProcess(Process process, string reason)
        {
            try
            {
                LogManager.Error("AriaServer", new TimeoutException(reason));
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException e)
            {
                Console.PrintLine("KillProcess()发生异常: {0}", e);
                LogManager.Error("AriaServer", e);
            }
            catch (Win32Exception e)
            {
                Console.PrintLine("KillProcess()进程终止失败: {0}", e);
                LogManager.Error("AriaServer", e);
            }
        }

        public static bool KillTrackedServer(string reason)
        {
            var server = Server;
            if (server == null)
            {
                return true;
            }

            KillProcess(server, reason);
            Server = null;
            return true;
        }

        internal static void SetTrackedServerForTests(Process? process)
        {
            Server = process;
        }

        internal static bool HasTrackedServerForTests()
        {
            return Server != null;
        }


        private static void ExecuteProcess(string exe, string arg, string? workingDirectory,
            DataReceivedEventHandler output)
        {
            var p = new Process();
            Server = p;

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

            // 启动线程
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
    }
}
