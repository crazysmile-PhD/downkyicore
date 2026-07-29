using DownKyi.Application.Diagnostics;

namespace DownKyi.Infrastructure.Logging;

internal sealed class ApplicationRecentLogBuffer
{
    private readonly Queue<ApplicationLogRecord> _events;
    private readonly int _capacity;
    private readonly object _gate = new();

    public ApplicationRecentLogBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _events = new Queue<ApplicationLogRecord>(capacity);
    }

    public void Add(ApplicationLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _events.Enqueue(record);
            while (_events.Count > _capacity)
            {
                _events.Dequeue();
            }
        }
    }

    public IReadOnlyList<ApplicationLogRecord> Snapshot()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }
}
