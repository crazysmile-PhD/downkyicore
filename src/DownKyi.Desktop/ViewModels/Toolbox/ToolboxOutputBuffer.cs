using System;

namespace DownKyi.ViewModels.Toolbox;

internal sealed class ToolboxOutputBuffer(Action<string> publish, Action<Action> dispatch)
{
    internal const int MaximumCharacters = 64 * 1024;
    private readonly object _gate = new();
    private string _text = string.Empty;
    private long _generation;
    private bool _pending;

    public Action<string> BeginOperation()
    {
        long generation;
        lock (_gate)
        {
            generation = ++_generation;
            _text = string.Empty;
        }
        publish(string.Empty);
        return output => Append(generation, output);
    }

    private void Append(long generation, string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            if (generation != _generation) return;
            var incoming = output.AsSpan();
            if (incoming.Length >= MaximumCharacters) incoming = incoming[^(MaximumCharacters - 1)..];
            if (incoming.Length > 0 && char.IsLowSurrogate(incoming[0])) incoming = incoming[1..];
            var retained = Math.Min(_text.Length, MaximumCharacters - incoming.Length - 1);
            var preceding = _text.AsSpan(_text.Length - retained);
            if (preceding.Length > 0 && char.IsLowSurrogate(preceding[0])) preceding = preceding[1..];
            _text = string.Concat(preceding, incoming, "\n");
            if (_pending) return;
            _pending = true;
        }
        dispatch(Flush);
    }

    private void Flush()
    {
        string text;
        lock (_gate)
        {
            text = _text;
            _pending = false;
        }
        publish(text);
    }
}
