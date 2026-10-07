using System.Diagnostics;
using System.Windows.Threading;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>
/// Moves engine events to the UI thread in batches. The engine delivers events on its own background task;
/// they go into a bounded queue here (the oldest is dropped and counted when it is full), and one dispatcher
/// timer drains the whole queue every <see cref="Interval"/> and hands it over in a single call. The UI
/// thread therefore gets at most one call per tick however fast clients make the server publish.
/// </summary>
public sealed class EventBatcher : IDisposable
{
    public const int Capacity = 5000;

    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly Queue<ServerEvent> _queue = new();
    private readonly int _capacity;
    private readonly Action<IReadOnlyList<ServerEvent>, long> _onBatch;
    private readonly DispatcherTimer _timer;
    private long _dropped;
    private long _droppedTotal;

    /// <param name="onBatch">Called on the dispatcher thread with the queued events and how many were dropped since the last call.</param>
    public EventBatcher(Dispatcher dispatcher, Action<IReadOnlyList<ServerEvent>, long> onBatch, int capacity = Capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _onBatch = onBatch;
        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Flush(), dispatcher);
    }

    /// <summary>Events waiting for the next tick; never more than the capacity.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>How long the last <see cref="Flush"/> took to take the queue (measurements).</summary>
    public TimeSpan LastDrain { get; private set; }

    /// <summary>Events dropped since this batcher was created.</summary>
    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    /// <summary>Subscribes to the server; dispose the result to stop receiving its events.</summary>
    public IDisposable Attach(FtpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Subscribe(Post);
    }

    /// <summary>Queues one event. Safe from any thread; never blocks on the UI.</summary>
    public void Post(ServerEvent e)
    {
        lock (_gate)
        {
            _queue.Enqueue(e);
            if (_queue.Count > _capacity)
            {
                _queue.Dequeue();
                _dropped++;
                Interlocked.Increment(ref _droppedTotal);
            }
        }
    }

    /// <summary>Hands everything queued to the callback in one call. Runs on the timer; call it directly to flush at once.</summary>
    public void Flush()
    {
        ServerEvent[] batch;
        long dropped;
        var watch = Stopwatch.StartNew();
        lock (_gate)
        {
            batch = [.. _queue];
            _queue.Clear();
            dropped = _dropped;
            _dropped = 0;
        }

        LastDrain = watch.Elapsed;

        if (batch.Length > 0 || dropped > 0)
        {
            _onBatch(batch, dropped);
        }
    }

    public void Dispose() => _timer.Stop();
}
