using System.Net;

namespace DownKyi.Core.Settings
{
    public partial class SettingsManager
    {
        // 是否开启解除地区限制
        private const AllowStatus IsLiftingOfRegion = AllowStatus.Yes;

        // 启用https
        private const AllowStatus UseSsl = AllowStatus.Yes;

        // UserAgent
        private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        // 下载器
        private const Downloader Downloader = Settings.Downloader.Aria;

        private const NetworkProxy NetworkProxy = Settings.NetworkProxy.None;
        private readonly string _customNetworkProxy = string.Empty;

        // 最大同时下载数(任务数)
        private const int MaxCurrentDownloads = 3;

        // 单文件最大线程数
        private const int Split = 8;

        // HttpProxy代理
        private const AllowStatus IsHttpProxy = AllowStatus.No;
        private readonly string _httpProxy = string.Empty;
        private const int HttpProxyListenPort = 0;

        /// <summary>
        /// 获取是否解除地区限制
        /// </summary>
        /// <returns></returns>
        public AllowStatus GetIsLiftingOfRegion()
        {
            if (_appSettings.Network.IsLiftingOfRegion == AllowStatus.None)
            {
                // 第一次获取，先设置默认值
                SetIsLiftingOfRegion(IsLiftingOfRegion);
                return IsLiftingOfRegion;
            }

            return _appSettings.Network.IsLiftingOfRegion;
        }

        /// <summary>
        /// 设置是否解除地区限制
        /// </summary>
        /// <param name="isLiftingOfRegion"></param>
        /// <returns></returns>
        public bool SetIsLiftingOfRegion(AllowStatus isLiftingOfRegion)
        {
            return SetProperty(
                _appSettings.Network.IsLiftingOfRegion,
                isLiftingOfRegion,
                v => _appSettings.Network.IsLiftingOfRegion = v);
        }

        /// <summary>
        /// 获取是否启用https
        /// </summary>
        /// <returns></returns>
        public AllowStatus GetUseSsl()
        {
            if (_appSettings.Network.UseSsl == AllowStatus.None)
            {
                // 第一次获取，先设置默认值
                SetUseSsl(UseSsl);
                return UseSsl;
            }

            return _appSettings.Network.UseSsl;
        }

        /// <summary>
        /// 设置是否启用https
        /// </summary>
        /// <param name="useSsl"></param>
        /// <returns></returns>
        public bool SetUseSsl(AllowStatus useSsl)
        {
            return SetProperty(
                _appSettings.Network.UseSsl,
                useSsl,
                v => _appSettings.Network.UseSsl = v);
        }

        /// <summary>
        /// 获取UserAgent
        /// </summary>
        /// <returns></returns>
        public string GetUserAgent()
        {
            if (string.IsNullOrEmpty(_appSettings.Network.UserAgent))
            {
                // 第一次获取，先设置默认值
                SetUserAgent(UserAgent);
                return UserAgent;
            }

            return _appSettings.Network.UserAgent;
        }

        /// <summary>
        /// 设置UserAgent
        /// </summary>
        /// <param name="userAgent"></param>
        /// <returns></returns>
        public bool SetUserAgent(string userAgent)
        {
            return SetProperty(
                _appSettings.Network.UserAgent,
                userAgent,
                v => _appSettings.Network.UserAgent = v);
        }

        /// <summary>
        /// 获取下载器
        /// </summary>
        /// <returns></returns>
        public Downloader GetDownloader()
        {
            if (_appSettings.Network.Downloader != Downloader.NotSet) return _appSettings.Network.Downloader;
            // 第一次获取，先设置默认值
            SetDownloader(Downloader);
            return Downloader;
        }

        /// <summary>
        /// 设置下载器
        /// </summary>
        /// <param name="downloader"></param>
        /// <returns></returns>
        public bool SetDownloader(Downloader downloader)
        {
            return SetProperty(
                _appSettings.Network.Downloader,
                downloader,
                v => _appSettings.Network.Downloader = v);
        }

        /// <summary>
        /// 获取网络代理类型
        /// </summary>
        /// <returns></returns>
        public NetworkProxy GetNetworkProxy()
        {
            if (_appSettings.Network.NetworkProxy != NetworkProxy.None) return _appSettings.Network.NetworkProxy;
            SetNetworkProxy(NetworkProxy);
            return NetworkProxy;
        }

        /// <summary>
        /// 设置网络代理类型
        /// </summary>
        /// <param name="networkProxy"></param>
        /// <returns></returns>
        public bool SetNetworkProxy(NetworkProxy networkProxy)
        {
            return SetProperty(
                _appSettings.Network.NetworkProxy,
                networkProxy,
                v => _appSettings.Network.NetworkProxy = v);
        }

        public string GetCustomProxy()
        {
            if (_appSettings.Network.NetworkProxy == NetworkProxy.Custom && !string.IsNullOrEmpty(_appSettings.Network.CustomNetworkProxy))
                return _appSettings.Network.CustomNetworkProxy;
            // 第一次获取，先设置默认值
            SetCustomProxy(_customNetworkProxy);
            return _customNetworkProxy;
        }

        public bool SetCustomProxy(string proxyAddress)
        {
            try
            {
                _ = new WebProxy(proxyAddress);
                return SetProperty(
                    _appSettings.Network.CustomNetworkProxy,
                    proxyAddress,
                    v => _appSettings.Network.CustomNetworkProxy = v);
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// 获取最大同时下载数(任务数)
        /// </summary>
        /// <returns></returns>
        public int GetMaxCurrentDownloads()
        {
            if (_appSettings.Network.MaxCurrentDownloads != -1) return _appSettings.Network.MaxCurrentDownloads;
            // 第一次获取，先设置默认值
            SetMaxCurrentDownloads(MaxCurrentDownloads);
            return MaxCurrentDownloads;
        }

        /// <summary>
        /// 设置最大同时下载数(任务数)
        /// </summary>
        /// <param name="maxCurrentDownloads"></param>
        /// <returns></returns>
        public bool SetMaxCurrentDownloads(int maxCurrentDownloads)
        {
            return SetProperty(
                _appSettings.Network.MaxCurrentDownloads,
                maxCurrentDownloads,
                v => _appSettings.Network.MaxCurrentDownloads = v);
        }

        /// <summary>
        /// 获取单文件最大线程数
        /// </summary>
        /// <returns></returns>
        public int GetSplit()
        {
            if (_appSettings.Network.Split != -1) return _appSettings.Network.Split;
            // 第一次获取，先设置默认值
            SetSplit(Split);
            return Split;
        }

        /// <summary>
        /// 设置单文件最大线程数
        /// </summary>
        /// <param name="split"></param>
        /// <returns></returns>
        public bool SetSplit(int split)
        {
            return SetProperty(
                _appSettings.Network.Split,
                split,
                v => _appSettings.Network.Split = v);
        }

        /// <summary>
        /// 获取是否开启Http代理
        /// </summary>
        /// <returns></returns>
        public AllowStatus GetIsHttpProxy()
        {
            if (_appSettings.Network.IsHttpProxy != AllowStatus.None) return _appSettings.Network.IsHttpProxy;
            // 第一次获取，先设置默认值
            SetIsHttpProxy(IsHttpProxy);
            return IsHttpProxy;
        }

        /// <summary>
        /// 设置是否开启Http代理
        /// </summary>
        /// <param name="isHttpProxy"></param>
        /// <returns></returns>
        public bool SetIsHttpProxy(AllowStatus isHttpProxy)
        {
            return SetProperty(
                _appSettings.Network.IsHttpProxy,
                isHttpProxy,
                v => _appSettings.Network.IsHttpProxy = v);
        }

        /// <summary>
        /// 获取Http代理的地址
        /// </summary>
        /// <returns></returns>
        public string GetHttpProxy()
        {
            if (_appSettings.Network.HttpProxy != null) return _appSettings.Network.HttpProxy;
            // 第一次获取，先设置默认值
            SetHttpProxy(_httpProxy);
            return _httpProxy;
        }

        /// <summary>
        /// 设置Aria的http代理的地址
        /// </summary>
        /// <param name="httpProxy"></param>
        /// <returns></returns>
        public bool SetHttpProxy(string httpProxy)
        {
            return SetProperty(
                _appSettings.Network.HttpProxy,
                httpProxy,
                v => _appSettings.Network.HttpProxy = v);
        }

        /// <summary>
        /// 获取Http代理的端口
        /// </summary>
        /// <returns></returns>
        public int GetHttpProxyListenPort()
        {
            if (_appSettings.Network.HttpProxyListenPort != -1) return _appSettings.Network.HttpProxyListenPort;
            // 第一次获取，先设置默认值
            SetHttpProxyListenPort(HttpProxyListenPort);
            return HttpProxyListenPort;
        }

        /// <summary>
        /// 设置Http代理的端口
        /// </summary>
        /// <param name="httpProxyListenPort"></param>
        /// <returns></returns>
        public bool SetHttpProxyListenPort(int httpProxyListenPort)
        {
            return SetProperty(
                _appSettings.Network.HttpProxyListenPort,
                httpProxyListenPort,
                v => _appSettings.Network.HttpProxyListenPort = v);
        }
    }
}
