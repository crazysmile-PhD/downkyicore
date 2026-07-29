using DownKyi.Core.Aria2cNet.Client.Entity;

namespace DownKyi.Core.Aria2cNet.Client;

public sealed partial class AriaClient
{
    /// <summary>
    /// This methods encapsulates multiple method calls in a single request.
    /// methods is an array of structs. The structs contain two keys: methodName and params.
    /// methodName is the method name to call and params is array containing parameters to the method call.
    /// This method returns an array of responses.
    /// The elements will be either a one-item array containing the return value of the method call or a struct of fault element if an encapsulated method call fails.
    /// </summary>
    /// <param name="systemMulticallMathods"></param>
    /// <returns></returns>
    public async Task<List<SystemMulticall>> MulticallAsync(IReadOnlyList<SystemMulticallMathod> systemMulticallMathods)
    {
        List<object> ariaParams = new List<object>
        {
            systemMulticallMathods
        };
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "system.multicall",
            Params = ariaParams
        };
        return await GetRpcResponseAsync<List<SystemMulticall>>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns all the available RPC methods in an array of string.
    /// Unlike other methods, this method does not require secret token.
    /// This is safe because this method just returns the available method names.
    /// </summary>
    /// <returns></returns>
    public async Task<SystemListMethods> ListMethodsAsync()
    {
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "system.listMethods"
        };
        return await GetRpcResponseAsync<SystemListMethods>(ariaSend).ConfigureAwait(false);
    }

    /// <summary>
    /// This method returns all the available RPC notifications in an array of string.
    /// Unlike other methods, this method does not require secret token.
    /// This is safe because this method just returns the available notifications names.
    /// </summary>
    /// <returns></returns>
    public async Task<SystemListNotifications> ListNotificationsAsync()
    {
        AriaSendData ariaSend = new AriaSendData
        {
            Id = Guid.NewGuid().ToString("N"),
            Jsonrpc = JSONRPC,
            Method = "system.listNotifications"
        };
        return await GetRpcResponseAsync<SystemListNotifications>(ariaSend).ConfigureAwait(false);
    }
}
