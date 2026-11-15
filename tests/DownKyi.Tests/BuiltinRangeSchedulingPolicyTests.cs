using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class BuiltinRangeSchedulingPolicyTests
{
    private const long MiB = 1024 * 1024;
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);

    [Fact]
    public void CurrentPeersOverrideAnEarlierPeakWhenTheWholeConnectionSlows()
    {
        var target = Sample(20 * MiB, MiB);
        var peer = Sample(20 * MiB, MiB);
        var earlierPeak = Sample(0, 16 * MiB) with { Finished = true };
        Assert.Null(BuiltinRangeSchedulingPolicy.SelectCandidate([target, peer, earlierPeak], Warmup, 20 * MiB));
    }

    [Fact]
    public void EquallySlowPeersDoNotCompeteForTheSameBottleneck()
    {
        Assert.Null(BuiltinRangeSchedulingPolicy.SelectCandidate(
            [Sample(MiB, 1000), Sample(MiB, 1000)], Warmup, 20 * MiB));
    }

    [Fact]
    public void LargestPredictedCompletionTimeWinsInsteadOfTheFirstTimer()
    {
        var smaller = Sample(MiB, 1000);
        var larger = Sample(2 * MiB, 1000);
        var healthy = Sample(20 * MiB, MiB);
        Assert.Equal(larger, BuiltinRangeSchedulingPolicy.SelectCandidate([smaller, larger, healthy], Warmup, 20 * MiB));
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(1048576, 2000)]
    public void SmallRemainingWorkOrExpensiveSetupDoesNotJustifyABackup(long remaining, double setupSeconds)
    {
        var target = Sample(remaining, 1000) with { HeaderSeconds = setupSeconds };
        Assert.Null(BuiltinRangeSchedulingPolicy.SelectCandidate([target, Sample(20 * MiB, MiB)], Warmup, 20 * MiB));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LocalWorkAndWarmupAreIneligible(bool localWork, bool warmingUp)
    {
        var target = Sample(MiB, 0) with { NetworkWaiting = !localWork, Age = warmingUp ? TimeSpan.Zero : Warmup };
        Assert.Null(BuiltinRangeSchedulingPolicy.SelectCandidate([target], Warmup, 20 * MiB));
    }

    [Fact]
    public void CompletedSpeedEvidenceExpires()
    {
        var target = Sample(20 * MiB, MiB);
        var oldPeak = Sample(0, 16 * MiB) with { Finished = true, FinishedAgo = TimeSpan.FromMinutes(1) };
        Assert.Null(BuiltinRangeSchedulingPolicy.SelectCandidate([target, oldPeak], Warmup, 20 * MiB));
    }

    [Fact]
    public void ReservationsBoundTrafficRefundUnreadBytesAndCannotBeRefilledByRetry()
    {
        var clock = new TestClock();
        var policy = new BuiltinRangeSchedulingPolicy(clock);
        policy.InitializeBudget(40 * MiB); // One bootstrap range: 20 MiB.
        var sample = Sample(12 * MiB, 0);
        var first = Assert.IsType<BuiltinRangeHedge>(policy.TryReserve([sample], limited: false));
        Assert.Equal(12 * MiB, first.ReservedBytes);
        Assert.Null(policy.TryReserve([sample], limited: false));
        first.Started = true;
        first.RecordBytes(8 * (int)MiB);
        policy.Complete(first, won: true);
        policy.InitializeBudget(400 * MiB); // A different source cannot refill it.
        clock.Advance(Warmup);
        var second = Assert.IsType<BuiltinRangeHedge>(policy.TryReserve([sample], limited: false));
        second.Started = true;
        second.RecordBytes(12 * (int)MiB);
        policy.Complete(second, won: true);
        clock.Advance(Warmup);
        Assert.Null(policy.TryReserve([Sample(1, 0)], limited: false));
    }

    [Fact]
    public void LosingBackupsIncreaseCooldownAndUserLimitsDisableReservations()
    {
        var clock = new TestClock();
        var policy = new BuiltinRangeSchedulingPolicy(clock);
        policy.InitializeBudget(20 * MiB);
        var sample = Sample(MiB, 0);
        Assert.Null(policy.TryReserve([sample], limited: true));
        for (var loss = 1; loss <= 3; loss++)
        {
            var hedge = Assert.IsType<BuiltinRangeHedge>(policy.TryReserve([sample], limited: false));
            hedge.Started = true;
            policy.Complete(hedge, won: false);
            clock.Advance(Warmup * (1 << loss) - TimeSpan.FromTicks(1));
            Assert.Null(policy.TryReserve([sample], limited: false));
            clock.Advance(TimeSpan.FromTicks(1));
        }
    }

    [Fact]
    public void AWinningBackupResetsThePenaltyFromEarlierLosses()
    {
        var clock = new TestClock();
        var policy = new BuiltinRangeSchedulingPolicy(clock);
        policy.InitializeBudget(20 * MiB);
        var sample = Sample(MiB, 0);
        var loser = Assert.IsType<BuiltinRangeHedge>(policy.TryReserve([sample], limited: false));
        loser.Started = true;
        policy.Complete(loser, won: false);
        clock.Advance(Warmup * 2);
        var winner = Assert.IsType<BuiltinRangeHedge>(policy.TryReserve([sample], limited: false));
        winner.Started = true;
        policy.Complete(winner, won: true);
        clock.Advance(Warmup - TimeSpan.FromTicks(1));
        Assert.Null(policy.TryReserve([sample], limited: false));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.NotNull(policy.TryReserve([sample], limited: false));
    }

    [Fact]
    public void SilentReadExpiresAnEarlyBurstAtProductionPollingIntervalsAndRecoversImmediately()
    {
        var clock = new TestClock();
        var activity = new BuiltinRangeActivity(new BuiltinRangeChunk(0, 20 * MiB - 1, 0),
            clock, Warmup / 2);
        activity.SetWaiting(true);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        activity.RecordBytes(10 * (int)MiB);
        Assert.Equal(20 * MiB, activity.Sample().BytesPerSecond);
        for (var poll = 0; poll < 20; poll++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            activity.Sample();
        }

        var stalled = activity.Sample();
        Assert.Equal(0, stalled.BytesPerSecond);
        Assert.NotNull(BuiltinRangeSchedulingPolicy.SelectCandidate([stalled], Warmup, 20 * MiB));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        activity.RecordBytes((int)MiB);
        Assert.Equal(2 * MiB, activity.Sample().BytesPerSecond);
    }

    [Fact]
    public void RecentNetworkEstimateDecaysAfterABurstAndRecoversWithoutCountingDiskWait()
    {
        var clock = new TestClock();
        var chunk = new BuiltinRangeChunk(0, 20 * MiB - 1, 0);
        var activity = new BuiltinRangeActivity(chunk, clock, TimeSpan.FromSeconds(2));
        activity.SetWaiting(true);
        clock.Advance(TimeSpan.FromSeconds(1));
        activity.RecordBytes(10 * (int)MiB);
        Assert.Equal(10 * MiB, activity.Sample().BytesPerSecond);
        clock.Advance(TimeSpan.FromSeconds(20));
        var stalled = activity.Sample();
        Assert.True(stalled.BytesPerSecond < 1000);
        Assert.NotNull(BuiltinRangeSchedulingPolicy.SelectCandidate([stalled], Warmup, 20 * MiB));
        clock.Advance(TimeSpan.FromSeconds(10));
        activity.RecordBytes(10 * (int)MiB);
        var recovered = activity.Sample();
        Assert.True(recovered.BytesPerSecond > MiB * 0.9);
        activity.SetWaiting(false);
        clock.Advance(TimeSpan.FromMinutes(1));
        var disk = activity.Sample();
        Assert.Equal(recovered.BytesPerSecond, disk.BytesPerSecond);
        Assert.False(disk.NetworkWaiting);
    }

    private static BuiltinRangeSample Sample(long remaining, double speed) => new(
        new BuiltinRangeChunk(0, Math.Max(1, remaining) - 1, 0), remaining,
        true, true, speed, 0.1, TimeSpan.FromSeconds(20), false, TimeSpan.Zero);

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
