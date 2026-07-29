using DownKyi.Core.Aria2cNet.Client.Entity;

namespace DownKyi.Core.Aria2cNet.Client;

public sealed partial class AriaClient
{
    /// <summary>
    /// This method returns options of the download denoted by gid.
    /// The response is a struct where keys are the names of options.
    /// The values are strings.
    /// Note that this method does not return options which have no default value and have not been set on the command-line,
    /// in configuration files or RPC methods.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaGetOption> GetOptionAsync(string gid)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            gid
        };

        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.getOption",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaGetOption>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method changes options of the download denoted by gid (string) dynamically.
    /// options is a struct.
    /// The options listed in Input File subsection are available, except for following options:
    /// <br/>
    /// dry-run metalink-base-uri parameterized-uri pause piece-length rpc-save-upload-metadata
    /// <br/>
    /// Except for the following options,
    /// changing the other options of active download makes it restart
    /// (restart itself is managed by aria2, and no user intervention is required):
    /// <br/>
    /// bt-max-peers bt-request-peer-speed-limit bt-remove-unselected-file force-save max-download-limit max-upload-limit
    /// <br/>
    /// This method returns OK for success.
    /// </summary>
    /// <param name="gid"></param>
    /// <param name="option"></param>
    /// <returns></returns>
    public async Task<AriaChangeOption> ChangeOptionAsync(string gid, object option)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            gid,
            option
        };

        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.changeOption",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaChangeOption>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns the global options.
    /// The response is a struct.
    /// Its keys are the names of options.
    /// Values are strings.
    /// Note that this method does not return options which have no default value and have not been set on the command-line,
    /// in configuration files or RPC methods.
    /// Because global options are used as a template for the options of newly added downloads,
    /// the response contains keys returned by the aria2.getOption() method.
    /// </summary>
    /// <returns></returns>
    public async Task<AriaGetOption> GetGlobalOptionAsync()
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
        };

        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.getGlobalOption",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaGetOption>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method changes global options dynamically.
    /// options is a struct.
    /// The following options are available:
    /// <br/>
    /// bt-max-open-files download-result keep-unfinished-download-result log log-level
    /// max-concurrent-downloads max-download-result max-overall-download-limit max-overall-upload-limit
    /// optimize-concurrent-downloads save-cookies save-session server-stat-of
    /// <br/>
    /// In addition, options listed in the Input File subsection are available,
    /// except for following options: checksum, index-out, out, pause and select-file.
    /// With the log option, you can dynamically start logging or change log file.
    /// To stop logging, specify an empty string("") as the parameter value.
    /// Note that log file is always opened in append mode.
    /// This method returns OK for success.
    /// </summary>
    /// <param name="option"></param>
    /// <returns></returns>
    public async Task<AriaChangeOption> ChangeGlobalOptionAsync(object option)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            option
        };

        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.changeGlobalOption",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaChangeOption>(ariaSend).ConfigureAwait(false);
    }
}
