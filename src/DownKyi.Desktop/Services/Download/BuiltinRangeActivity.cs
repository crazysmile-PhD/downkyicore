using System;
using System.Threading;
using System.Threading.Tasks;

namespace DownKyi.Services.Download;

/// <summary>Ephemeral network observations; the chunk remains the position owner.</summary>
internal sealed class BuiltinRangeActivity(BuiltinRangeChunk chunk, TimeProvider clock, TimeSpan smoothingWindow)
{
    private readonly Lock _gate = new();
    private readonly long _started = clock.GetTimestamp();
    private long _waitingSince;
    private bool _waiting;
    private double _networkSeconds;
    private double _sampleSeconds;
    private double _lastByteSeconds;
    private long _bytes;
    private long _sampleBytes;
    private double _rate;
    private bool _hasRate;
    private double _headerSeconds;
    private long? _finished;
    private BuiltinRangeHedge? _hedge;

    public TaskCompletionSource<BuiltinRangeHedge> HedgeRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetWaiting(bool waiting)
    {
        lock (_gate)
        {
            if (_waiting == waiting)
            {
                return;
            }

            if (waiting)
            {
                _waitingSince = clock.GetTimestamp();
            }
            else
            {
                _networkSeconds += clock.GetElapsedTime(_waitingSince).TotalSeconds;
            }

            _waiting = waiting;
        }
    }

    public void HeadersReceived()
    {
        lock (_gate)
        {
            _headerSeconds = clock.GetElapsedTime(_started).TotalSeconds;
        }
    }

    public void RecordBytes(int count)
    {
        lock (_gate)
        {
            _bytes += count;
            if (count > 0)
            {
                _lastByteSeconds = NetworkSeconds(clock.GetTimestamp());
            }
        }
    }

    public BuiltinRangeSample Sample()
    {
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            var seconds = NetworkSeconds(now);
            var elapsed = seconds - _sampleSeconds;
            if (elapsed > 0)
            {
                var measured = (_bytes - _sampleBytes) / elapsed;
                // A sliding estimate forgets early bursts and decays during a
                // read stall. Local disk/bandwidth waits add no network time.
                var alpha = 1 - Math.Exp(-elapsed / smoothingWindow.TotalSeconds);
                _rate = _hasRate && _rate > 0 ? _rate + alpha * (measured - _rate) : measured;
                _hasRate = true;
                _sampleSeconds = seconds;
                _sampleBytes = _bytes;
            }

            // Expire a burst after a full silent window. EMA alone can otherwise
            // hide a stalled read until the whole-source watchdog replaces it.
            if (seconds - _lastByteSeconds >= smoothingWindow.TotalSeconds * 2)
            {
                _rate = 0;
            }

            return new BuiltinRangeSample(chunk, chunk.End - chunk.Start + 1 - chunk.Position,
                _waiting, _hasRate, _rate, _headerSeconds, clock.GetElapsedTime(_started, now),
                _finished != null, _finished is { } finished ? clock.GetElapsedTime(finished, now) : TimeSpan.Zero);
        }
    }

    private double NetworkSeconds(long now) =>
        _networkSeconds + (_waiting ? clock.GetElapsedTime(_waitingSince, now).TotalSeconds : 0);

    public bool TryRequest(BuiltinRangeHedge hedge)
    {
        lock (_gate)
        {
            if (_finished != null)
            {
                return false;
            }

            _hedge = hedge;
            return HedgeRequested.TrySetResult(hedge);
        }
    }

    public BuiltinRangeHedge? Finish()
    {
        lock (_gate)
        {
            _finished = clock.GetTimestamp();
            return _hedge;
        }
    }
}
