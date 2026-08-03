using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DownKyi.Services.Download;

internal sealed record LocalAriaRpcEndpoint(int Port, string Secret)
{
    public static LocalAriaRpcEndpoint Create()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new LocalAriaRpcEndpoint(endpoint.Port, secret);
    }
}
