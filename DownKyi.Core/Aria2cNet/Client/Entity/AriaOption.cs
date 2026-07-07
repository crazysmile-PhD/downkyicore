using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity
{
    [JsonObject]
    public class AriaOption
    {
        [JsonProperty("all-proxy")]
        public string AllProxy { get; set; } = string.Empty;

        [JsonProperty("allow-overwrite")]
        public string AllowOverwrite { get; set; } = string.Empty;

        [JsonProperty("allow-piece-length-change")]
        public string AllowPieceLengthChange { get; set; } = string.Empty;

        [JsonProperty("always-resume")]
        public string AlwaysResume { get; set; } = string.Empty;

        [JsonProperty("async-dns")]
        public string AsyncDns { get; set; } = string.Empty;

        [JsonProperty("auto-file-renaming")]
        public string AutoFileRenaming { get; set; } = string.Empty;

        [JsonProperty("bt-enable-hook-after-hash-check")]
        public string BtEnableHookAfterHashCheck { get; set; } = string.Empty;

        [JsonProperty("bt-enable-lpd")]
        public string BtEnableLpd { get; set; } = string.Empty;

        [JsonProperty("bt-force-encryption")]
        public string BtForceEncryption { get; set; } = string.Empty;

        [JsonProperty("bt-hash-check-seed")]
        public string BtHashCheckSeed { get; set; } = string.Empty;

        [JsonProperty("bt-load-saved-metadata")]
        public string BtLoadSavedMetadata { get; set; } = string.Empty;

        [JsonProperty("bt-max-peers")]
        public string BtMaxPeers { get; set; } = string.Empty;

        [JsonProperty("bt-metadata-only")]
        public string BtMetadataOnly { get; set; } = string.Empty;

        [JsonProperty("bt-min-crypto-level")]
        public string BtMinCryptoLevel { get; set; } = string.Empty;

        [JsonProperty("bt-remove-unselected-file")]
        public string BtRemoveUnselectedFile { get; set; } = string.Empty;

        [JsonProperty("bt-request-peer-speed-limit")]
        public string BtRequestPeerSpeedLimit { get; set; } = string.Empty;

        [JsonProperty("bt-require-crypto")]
        public string BtRequireCrypto { get; set; } = string.Empty;

        [JsonProperty("bt-save-metadata")]
        public string BtSaveMetadata { get; set; } = string.Empty;

        [JsonProperty("bt-seed-unverified")]
        public string BtSeedUnverified { get; set; } = string.Empty;

        [JsonProperty("bt-stop-timeout")]
        public string BtStopTimeout { get; set; } = string.Empty;

        [JsonProperty("bt-tracker-connect-timeout")]
        public string BtTrackerConnectTimeout { get; set; } = string.Empty;

        [JsonProperty("bt-tracker-interval")]
        public string BtTrackerInterval { get; set; } = string.Empty;

        [JsonProperty("bt-tracker-timeout")]
        public string BtTrackerTimeout { get; set; } = string.Empty;

        [JsonProperty("check-integrity")]
        public string CheckIntegrity { get; set; } = string.Empty;

        [JsonProperty("conditional-get")]
        public string ConditionalGet { get; set; } = string.Empty;

        [JsonProperty("connect-timeout")]
        public string ConnectTimeout { get; set; } = string.Empty;

        [JsonProperty("content-disposition-default-utf8")]
        public string ContentDispositionDefaultUtf8 { get; set; } = string.Empty;

        [JsonProperty("continue")]
        public string Continue { get; set; } = string.Empty;

        [JsonProperty("dir")]
        public string Dir { get; set; } = string.Empty;

        [JsonProperty("dry-run")]
        public string DryRun { get; set; } = string.Empty;

        [JsonProperty("enable-http-keep-alive")]
        public string EnableHttpKeepAlive { get; set; } = string.Empty;

        [JsonProperty("enable-http-pipelining")]
        public string EnableHttpPipelining { get; set; } = string.Empty;

        [JsonProperty("enable-mmap")]
        public string EnableMmap { get; set; } = string.Empty;

        [JsonProperty("enable-peer-exchange")]
        public string EnablePeerExchange { get; set; } = string.Empty;

        [JsonProperty("file-allocation")]
        public string FileAllocation { get; set; } = string.Empty;

        [JsonProperty("follow-metalink")]
        public string FollowMetalink { get; set; } = string.Empty;

        [JsonProperty("follow-torrent")]
        public string FollowTorrent { get; set; } = string.Empty;

        [JsonProperty("force-save")]
        public string ForceSave { get; set; } = string.Empty;

        [JsonProperty("ftp-pasv")]
        public string FtpPasv { get; set; } = string.Empty;

        [JsonProperty("ftp-reuse-connection")]
        public string FtpReuseConnection { get; set; } = string.Empty;

        [JsonProperty("ftp-type")]
        public string FtpType { get; set; } = string.Empty;

        [JsonProperty("hash-check-only")]
        public string HashCheckOnly { get; set; } = string.Empty;

        [JsonProperty("http-accept-gzip")]
        public string HttpAcceptGzip { get; set; } = string.Empty;

        [JsonProperty("http-auth-challenge")]
        public string HttpAuthChallenge { get; set; } = string.Empty;

        [JsonProperty("http-no-cache")]
        public string HttpNoCache { get; set; } = string.Empty;

        [JsonProperty("lowest-speed-limit")]
        public string LowestSpeedLimit { get; set; } = string.Empty;

        [JsonProperty("max-connection-per-server")]
        public string MaxConnectionPerServer { get; set; } = string.Empty;

        [JsonProperty("max-download-limit")]
        public string MaxDownloadLimit { get; set; } = string.Empty;

        [JsonProperty("max-file-not-found")]
        public string MaxFileNotFound { get; set; } = string.Empty;

        [JsonProperty("max-mmap-limit")]
        public string MaxMmapLimit { get; set; } = string.Empty;

        [JsonProperty("max-resume-failure-tries")]
        public string MaxResumeFailureTries { get; set; } = string.Empty;

        [JsonProperty("max-tries")]
        public string MaxTries { get; set; } = string.Empty;

        [JsonProperty("max-upload-limit")]
        public string MaxUploadLimit { get; set; } = string.Empty;

        [JsonProperty("metalink-enable-unique-protocol")]
        public string MetalinkEnableUniqueProtocol { get; set; } = string.Empty;

        [JsonProperty("metalink-preferred-protocol")]
        public string MetalinkPreferredProtocol { get; set; } = string.Empty;

        [JsonProperty("min-split-size")]
        public string MinSplitSize { get; set; } = string.Empty;

        [JsonProperty("no-file-allocation-limit")]
        public string NoFileAllocationLimit { get; set; } = string.Empty;

        [JsonProperty("no-netrc")]
        public string NoNetrc { get; set; } = string.Empty;

        [JsonProperty("out")]
        public string Out { get; set; } = string.Empty;

        [JsonProperty("parameterized-uri")]
        public string ParameterizedUri { get; set; } = string.Empty;

        [JsonProperty("pause-metadata")]
        public string PauseMetadata { get; set; } = string.Empty;

        [JsonProperty("piece-length")]
        public string PieceLength { get; set; } = string.Empty;

        [JsonProperty("proxy-method")]
        public string ProxyMethod { get; set; } = string.Empty;

        [JsonProperty("realtime-chunk-checksum")]
        public string RealtimeChunkChecksum { get; set; } = string.Empty;

        [JsonProperty("remote-time")]
        public string RemoteTime { get; set; } = string.Empty;

        [JsonProperty("remove-control-file")]
        public string RemoveControlFile { get; set; } = string.Empty;

        [JsonProperty("retry-wait")]
        public string RetryWait { get; set; } = string.Empty;

        [JsonProperty("reuse-uri")]
        public string ReuseUri { get; set; } = string.Empty;

        [JsonProperty("rpc-save-upload-metadata")]
        public string RpcSaveUploadMetadata { get; set; } = string.Empty;

        [JsonProperty("save-not-found")]
        public string SaveNotFound { get; set; } = string.Empty;

        [JsonProperty("seed-ratio")]
        public string SeedRatio { get; set; } = string.Empty;

        [JsonProperty("split")]
        public string Split { get; set; } = string.Empty;

        [JsonProperty("stream-piece-selector")]
        public string StreamPieceSelector { get; set; } = string.Empty;

        [JsonProperty("timeout")]
        public string Timeout { get; set; } = string.Empty;

        [JsonProperty("uri-selector")]
        public string UriSelector { get; set; } = string.Empty;

        [JsonProperty("use-head")]
        public string UseHead { get; set; } = string.Empty;

        [JsonProperty("user-agent")]
        public string UserAgent { get; set; } = string.Empty;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
