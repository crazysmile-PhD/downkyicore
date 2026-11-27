using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
using DownKyi.Services.Migration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class LegacyUpgradeCoordinatorTests
{
    // Produced by the v1.0.20 LoginHelper. The NRBF payload contains sparse
    // CookieCollection and ArrayList backing arrays; SHA-256 starts with 928A046E.
    private const string SparseCookieContainer =
        "AAEAAAD/////AQAAAAAAAAAMAgAAAElTeXN0ZW0sIFZlcnNpb249NC4wLjAuMCwgQ3VsdHVyZT1uZXV0cmFsLCBQdWJsaWNLZXlUb2tlbj1iNzdhNWM1NjE5MzRlMDg5BQEAAAAaU3lzdGVtLk5ldC5Db29raWVDb250YWluZXIGAAAADW1fZG9tYWluVGFibGUPbV9tYXhDb29raWVTaXplDG1fbWF4Q29va2llcxVtX21heENvb2tpZXNQZXJEb21haW4HbV9jb3VudA5tX2ZxZG5NeURvbWFpbgMAAAAAARxTeXN0ZW0uQ29sbGVjdGlvbnMuSGFzaHRhYmxlCAgICAIAAAAJAwAAAAAQAAAsAQAAFAAAAAIAAAAGBAAAAAAEAwAAABxTeXN0ZW0uQ29sbGVjdGlvbnMuSGFzaHRhYmxlBwAAAApMb2FkRmFjdG9yB1ZlcnNpb24IQ29tcGFyZXIQSGFzaENvZGVQcm92aWRlcghIYXNoU2l6ZQRLZXlzBlZhbHVlcwAAAwMABQULCBxTeXN0ZW0uQ29sbGVjdGlvbnMuSUNvbXBhcmVyJFN5c3RlbS5Db2xsZWN0aW9ucy5JSGFzaENvZGVQcm92aWRlcgjsUTg/AQAAAAoKAwAAAAkFAAAACQYAAAAQBQAAAAEAAAAGBwAAAA0uYmlsaWJpbGkuY29tEAYAAAABAAAACQgAAAAFCAAAABNTeXN0ZW0uTmV0LlBhdGhMaXN0AQAAAAZtX2xpc3QDLFN5c3RlbS5Db2xsZWN0aW9ucy5Tb3J0ZWRMaXN0K1N5bmNTb3J0ZWRMaXN0AgAAAAkJAAAABAkAAAAsU3lzdGVtLkNvbGxlY3Rpb25zLlNvcnRlZExpc3QrU3luY1NvcnRlZExpc3QJAAAABV9saXN0BV9yb290D1NvcnRlZExpc3Qra2V5cxFTb3J0ZWRMaXN0K3ZhbHVlcxBTb3J0ZWRMaXN0K19zaXplElNvcnRlZExpc3QrdmVyc2lvbhNTb3J0ZWRMaXN0K2NvbXBhcmVyElNvcnRlZExpc3Qra2V5TGlzdBRTb3J0ZWRMaXN0K3ZhbHVlTGlzdAMCBQUAAAMDAx1TeXN0ZW0uQ29sbGVjdGlvbnMuU29ydGVkTGlzdAgIG1N5c3RlbS5Db2xsZWN0aW9ucy5Db21wYXJlciVTeXN0ZW0uQ29sbGVjdGlvbnMuU29ydGVkTGlzdCtLZXlMaXN0J1N5c3RlbS5Db2xsZWN0aW9ucy5Tb3J0ZWRMaXN0K1ZhbHVlTGlzdAkKAAAACQoAAAAJCwAAAAkLAAAAAAAAAAAAAAAJDAAAAAoKBAoAAAAdU3lzdGVtLkNvbGxlY3Rpb25zLlNvcnRlZExpc3QHAAAABGtleXMGdmFsdWVzBV9zaXplB3ZlcnNpb24IY29tcGFyZXIHa2V5TGlzdAl2YWx1ZUxpc3QFBQAABAMDCAgkU3lzdGVtLk5ldC5QYXRoTGlzdCtQYXRoTGlzdENvbXBhcmVyAgAAACVTeXN0ZW0uQ29sbGVjdGlvbnMuU29ydGVkTGlzdCtLZXlMaXN0J1N5c3RlbS5Db2xsZWN0aW9ucy5Tb3J0ZWRMaXN0K1ZhbHVlTGlzdAkNAAAACQ4AAAABAAAAAQAAAAkPAAAACgkQAAAAEAsAAAAAAAAABAwAAAAbU3lzdGVtLkNvbGxlY3Rpb25zLkNvbXBhcmVyAQAAAAtDb21wYXJlSW5mbwMgU3lzdGVtLkdsb2JhbGl6YXRpb24uQ29tcGFyZUluZm8JEQAAABANAAAAEAAAAAYSAAAAAS8NDxAOAAAAEAAAAAkTAAAADQ8FDwAAACRTeXN0ZW0uTmV0LlBhdGhMaXN0K1BhdGhMaXN0Q29tcGFyZXIAAAAAAgAAAAQQAAAAJ1N5c3RlbS5Db2xsZWN0aW9ucy5Tb3J0ZWRMaXN0K1ZhbHVlTGlzdAEAAAAKc29ydGVkTGlzdAMdU3lzdGVtLkNvbGxlY3Rpb25zLlNvcnRlZExpc3QJCgAAAAQRAAAAIFN5c3RlbS5HbG9iYWxpemF0aW9uLkNvbXBhcmVJbmZvAwAAAAZtX25hbWUNbV9Tb3J0VmVyc2lvbgdjdWx0dXJlAQMAIFN5c3RlbS5HbG9iYWxpemF0aW9uLlNvcnRWZXJzaW9uCAYVAAAABXpoLVRXCgQEAAAFEwAAABtTeXN0ZW0uTmV0LkNvb2tpZUNvbGxlY3Rpb24EAAAABm1fbGlzdAltX3ZlcnNpb24LbV9UaW1lU3RhbXAUbV9oYXNfb3RoZXJfdmVyc2lvbnMDAAAAHFN5c3RlbS5Db2xsZWN0aW9ucy5BcnJheUxpc3QIDQECAAAACRYAAAACAAAAAAAAAAAAAAABBBYAAAAcU3lzdGVtLkNvbGxlY3Rpb25zLkFycmF5TGlzdAMAAAAGX2l0ZW1zBV9zaXplCF92ZXJzaW9uBQAACAgJFwAAAAIAAAACAAAAEBcAAAAEAAAACRgAAAAJGQAAAA0CBRgAAAARU3lzdGVtLk5ldC5Db29raWUVAAAACW1fY29tbWVudAxtX2NvbW1lbnRVcmkPbV9jb29raWVWYXJpYW50CW1fZGlzY2FyZAhtX2RvbWFpbhFtX2RvbWFpbl9pbXBsaWNpdAltX2V4cGlyZXMGbV9uYW1lBm1fcGF0aA9tX3BhdGhfaW1wbGljaXQGbV9wb3J0D21fcG9ydF9pbXBsaWNpdAttX3BvcnRfbGlzdAhtX3NlY3VyZQptX2h0dHBPbmx5C21fdGltZVN0YW1wB21fdmFsdWUJbV92ZXJzaW9uC21fZG9tYWluS2V5D0lzUXVvdGVkVmVyc2lvbg5Jc1F1b3RlZERvbWFpbgEEBAABAAABAQABAAcAAAABAAEAAApTeXN0ZW0uVXJpAgAAABhTeXN0ZW0uTmV0LkNvb2tpZVZhcmlhbnQCAAAAAQENAQEIAQENCAEBAgAAAAkEAAAACgXl////GFN5c3RlbS5OZXQuQ29va2llVmFyaWFudAEAAAAHdmFsdWVfXwAIAgAAAAEAAAAACQcAAAAAeDinzMsr34gGHQAAAAhTRVNTREFUQQkSAAAAAAkEAAAAAQoAAFZTPqICK9+IBiAAAAAQaXNzdWU1ODMtZml4dHVyZQAAAAAJBwAAAAAAARkAAAAYAAAACQQAAAAKAd3////l////AQAAAAAJBwAAAAB4OKfMyyvfiAYlAAAACGJpbGlfamN0CRIAAAAACQQAAAABCgAAhJ9AogIr34gGKAAAABBpc3N1ZTU4My1maXh0dXJlAAAAAAkHAAAAAAAL";

    [Fact]
    public void SparseLegacyCookieContainerSkipsNullSlots()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(SparseCookieContainer));

        var cookies = LegacyUpgradeCoordinator.ReadLegacyCookies(
            stream,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            cookies,
            cookie =>
            {
                Assert.Equal("SESSDATA", cookie.Name);
                Assert.Equal("issue583-fixture", cookie.Value);
                Assert.Equal(".bilibili.com", cookie.Domain);
            },
            cookie =>
            {
                Assert.Equal("bili_jct", cookie.Name);
                Assert.Equal("issue583-fixture", cookie.Value);
                Assert.Equal(".bilibili.com", cookie.Domain);
            });
    }

    [Fact]
    public void LegacyCookieArraysSkipOnlyNullSlotsAndRejectUnexpectedValues()
    {
        Assert.Empty(LegacyUpgradeCoordinator.EnumerateClassRecords(
            new object?[] { null },
            "fixture"));

        var exception = Assert.Throws<InvalidDataException>(() =>
            LegacyUpgradeCoordinator.EnumerateClassRecords(
                    new object?[] { null, "unexpected" },
                    "fixture")
                .ToArray());

        Assert.Contains("fixture", exception.Message, StringComparison.Ordinal);
        Assert.Contains("System.String", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigratedHistoryBatchPreservesRecordsAndCancellationToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var records = new[] { CreateHistory("first"), CreateHistory("second") };
        var written = new List<DownloadHistoryRecord>();
        var history = new StubHistoryService((record, token) =>
        {
            Assert.Equal(cancellationToken, token);
            written.Add(record);
            return Task.FromResult(OperationResult.Success());
        });

        await CreateCoordinator(history).PersistBatchAsync(records, cancellationToken);

        Assert.Equal(records, written);
    }

    [Fact]
    public async Task FailedHistoryWriteStopsTheBatchAndRetainsMigrationFailureSemantics()
    {
        var written = new List<string>();
        var history = new StubHistoryService((record, _) =>
        {
            written.Add(record.Id.Value);
            return Task.FromResult(written.Count == 1
                ? OperationResult.Success()
                : OperationResult.Failure(new OperationError(
                    "test.history.failed", "History write failed.", OperationErrorKind.Conflict)));
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateCoordinator(history).PersistBatchAsync(
                [CreateHistory("first"), CreateHistory("failed"), CreateHistory("remaining")],
                TestContext.Current.CancellationToken));

        Assert.Equal("History write failed.", failure.Message);
        Assert.True(LegacyUpgradeCoordinator.IsMigrationException(failure));
        Assert.Equal(["first", "failed"], written);
    }

    [Fact]
    public async Task CanceledHistoryWritePropagatesCancellationAndStopsTheBatch()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var writes = 0;
        var history = new StubHistoryService(async (_, token) =>
        {
            writes++;
            await cancellation.CancelAsync().ConfigureAwait(true);
            return await Task.FromCanceled<OperationResult>(token).ConfigureAwait(true);
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateCoordinator(history).PersistBatchAsync(
                [CreateHistory("canceled"), CreateHistory("remaining")], cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(LegacyUpgradeCoordinator.IsMigrationException(failure));
        Assert.Equal(1, writes);
    }

    private static LegacyUpgradeCoordinator CreateCoordinator(IDownloadHistoryService history) =>
        new(history, new SystemClock(), NullLogger<LegacyUpgradeCoordinator>.Instance);

    private static DownloadHistoryRecord CreateHistory(string id) =>
        new(new DownloadTaskId(id), 42, 1, 1, "Series", "Episode", "01:00", "AVC",
            80, "1080P", "AAC", "1 GB", [], 1_700_000_000, "finished", "25 MB/s",
            DownloadContentSelection.None);

    private sealed class StubHistoryService(
        Func<DownloadHistoryRecord, CancellationToken, Task<OperationResult>> addAsync)
        : IDownloadHistoryService
    {
        public Task<OperationResult> AddAsync(DownloadHistoryRecord history, CancellationToken cancellationToken) =>
            addAsync(history, cancellationToken);

        public Task<OperationResult> CompleteAsync(
            DownloadTask completedTask, long expectedVersion, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DownloadHistoryPage> GetPageAsync(
            DownloadHistoryCursor? cursor, int pageSize, CancellationToken cancellationToken,
            DownKyi.Application.Settings.DownloadFinishedSort sort = DownKyi.Application.Settings.DownloadFinishedSort.DownloadDesc) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadHistoryRecord>> GetByCidAsync(long cid, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadHistoryRecord>> GetByCidsAsync(
            IReadOnlyCollection<long> cids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult> ClearAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
