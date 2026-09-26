using System.Reflection;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.Tests;

public sealed class AriaClientRpcContractTests
{
    [Fact]
    public async Task PublicRpcMethodsKeepTheirAria2WireMethodAndAuthenticationContract()
    {
        var cases = CreateCases();
        var publicRpcMethods = typeof(AriaClient)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            publicRpcMethods,
            cases.Select(testCase => testCase.MethodName)
                .Order(StringComparer.Ordinal));

        foreach (var testCase in cases)
        {
            string? capturedPayload = null;
            var captureSignal = new InvalidOperationException("RPC payload captured.");
            var client = new AriaClient(
                "https://aria-contract.example",
                35076,
                "contract-token",
                (_, payload) =>
                {
                    capturedPayload = payload;
                    return Task.FromException<string?>(captureSignal);
                });

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => testCase.Invoke(client));
            Assert.Same(captureSignal, thrown);

            var request = JObject.Parse(Assert.IsType<string>(capturedPayload));
            Assert.Equal("2.0", request["jsonrpc"]?.Value<string>());
            Assert.False(string.IsNullOrWhiteSpace(request["id"]?.Value<string>()));
            Assert.Equal(testCase.RpcMethod, request["method"]?.Value<string>());

            var parameters = request["params"] as JArray;
            if (testCase.RequiresToken)
            {
                Assert.NotNull(parameters);
                Assert.Equal("token:contract-token", parameters[0]?.Value<string>());
            }
            else
            {
                var firstParameter = parameters?.First;
                Assert.False(
                    firstParameter?.Type == JTokenType.String
                    && firstParameter.Value<string>() == "token:contract-token");
            }
        }
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("force-remove")]
    [InlineData("remove-result")]
    public async Task TransferRemovalMethodsPropagateCancellation(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedToken = CancellationToken.None;
        var client = new AriaClient(
            "https://aria-contract.example",
            35076,
            "contract-token",
            async (_, _, cancellationToken) =>
            {
                observedToken = cancellationToken;
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return null;
            });
        var request = operation switch
        {
            "remove" => client.RemoveAsync("gid", cancellation.Token),
            "force-remove" => client.ForceRemoveAsync("gid", cancellation.Token),
            "remove-result" => client.RemoveDownloadResultAsync("gid", cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };

        await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(cancellation.Token, observedToken);
    }

    private static IReadOnlyList<RpcContractCase> CreateCases()
    {
        var sendOption = new AriaSendOption();
        return
        [
            new(nameof(AriaClient.AddUriAsync), "aria2.addUri", true, client => client.AddUriAsync(["https://media.example/video"], sendOption)),
            new(nameof(AriaClient.AddTorrentAsync), "aria2.addTorrent", true, client => client.AddTorrentAsync("torrent", [], sendOption)),
            new(nameof(AriaClient.AddMetalinkAsync), "aria2.addMetalink", true, client => client.AddMetalinkAsync("metalink", [], sendOption)),
            new(nameof(AriaClient.RemoveAsync), "aria2.remove", true, client => client.RemoveAsync("gid")),
            new(nameof(AriaClient.ForceRemoveAsync), "aria2.forceRemove", true, client => client.ForceRemoveAsync("gid")),
            new(nameof(AriaClient.PauseAsync), "aria2.pause", true, client => client.PauseAsync("gid")),
            new(nameof(AriaClient.PauseAllAsync), "aria2.pauseAll", true, client => client.PauseAllAsync()),
            new(nameof(AriaClient.ForcePauseAsync), "aria2.forcePause", true, client => client.ForcePauseAsync("gid")),
            new(nameof(AriaClient.ForcePauseAllAsync), "aria2.forcePauseAll", true, client => client.ForcePauseAllAsync()),
            new(nameof(AriaClient.UnpauseAsync), "aria2.unpause", true, client => client.UnpauseAsync("gid")),
            new(nameof(AriaClient.UnpauseAllAsync), "aria2.unpauseAll", true, client => client.UnpauseAllAsync()),
            new(nameof(AriaClient.TellStatus), "aria2.tellStatus", true, client => client.TellStatus("gid")),
            new(nameof(AriaClient.GetUrisAsync), "aria2.getUris", true, client => client.GetUrisAsync("gid")),
            new(nameof(AriaClient.GetFilesAsync), "aria2.getFiles", true, client => client.GetFilesAsync("gid")),
            new(nameof(AriaClient.GetPeersAsync), "aria2.getPeers", true, client => client.GetPeersAsync("gid")),
            new(nameof(AriaClient.GetServersAsync), "aria2.getServers", true, client => client.GetServersAsync("gid")),
            new(nameof(AriaClient.TellActiveAsync), "aria2.tellActive", true, client => client.TellActiveAsync()),
            new(nameof(AriaClient.TellWaitingAsync), "aria2.tellWaiting", true, client => client.TellWaitingAsync(0, 10)),
            new(nameof(AriaClient.TellStoppedAsync), "aria2.tellStopped", true, client => client.TellStoppedAsync(0, 10)),
            new(nameof(AriaClient.ChangePositionAsync), "aria2.changePosition", true, client => client.ChangePositionAsync("gid", 0, HowChangePosition.PosSet)),
            new(nameof(AriaClient.ChangeUriAsync), "aria2.changeUri", true, client => client.ChangeUriAsync("gid", 1, [], ["https://media.example/video"])),
            new(nameof(AriaClient.GetOptionAsync), "aria2.getOption", true, client => client.GetOptionAsync("gid")),
            new(nameof(AriaClient.ChangeOptionAsync), "aria2.changeOption", true, client => client.ChangeOptionAsync("gid", new { Split = "4" })),
            new(nameof(AriaClient.GetGlobalOptionAsync), "aria2.getGlobalOption", true, client => client.GetGlobalOptionAsync()),
            new(nameof(AriaClient.ChangeGlobalOptionAsync), "aria2.changeGlobalOption", true, client => client.ChangeGlobalOptionAsync(new { Split = "4" })),
            new(nameof(AriaClient.GetGlobalStatAsync), "aria2.getGlobalStat", true, client => client.GetGlobalStatAsync()),
            new(nameof(AriaClient.PurgeDownloadResultAsync), "aria2.purgeDownloadResult", true, client => client.PurgeDownloadResultAsync()),
            new(nameof(AriaClient.RemoveDownloadResultAsync), "aria2.removeDownloadResult", true, client => client.RemoveDownloadResultAsync("gid")),
            new(nameof(AriaClient.GetAriaVersionAsync), "aria2.getVersion", true, client => client.GetAriaVersionAsync()),
            new(nameof(AriaClient.GetSessionInfoAsync), "aria2.getSessionInfo", true, client => client.GetSessionInfoAsync()),
            new(nameof(AriaClient.ShutdownAsync), "aria2.shutdown", true, client => client.ShutdownAsync()),
            new(nameof(AriaClient.ForceShutdownAsync), "aria2.forceShutdown", true, client => client.ForceShutdownAsync()),
            new(nameof(AriaClient.SaveSessionAsync), "aria2.saveSession", true, client => client.SaveSessionAsync()),
            new(nameof(AriaClient.MulticallAsync), "system.multicall", false, client => client.MulticallAsync([])),
            new(nameof(AriaClient.ListMethodsAsync), "system.listMethods", false, client => client.ListMethodsAsync()),
            new(nameof(AriaClient.ListNotificationsAsync), "system.listNotifications", false, client => client.ListNotificationsAsync())
        ];
    }

    private sealed record RpcContractCase(
        string MethodName,
        string RpcMethod,
        bool RequiresToken,
        Func<AriaClient, Task> Invoke);
}
