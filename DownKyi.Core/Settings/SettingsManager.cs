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

    private static readonly object _settingsLock = new object();
    // 内存中保存一份配置
    private AppSettings _appSettings;

    // 设置的配置文件路径
    private readonly string _settingsName = StorageManager.GetSettings();

    // 密钥（用于旧版加密配置迁移）
    private readonly string password = "YO1J$4#p";

    // 防抖写：延迟 500ms 后真正落盘
    private Timer? _flushTimer;
    private volatile bool _dirty;

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
            var jsonWordTemplate = File.ReadAllText(_settingsName, Encoding.UTF8);
            try
            {
                return JsonConvert.DeserializeObject<AppSettings>(jsonWordTemplate);
            }
            catch
            {
                // 尝试旧版加密格式
                try
                {
                    string decryptedJson = Encryptor.DecryptString(jsonWordTemplate, password);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(decryptedJson);
                    if (settings != null)
                    {
                        // 迁移：以明文重新写入
                        var migrated = settings;
                        _appSettings = migrated;
                        FlushNow();
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

    /// <summary>
    /// 触发防抖计时器：500ms 内多次调用只落盘一次
    /// </summary>
    private void ScheduleFlush()
    {
        _dirty = true;
        if (_flushTimer == null)
        {
            _flushTimer = new Timer(_ => FlushNow(), null, 500, Timeout.Infinite);
        }
        else
        {
            // 重置计时器
            _flushTimer.Change(500, Timeout.Infinite);
        }
    }

    /// <summary>
    /// 立即将内存配置写入磁盘
    /// </summary>
    private void FlushNow()
    {
        if (!_dirty) return;
        lock (_settingsLock)
        {
            if (!_dirty) return;
            try
            {
                var json = JsonConvert.SerializeObject(_appSettings);
                File.WriteAllText(_settingsName, json, Encoding.UTF8);
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
        _flushTimer?.Dispose();
        _flushTimer = null;
        FlushNow();
    }
}
