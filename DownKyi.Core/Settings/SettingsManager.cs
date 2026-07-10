using System.Text;
using System.Threading;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings.Models;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;
using DownKyi.Core.Utils.Encryptor;
using Newtonsoft.Json;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.Settings;

public partial class SettingsManager
{
    private static readonly TimeSpan FlushDelay = TimeSpan.FromMilliseconds(750);

    private bool SetProperty<T>(T currentValue, T newValue, Action<T> setter)
    {
        if (!EqualityComparer<T>.Default.Equals(currentValue, newValue))
        {
            setter(newValue);
            ScheduleFlush();
            return true;
        }
        return true;
    }

    private static SettingsManager? _instance;

    private static readonly object _settingsLock = new();
    // 内存中保存一份配置
    private AppSettings _appSettings;

    // 设置的配置文件路径
    private readonly string _settingsName = StorageManager.GetSettings();

    // 密钥（用于旧版加密配置迁移）
    private readonly string password = "YO1J$4#p";

    // 防抖写：延迟 500ms 后真正落盘
    private Timer? _flushTimer;
    private bool _dirty;

    /// <summary>
    /// 获取 SettingsManager 实例（单例）
    /// </summary>
    public static SettingsManager GetInstance()
    {
        return _instance ??= new SettingsManager();
    }

    /// <summary>
    /// 隐藏构造函数，必须使用单例模式
    /// </summary>
    private SettingsManager()
    {
        _appSettings = LoadFromFile();
    }

    /// <summary>
    /// 从文件加载配置（仅在初始化时调用一次）
    /// </summary>
    private AppSettings LoadFromFile()
    {
        try
        {
            if (!File.Exists(_settingsName))
            {
                return CreateDefaultSettingsFile();
            }

            var jsonWordTemplate = File.ReadAllText(_settingsName, Encoding.UTF8);
            try
            {
                return JsonConvert.DeserializeObject<AppSettings>(jsonWordTemplate) ?? new AppSettings();
            }
            catch
            {
                // 尝试旧版加密格式
                try
                {
                    string decryptedJson = LegacySettingsDecryptor.Decrypt(jsonWordTemplate, password);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(decryptedJson);
                    if (settings != null)
                    {
                        // 迁移：以明文重新写入
                        var migrated = settings;
                        _appSettings = migrated;
                        WriteSettingsFile(migrated);
                        return migrated;
                    }
                }
                catch (Exception decryptEx)
                {
                    Console.PrintLine("配置文件解密失败: {0}", decryptEx.Message);
                    LogManager.Error("SettingsManager", decryptEx);
                }
            }
        }
        catch (Exception e)
        {
            Console.PrintLine("LoadFromFile()发生异常: {0}", e);
            LogManager.Error("SettingsManager", e);
        }
        return new AppSettings();
    }

    private AppSettings CreateDefaultSettingsFile()
    {
        var settings = new AppSettings();
        try
        {
            WriteSettingsFile(settings);
        }
        catch (Exception e)
        {
            Console.PrintLine("CreateDefaultSettingsFile()发生异常: {0}", e);
            LogManager.Error("SettingsManager", e);
        }

        return settings;
    }

    /// <summary>
    /// 触发防抖计时器：500ms 内多次调用只落盘一次
    /// </summary>
    private void ScheduleFlush()
    {
        lock (_settingsLock)
        {
            _dirty = true;
            _flushTimer ??= new Timer(static state => ((SettingsManager)state!).FlushNow(), this, Timeout.Infinite, Timeout.Infinite);
            _flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// 立即将内存配置写入磁盘
    /// </summary>
    private void FlushNow()
    {
        lock (_settingsLock)
        {
            if (!_dirty) return;
            try
            {
                WriteSettingsFile(_appSettings);
                _dirty = false;
            }
            catch (Exception e)
            {
                Console.PrintLine("FlushNow()发生异常: {0}", e);
                LogManager.Error("SettingsManager", e);
            }
        }
    }

    /// <summary>
    /// 强制立即将未落盘的配置写入磁盘（应用退出时调用）
    /// </summary>
    public void Flush()
    {
        lock (_settingsLock)
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            if (!_dirty) return;

            try
            {
                WriteSettingsFile(_appSettings);
                _dirty = false;
            }
            catch (Exception e)
            {
                Console.PrintLine("Flush()发生异常: {0}", e);
                LogManager.Error("SettingsManager", e);
            }
        }
    }

    private void WriteSettingsFile(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsName);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonConvert.SerializeObject(settings);
        var tempFile = $"{_settingsName}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_settingsName))
            {
                File.Replace(tempFile, _settingsName, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempFile, _settingsName);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception e)
            {
                LogManager.Error("SettingsManager", e);
            }
        }
    }
}
