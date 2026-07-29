using DownKyi.Core.Aria2cNet.Server;

namespace DownKyi.Core.Settings
{
    public partial class SettingsManager
    {
        // Aria服务器token
        private const string AriaToken = "downkyi";

        // Aria服务器host
        private const string AriaHost = "http://localhost";

        // Aria服务器端口号
        private const int AriaListenPort = 35076;

        // Aria日志等级
        private const AriaConfigLogLevel AriaLogLevel = AriaConfigLogLevel.WARN;

        // Aria单文件最大线程数
        private const int AriaSplit = 5;

        // Aria下载速度限制
        private const int AriaMaxOverallDownloadLimit = 0;

        // Aria下载单文件速度限制
        private const int AriaMaxDownloadLimit = 0;

        // Aria文件预分配
        private const AriaConfigFileAllocation AriaFileAllocation = AriaConfigFileAllocation.NONE;

        // Aria HttpProxy代理
        private const AllowStatus IsAriaHttpProxy = AllowStatus.No;
        private readonly string _ariaHttpProxy = string.Empty;
        private const int AriaHttpProxyListenPort = 0;

        /// <summary>
        /// 获取Aria服务器的token
        /// </summary>
        /// <returns></returns>
        public string GetAriaToken()
        {
            if (_appSettings.Network.AriaToken == null)
            {
                // 第一次获取，先设置默认值
                SetAriaToken(AriaToken);
                return AriaToken;
            }

            return _appSettings.Network.AriaToken;
        }

        /// <summary>
        /// 设置Aria服务器的token
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public bool SetAriaToken(string token)
        {
            return SetProperty(
                _appSettings.Network.AriaToken,
                token,
                v => _appSettings.Network.AriaToken = v);
        }

        /// <summary>
        /// 获取Aria服务器的host
        /// </summary>
        /// <returns></returns>
        public string GetAriaHost()
        {
            if (_appSettings.Network.AriaHost == null)
            {
                // 第一次获取，先设置默认值
                SetAriaHost(AriaHost);
                return AriaHost;
            }

            return _appSettings.Network.AriaHost;
        }

        /// <summary>
        /// 设置Aria服务器的host
        /// </summary>
        /// <param name="host"></param>
        /// <returns></returns>
        public bool SetAriaHost(string host)
        {
            return SetProperty(
                _appSettings.Network.AriaHost,
                host,
                v => _appSettings.Network.AriaHost = v);
        }

        /// <summary>
        /// 获取Aria服务器的端口号
        /// </summary>
        /// <returns></returns>
        public int GetAriaListenPort()
        {
            if (_appSettings.Network.AriaListenPort == -1)
            {
                // 第一次获取，先设置默认值
                SetAriaListenPort(AriaListenPort);
                return AriaListenPort;
            }

            return _appSettings.Network.AriaListenPort;
        }

        /// <summary>
        /// 设置Aria服务器的端口号
        /// </summary>
        /// <param name="ariaListenPort"></param>
        /// <returns></returns>
        public bool SetAriaListenPort(int ariaListenPort)
        {
            return SetProperty(
                _appSettings.Network.AriaListenPort,
                ariaListenPort,
                v => _appSettings.Network.AriaListenPort = v);
        }

        /// <summary>
        /// 获取Aria日志等级
        /// </summary>
        /// <returns></returns>
        public AriaConfigLogLevel GetAriaLogLevel()
        {
            if (_appSettings.Network.AriaLogLevel == AriaConfigLogLevel.NotSet)
            {
                // 第一次获取，先设置默认值
                SetAriaLogLevel(AriaLogLevel);
                return AriaLogLevel;
            }

            return _appSettings.Network.AriaLogLevel;
        }

        /// <summary>
        /// 设置Aria日志等级
        /// </summary>
        /// <param name="ariaLogLevel"></param>
        /// <returns></returns>
        public bool SetAriaLogLevel(AriaConfigLogLevel ariaLogLevel)
        {
            return SetProperty(
                _appSettings.Network.AriaLogLevel,
                ariaLogLevel,
                v => _appSettings.Network.AriaLogLevel = v);
        }

        /// <summary>
        /// 获取Aria单文件最大线程数
        /// </summary>
        /// <returns></returns>
        public int GetAriaSplit()
        {
            if (_appSettings.Network.AriaSplit == -1)
            {
                // 第一次获取，先设置默认值
                SetAriaSplit(AriaSplit);
                return AriaSplit;
            }

            return _appSettings.Network.AriaSplit;
        }

        /// <summary>
        /// 设置Aria单文件最大线程数
        /// </summary>
        /// <param name="ariaSplit"></param>
        /// <returns></returns>
        public bool SetAriaSplit(int ariaSplit)
        {
            return SetProperty(
                _appSettings.Network.AriaSplit,
                ariaSplit,
                v => _appSettings.Network.AriaSplit = v);
        }

        /// <summary>
        /// 获取Aria下载速度限制
        /// </summary>
        /// <returns></returns>
        public int GetAriaMaxOverallDownloadLimit()
        {
            if (_appSettings.Network.AriaMaxOverallDownloadLimit == -1)
            {
                // 第一次获取，先设置默认值
                SetAriaMaxOverallDownloadLimit(AriaMaxOverallDownloadLimit);
                return AriaMaxOverallDownloadLimit;
            }

            return _appSettings.Network.AriaMaxOverallDownloadLimit;
        }

        /// <summary>
        /// 设置Aria下载速度限制
        /// </summary>
        /// <param name="ariaMaxOverallDownloadLimit"></param>
        /// <returns></returns>
        public bool SetAriaMaxOverallDownloadLimit(int ariaMaxOverallDownloadLimit)
        {
            return SetProperty(
                _appSettings.Network.AriaMaxOverallDownloadLimit,
                ariaMaxOverallDownloadLimit,
                v => _appSettings.Network.AriaMaxOverallDownloadLimit = v);
        }

        /// <summary>
        /// 获取Aria下载单文件速度限制
        /// </summary>
        /// <returns></returns>
        public int GetAriaMaxDownloadLimit()
        {
            if (_appSettings.Network.AriaMaxDownloadLimit == -1)
            {
                // 第一次获取，先设置默认值
                SetAriaMaxDownloadLimit(AriaMaxDownloadLimit);
                return AriaMaxDownloadLimit;
            }

            return _appSettings.Network.AriaMaxDownloadLimit;
        }

        /// <summary>
        /// 设置Aria下载单文件速度限制
        /// </summary>
        /// <param name="ariaMaxDownloadLimit"></param>
        /// <returns></returns>
        public bool SetAriaMaxDownloadLimit(int ariaMaxDownloadLimit)
        {
            return SetProperty(
                _appSettings.Network.AriaMaxDownloadLimit,
                ariaMaxDownloadLimit,
                v => _appSettings.Network.AriaMaxDownloadLimit = v);
        }

        /// <summary>
        /// 获取Aria文件预分配
        /// </summary>
        /// <returns></returns>
        public AriaConfigFileAllocation GetAriaFileAllocation()
        {
            if (_appSettings.Network.AriaFileAllocation == AriaConfigFileAllocation.NotSet)
            {
                // 第一次获取，先设置默认值
                SetAriaFileAllocation(AriaFileAllocation);
                return AriaFileAllocation;
            }

            return _appSettings.Network.AriaFileAllocation;
        }

        /// <summary>
        /// 设置Aria文件预分配
        /// </summary>
        /// <param name="ariaFileAllocation"></param>
        /// <returns></returns>
        public bool SetAriaFileAllocation(AriaConfigFileAllocation ariaFileAllocation)
        {
            return SetProperty(
                _appSettings.Network.AriaFileAllocation,
                ariaFileAllocation,
                v => _appSettings.Network.AriaFileAllocation = v);
        }

        /// <summary>
        /// 获取是否开启Aria http代理
        /// </summary>
        /// <returns></returns>
        public AllowStatus GetIsAriaHttpProxy()
        {
            if (_appSettings.Network.IsAriaHttpProxy == AllowStatus.None)
            {
                // 第一次获取，先设置默认值
                SetIsAriaHttpProxy(IsAriaHttpProxy);
                return IsAriaHttpProxy;
            }

            return _appSettings.Network.IsAriaHttpProxy;
        }

        /// <summary>
        /// 设置是否开启Aria http代理
        /// </summary>
        /// <param name="isAriaHttpProxy"></param>
        /// <returns></returns>
        public bool SetIsAriaHttpProxy(AllowStatus isAriaHttpProxy)
        {
            return SetProperty(
                _appSettings.Network.IsAriaHttpProxy,
                isAriaHttpProxy,
                v => _appSettings.Network.IsAriaHttpProxy = v);
        }

        /// <summary>
        /// 获取Aria的http代理的地址
        /// </summary>
        /// <returns></returns>
        public string GetAriaHttpProxy()
        {
            if (_appSettings.Network.AriaHttpProxy == null)
            {
                // 第一次获取，先设置默认值
                SetAriaHttpProxy(_ariaHttpProxy);
                return _ariaHttpProxy;
            }

            return _appSettings.Network.AriaHttpProxy;
        }

        /// <summary>
        /// 设置Aria的http代理的地址
        /// </summary>
        /// <param name="ariaHttpProxy"></param>
        /// <returns></returns>
        public bool SetAriaHttpProxy(string ariaHttpProxy)
        {
            return SetProperty(
                _appSettings.Network.AriaHttpProxy,
                ariaHttpProxy,
                v => _appSettings.Network.AriaHttpProxy = v);
        }

        /// <summary>
        /// 获取Aria的http代理的端口
        /// </summary>
        /// <returns></returns>
        public int GetAriaHttpProxyListenPort()
        {
            if (_appSettings.Network.AriaHttpProxyListenPort == -1)
            {
                // 第一次获取，先设置默认值
                SetAriaHttpProxyListenPort(AriaHttpProxyListenPort);
                return AriaHttpProxyListenPort;
            }

            return _appSettings.Network.AriaHttpProxyListenPort;
        }

        /// <summary>
        /// 设置Aria的http代理的端口
        /// </summary>
        /// <param name="ariaHttpProxyListenPort"></param>
        /// <returns></returns>
        public bool SetAriaHttpProxyListenPort(int ariaHttpProxyListenPort)
        {
            return SetProperty(
                _appSettings.Network.AriaHttpProxyListenPort,
                ariaHttpProxyListenPort,
                v => _appSettings.Network.AriaHttpProxyListenPort = v);
        }
    }
}
