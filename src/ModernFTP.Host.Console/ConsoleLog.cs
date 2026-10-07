using System.Threading.Channels;

namespace ModernFTP.Host.Console;

/// <summary>
/// Writes log lines from one dedicated thread, so a blocked stdout (a paused terminal, a console in
/// selection mode, an undrained pipe) delays only this writer. Callers never block: when the queue is
/// full the oldest lines are dropped.
/// </summary>
internal sealed class ConsoleLog : IAsyncDisposable
{
    public const int Capacity = 10_000;

    private readonly TextWriter _output;
    private readonly Channel<string> _lines;
    private readonly Task _writer;

    public ConsoleLog(TextWriter output)
    {
        _output = output;
        _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void WriteLine(string line) => _lines.Writer.TryWrite(line);

    /// <summary>Stops accepting lines and gives the writer a short time to flush what is queued.</summary>
    public async ValueTask DisposeAsync()
    {
        _lines.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // stdout is blocked; the remaining lines are abandoned.
        }
    }

    private void WriteLoop()
    {
        var reader = _lines.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var line))
            {
                try
                {
                    _output.WriteLine(line);
                }
                catch (IOException)
                {
                    // stdout was closed; keep draining so callers stay unblocked.
                }
            }
        }
    }
}
