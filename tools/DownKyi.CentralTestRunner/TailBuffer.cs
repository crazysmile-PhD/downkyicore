namespace DownKyi.CentralTestRunner;

internal sealed class TailBuffer
{
    private readonly int maximumCharacters;
    private readonly Queue<string> lines = new();
    private readonly object synchronization = new();
    private int characters;

    public TailBuffer(int maximumCharacters)
    {
        this.maximumCharacters = maximumCharacters;
    }

    public string Value
    {
        get
        {
            lock (synchronization)
            {
                return string.Join(Environment.NewLine, lines);
            }
        }
    }

    public void Add(string line)
    {
        lock (synchronization)
        {
            var retained = line.Length > maximumCharacters
                ? line[^maximumCharacters..]
                : line;
            lines.Enqueue(retained);
            characters += retained.Length + Environment.NewLine.Length;
            while (characters > maximumCharacters && lines.Count > 1)
            {
                characters -= lines.Dequeue().Length + Environment.NewLine.Length;
            }
            if (characters > maximumCharacters && lines.Count == 1)
            {
                var onlyLine = lines.Dequeue();
                var keep = Math.Max(0, maximumCharacters - Environment.NewLine.Length);
                retained = keep == 0 ? string.Empty : onlyLine[^Math.Min(keep, onlyLine.Length)..];
                lines.Enqueue(retained);
                characters = retained.Length + Environment.NewLine.Length;
            }
        }
    }
}
