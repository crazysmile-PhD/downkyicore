using DownKyi.Core.Aria2cNet;
using DownKyi.Core.Aria2cNet.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed class AriaManagerContractTests
{
    [Fact]
    public async Task RpcRejectionFailsImmediatelyWithMachineReadableCode()
    {
        const string response =
            """
            {
              "jsonrpc": "2.0",
              "id": "test",
              "error": {
                "code": 1,
                "message": "Sanitized RPC rejection."
              }
            }
            """;
        var requestCount = 0;
        var manager = CreateManager(response, () => requestCount++);

        var result = await manager.GetDownloadStatusDetailAsync(
            "test-gid",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DownloadResult.FAILED, result.Result);
        Assert.Equal("rpc-1", result.ErrorCode);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task MissingGidRetainsAbortSemantics()
    {
        const string response =
            """
            {
              "jsonrpc": "2.0",
              "id": "test",
              "error": {
                "code": 1,
                "message": "GID test-gid is not found"
              }
            }
            """;
        var manager = CreateManager(response, static () => { });

        var result = await manager.GetDownloadStatusDetailAsync(
            "test-gid",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DownloadResult.ABORT, result.Result);
        Assert.Equal("not-found", result.ErrorCode);
    }

    [Fact]
    public async Task EmptyRpcEnvelopeFailsImmediately()
    {
        const string response =
            """
            {
              "jsonrpc": "2.0",
              "id": "test"
            }
            """;
        var requestCount = 0;
        var manager = CreateManager(response, () => requestCount++);

        var result = await manager.GetDownloadStatusDetailAsync(
            "test-gid",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DownloadResult.FAILED, result.Result);
        Assert.Equal("rpc-empty", result.ErrorCode);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task CompleteStatusCarriesIncompleteNativeEvidenceForTransferValidation()
    {
        const string response =
            """
            {
              "jsonrpc": "2.0",
              "id": "test",
              "result": {
                "gid": "test-gid",
                "status": "complete",
                "totalLength": "8",
                "completedLength": "4",
                "pieceLength": "4",
                "numPieces": "2",
                "bitfield": "80",
                "files": [{
                  "index": "1",
                  "path": "media.tmp",
                  "length": "8",
                  "completedLength": "4",
                  "selected": "true"
                }]
              }
            }
            """;
        var manager = CreateManager(response, static () => { });

        var result = await manager.GetDownloadStatusDetailAsync(
            "test-gid",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DownloadResult.SUCCESS, result.Result);
        Assert.NotNull(result.CompletionEvidence);
        Assert.False(result.CompletionEvidence.IsComplete);
    }

    [Fact]
    public async Task CompleteStatusReturnsNativeByteAndPieceEvidence()
    {
        const string response =
            """
            {
              "jsonrpc": "2.0",
              "id": "test",
              "result": {
                "gid": "test-gid",
                "status": "complete",
                "totalLength": "8",
                "completedLength": "8",
                "pieceLength": "4",
                "numPieces": "2",
                "bitfield": "c0",
                "files": [{
                  "index": "1",
                  "path": "media.tmp",
                  "length": "8",
                  "completedLength": "8",
                  "selected": "true"
                }]
              }
            }
            """;
        var manager = CreateManager(response, static () => { });

        var result = await manager.GetDownloadStatusDetailAsync(
            "test-gid",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DownloadResult.SUCCESS, result.Result);
        var evidence = Assert.IsType<AriaTransferCompletionEvidence>(result.CompletionEvidence);
        Assert.True(evidence.IsComplete);
        Assert.Equal(8, evidence.TotalLength);
        Assert.Equal(8, evidence.CompletedLength);
        Assert.Equal("c0", evidence.Bitfield);
        Assert.Equal(2, evidence.NumPieces);
    }

    [Theory]
    [InlineData(8, 8, "c0", 2, true)]
    [InlineData(8, 8, "80", 2, false)]
    [InlineData(17, 17, "ffff80", 17, true)]
    [InlineData(17, 16, "ffff80", 17, false)]
    [InlineData(8, 8, "", 2, false)]
    public void CompletionEvidenceRequiresExactBytesAndEveryNativePiece(
        long totalLength,
        long completedLength,
        string bitfield,
        long numPieces,
        bool expected)
    {
        var evidence = new AriaTransferCompletionEvidence(
            totalLength,
            completedLength,
            bitfield,
            numPieces);

        Assert.Equal(expected, evidence.IsComplete);
    }

    private static AriaManager CreateManager(
        string response,
        Action onRequest)
    {
        var client = new AriaClient(
            "http://localhost",
            35076,
            "test-token",
            (_, _) =>
            {
                onRequest();
                return Task.FromResult<string?>(response);
            });
        return new AriaManager(
            client,
            NullLogger<AriaManager>.Instance);
    }
}
