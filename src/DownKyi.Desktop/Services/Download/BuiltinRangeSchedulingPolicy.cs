using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DownKyi.Services.Download;

/// <summary>
/// One logical transfer's speculation decisions and cost budget, shared across
/// source attempts. It owns no file positions, writers or retry transitions.
/// </summary>
internal sealed class BuiltinRangeSchedulingPolicy
{
    private const double MinimumBytesPerSecond = 16 * 1024;
    private const double EstimatedBackupRateFactor = 0.75;
    private const double RequiredDurationReduction = 0.25;
    private readonly Lock _gate = new();
    private long? _budget;
    private long _spent;
    private BuiltinRangeHedge? _active;
    private long? _lastCompletion;
    private int _ineffectiveHedges;

    public BuiltinRangeSchedulingPolicy(TimeProvider clock, TimeSpan? warmup = null)
    {
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Warmup = warmup ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Warmup, TimeSpan.Zero);
    }

    public TimeProvider Clock { get; }
    public TimeSpan Warmup { get; }
    public TimeSpan PollInterval => TimeSpan.FromTicks(Math.Min(Warmup.Ticks / 2 + 1, TimeSpan.FromMilliseconds(500).Ticks));

    public void InitializeBudget(long remainingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(remainingBytes);
        lock (_gate)
        {
            // At most 10% extra traffic, with one bounded range of bootstrap
            // allowance for small transfers. Retrying cannot refill this budget.
            _budget ??= Math.Min(remainingBytes, Math.Max(BuiltinRangeDownloader.DefaultSegmentSize, remainingBytes / 10));
        }
    }

    public BuiltinRangeHedge? TryReserve(IReadOnlyList<BuiltinRangeSample> samples, bool limited)
    {
        lock (_gate)
        {
            var cooldown = TimeSpan.FromTicks(Warmup.Ticks * (1L << Math.Min(_ineffectiveHedges, 3)));
            if (limited || _active != null || _budget == null ||
                (_lastCompletion is { } completed && Clock.GetElapsedTime(completed) < cooldown))
            {
                return null;
            }

            var selected = SelectCandidate(samples, Warmup, _budget.Value - _spent);
            if (selected is not { } candidate)
            {
                return null;
            }

            _active = new BuiltinRangeHedge(candidate.Chunk, candidate.RemainingBytes);
            return _active;
        }
    }

    public void Complete(BuiltinRangeHedge hedge, bool won)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, hedge))
            {
                throw new InvalidOperationException("A range reservation can only be completed by its owner.");
            }

            _spent += hedge.BytesReceived;
            _active = null;
            if (hedge.Started)
            {
                _lastCompletion = Clock.GetTimestamp();
                _ineffectiveHedges = won ? 0 : Math.Min(_ineffectiveHedges + 1, 3);
            }
        }
    }

    internal static BuiltinRangeSample? SelectCandidate(
        IReadOnlyList<BuiltinRangeSample> samples, TimeSpan warmup, long availableBytes)
    {
        BuiltinRangeSample? selected = null;
        var longest = 0.0;
        foreach (var sample in samples)
        {
            if (sample.Finished || !sample.NetworkWaiting || sample.Age < warmup ||
                sample.RemainingBytes <= 0 || sample.RemainingBytes > BuiltinRangeDownloader.DefaultSegmentSize ||
                sample.RemainingBytes > availableBytes)
            {
                continue;
            }
            if (!CanImprove(sample, samples, warmup, out var primarySeconds))
            {
                continue;
            }

            if (selected == null || primarySeconds > longest ||
                primarySeconds == longest && sample.RemainingBytes > selected.Value.RemainingBytes)
            {
                selected = sample;
                longest = primarySeconds;
            }
        }

        return selected;
    }

    private static bool CanImprove(BuiltinRangeSample sample, IReadOnlyList<BuiltinRangeSample> samples,
        TimeSpan warmup, out double primarySeconds)
    {
        var peers = samples.Where(peer => !ReferenceEquals(peer.Chunk, sample.Chunk) &&
            peer.HasRate && !peer.Finished).Select(static peer => peer.BytesPerSecond).ToArray();
        if (peers.Length == 0)
        {
            peers = samples.Where(peer => !ReferenceEquals(peer.Chunk, sample.Chunk) &&
                peer.HasRate && peer.Finished && peer.FinishedAgo <= warmup * 3)
                .Select(static peer => peer.BytesPerSecond).ToArray();
        }

        Array.Sort(peers);
        var reference = peers.Length == 0 ? MinimumBytesPerSecond : peers[peers.Length / 2];
        primarySeconds = sample.BytesPerSecond > 0 ? sample.RemainingBytes / sample.BytesPerSecond : double.PositiveInfinity;
        // Equal slow peers indicate a shared bottleneck. With no peer evidence,
        // permit only a bounded probe of an absolutely slow/stalled range.
        if (peers.Length == 0 && sample.BytesPerSecond >= MinimumBytesPerSecond ||
            peers.Length > 0 && sample.BytesPerSecond >= reference / 3)
        {
            return false;
        }

        var backupSeconds = sample.HeaderSeconds + sample.RemainingBytes / Math.Max(1, reference * EstimatedBackupRateFactor);
        return primarySeconds - backupSeconds >= warmup.TotalSeconds && backupSeconds <= primarySeconds * (1 - RequiredDurationReduction);
    }
}

internal readonly record struct BuiltinRangeSample(
    BuiltinRangeChunk Chunk, long RemainingBytes, bool NetworkWaiting,
    bool HasRate, double BytesPerSecond, double HeaderSeconds,
    TimeSpan Age, bool Finished, TimeSpan FinishedAgo);

internal sealed class BuiltinRangeHedge(BuiltinRangeChunk chunk, long reservedBytes)
{
    private long _bytes;
    public BuiltinRangeChunk Chunk { get; } = chunk;
    public long ReservedBytes { get; } = reservedBytes;
    public long BytesReceived => Interlocked.Read(ref _bytes);
    public bool Started { get; set; }
    public void RecordBytes(int count) => Interlocked.Add(ref _bytes, count);
}
