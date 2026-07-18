using System;
using System.Threading;
using DownKyi.Core.Aria2cNet.Client;

namespace DownKyi.Services.Download;

internal sealed class AriaRuntimeClientRegistry
{
    private readonly Lock _sync = new();
    private AriaClient? _current;

    public AriaClient? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public IDisposable Activate(AriaClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_sync)
        {
            if (_current != null && !ReferenceEquals(_current, client))
            {
                throw new InvalidOperationException("An aria2 runtime is already active.");
            }

            _current = client;
            return new Registration(this, client);
        }
    }

    private void Release(AriaClient client)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, client))
            {
                _current = null;
            }
        }
    }

    private sealed class Registration(
        AriaRuntimeClientRegistry owner,
        AriaClient client) : IDisposable
    {
        private AriaRuntimeClientRegistry? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(client);
        }
    }
}
