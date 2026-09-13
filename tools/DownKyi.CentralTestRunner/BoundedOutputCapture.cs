using System.Text;

namespace DownKyi.CentralTestRunner;

internal static class BoundedOutputCapture
{
    private const int MaximumOutputLineCharacters = 8192;
    private const string TruncatedOutputLine = "[output line exceeded 8192 characters and was discarded]";

    internal static async Task CaptureAsync(
        StreamReader reader,
        TailBuffer tail,
        TextWriter destination,
        SensitiveEvidenceRedactor redactor,
        CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new char[4096];
            var line = new StringBuilder(MaximumOutputLineCharacters);
            var discardingLine = false;
            var previousWasCarriageReturn = false;

            async Task FlushLineAsync()
            {
                var redacted = discardingLine
                    ? TruncatedOutputLine
                    : redactor.Redact(line.ToString());
                tail.Add(redacted);
                await destination.WriteLineAsync(redacted).ConfigureAwait(false);
                line.Clear();
                discardingLine = false;
            }

            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                for (var index = 0; index < count; index++)
                {
                    var character = buffer[index];
                    if (character == '\n')
                    {
                        if (!previousWasCarriageReturn)
                        {
                            await FlushLineAsync().ConfigureAwait(false);
                        }
                        previousWasCarriageReturn = false;
                        continue;
                    }

                    if (character == '\r')
                    {
                        await FlushLineAsync().ConfigureAwait(false);
                        previousWasCarriageReturn = true;
                        continue;
                    }

                    previousWasCarriageReturn = false;
                    if (discardingLine)
                    {
                        continue;
                    }

                    if (line.Length == MaximumOutputLineCharacters)
                    {
                        line.Clear();
                        discardingLine = true;
                        continue;
                    }

                    line.Append(character);
                }
            }

            if (line.Length > 0 || discardingLine)
            {
                await FlushLineAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The existing cleanup bound ended stream collection.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Process disposal closes redirected streams after bounded cleanup.
        }
    }
}
