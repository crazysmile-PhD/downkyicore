using DownKyi.Core.Aria2cNet.Client.Entity;

namespace DownKyi.Core.Aria2cNet.Client;

public sealed partial class AriaClient
{
    /// <summary>
    /// This method returns the progress of the download denoted by gid (string).
    /// keys is an array of strings.
    /// If specified, the response contains only keys in the keys array.
    /// If keys is empty or omitted, the response contains all keys.
    /// This is useful when you just want specific keys and avoid unnecessary transfers.
    /// For example, aria2.tellStatus("2089b05ecca3d829", ["gid", "status"]) returns the gid and status keys only.
    /// The response is a struct and contains following keys. Values are strings.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaTellStatus> TellStatus(string gid)
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
            Method = "aria2.tellStatus",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaTellStatus>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns the URIs used in the download denoted by gid (string).
    /// The response is an array of structs and it contains following keys.
    /// Values are string.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaGetUris> GetUrisAsync(string gid)
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
            Method = "aria2.getUris",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaGetUris>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns the file list of the download denoted by gid (string).
    /// The response is an array of structs which contain following keys.
    /// Values are strings.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaGetFiles> GetFilesAsync(string gid)
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
            Method = "aria2.getFiles",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaGetFiles>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns a list peers of the download denoted by gid (string).
    /// This method is for BitTorrent only.
    /// The response is an array of structs and contains the following keys.
    /// Values are strings.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaGetPeers> GetPeersAsync(string gid)
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
            Method = "aria2.getPeers",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaGetPeers>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns currently connected HTTP(S)/FTP/SFTP servers of the download denoted by gid (string).
    /// The response is an array of structs and contains the following keys.
    /// Values are strings.
    /// </summary>
    /// <param name="gid"></param>
    /// <returns></returns>
    public async Task<AriaGetServers> GetServersAsync(string gid)
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
            Method = "aria2.getServers",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaGetServers>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns a list of active downloads.
    /// The response is an array of the same structs as returned by the aria2.tellStatus() method.
    /// For the keys parameter, please refer to the aria2.tellStatus() method.
    /// </summary>
    /// <returns></returns>
    public async Task<AriaTellStatusList> TellActiveAsync()
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.tellActive",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaTellStatusList>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns a list of waiting downloads, including paused ones.
    /// offset is an integer and specifies the offset from the download waiting at the front.
    /// num is an integer and specifies the max.
    /// number of downloads to be returned.
    /// For the keys parameter, please refer to the aria2.tellStatus() method.
    /// <br/><br/>
    /// If offset is a positive integer,
    /// this method returns downloads in the range of [offset, offset + num).
    /// <br/><br/>
    /// offset can be a negative integer.
    /// offset == -1 points last download in the waiting queue and offset == -2 points the download before the last download, and so on.
    /// Downloads in the response are in reversed order then.
    /// <br/><br/>
    /// For example, imagine three downloads "A","B" and "C" are waiting in this order.
    /// aria2.tellWaiting(0, 1) returns ["A"].
    /// aria2.tellWaiting(1, 2) returns ["B", "C"].
    /// aria2.tellWaiting(-1, 2) returns ["C", "B"].
    /// <br/><br/>
    /// The response is an array of the same structs as returned by aria2.tellStatus() method.
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="num"></param>
    /// <returns></returns>
    public async Task<AriaTellStatusList> TellWaitingAsync(int offset, int num)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            offset,
            num
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.tellWaiting",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaTellStatusList>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns a list of stopped downloads.
    /// offset is an integer and specifies the offset from the least recently stopped download.
    /// num is an integer and specifies the max.
    /// number of downloads to be returned.
    /// For the keys parameter, please refer to the aria2.tellStatus() method.
    /// <br/><br/>
    /// offset and num have the same semantics as described in the aria2.tellWaiting() method.
    /// <br/><br/>
    /// The response is an array of the same structs as returned by the aria2.tellStatus() method.
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="num"></param>
    /// <returns></returns>
    public async Task<AriaTellStatusList> TellStoppedAsync(int offset, int num)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            offset,
            num
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.tellStopped",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaTellStatusList>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method changes the position of the download denoted by gid in the queue.
    /// pos is an integer.
    /// how is a string.
    /// If how is POS_SET, it moves the download to a position relative to the beginning of the queue.
    /// If how is POS_CUR, it moves the download to a position relative to the current position.
    /// If how is POS_END, it moves the download to a position relative to the end of the queue.
    /// If the destination position is less than 0 or beyond the end of the queue,
    /// it moves the download to the beginning or the end of the queue respectively.
    /// The response is an integer denoting the resulting position.
    ///
    /// For example, if GID#2089b05ecca3d829 is currently in position 3,
    /// aria2.changePosition('2089b05ecca3d829', -1, 'POS_CUR') will change its position to 2.
    /// Additionally aria2.changePosition('2089b05ecca3d829', 0, 'POS_SET') will change its position to 0 (the beginning of the queue).
    ///
    /// The following examples move the download GID#2089b05ecca3d829 to the front of the queue.
    /// </summary>
    /// <param name="gid"></param>
    /// <param name="pos"></param>
    /// <param name="how"></param>
    /// <returns></returns>
    public async Task<AriaChangePosition> ChangePositionAsync(string gid, int pos, HowChangePosition how)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            gid,
            pos,
            GetChangePositionValue(how)
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.changePosition",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaChangePosition>(ariaSend).ConfigureAwait(false);
    }

    internal static string GetChangePositionValue(HowChangePosition how)
    {
        return how switch
        {
            HowChangePosition.None => nameof(HowChangePosition.None),
            HowChangePosition.PosSet => "POS_SET",
            HowChangePosition.PosCurrent => "POS_CUR",
            HowChangePosition.PosEnd => "POS_END",
            _ => throw new ArgumentOutOfRangeException(nameof(how), how, "Unknown aria2 change-position mode.")
        };
    }

    /// <summary>
    /// This method removes the URIs in delUris from and appends the URIs in addUris to download denoted by gid.
    /// delUris and addUris are lists of strings.
    /// A download can contain multiple files and URIs are attached to each file.
    /// fileIndex is used to select which file to remove/attach given URIs.
    /// fileIndex is 1-based.
    /// position is used to specify where URIs are inserted in the existing waiting URI list.
    /// position is 0-based.
    /// When position is omitted, URIs are appended to the back of the list.
    /// This method first executes the removal and then the addition.
    /// position is the position after URIs are removed, not the position when this method is called.
    /// When removing an URI, if the same URIs exist in download,
    /// only one of them is removed for each URI in delUris.
    /// In other words,
    /// if there are three URIs http://example.org/aria2 and you want remove them all,
    /// you have to specify (at least) 3 http://example.org/aria2 in delUris.
    /// This method returns a list which contains two integers.
    /// The first integer is the number of URIs deleted. The second integer is the number of URIs added.
    /// </summary>
    /// <param name="gid"></param>
    /// <param name="fileIndex"></param>
    /// <param name="delUris"></param>
    /// <param name="addUris"></param>
    /// <param name="position"></param>
    /// <returns></returns>
    public async Task<AriaChangeUri> ChangeUriAsync(string gid, int fileIndex, IReadOnlyList<string> delUris, IReadOnlyList<string> addUris, int position = -1)
    {
        List<object> ariaParams = new List<object>
        {
            "token:" + _token,
            gid,
            fileIndex,
            delUris,
            addUris
        };
        if (position > -1)
        {
            ariaParams.Add(position);
        }

        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "aria2.changeUri",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<AriaChangeUri>(ariaSend).ConfigureAwait(false);
    }
}
