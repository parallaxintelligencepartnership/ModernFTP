using System.Collections.Immutable;
using System.Threading.Channels;

namespace ModernFTP.Engine;

/// <summary>
/// Thread safe multicast observable that never blocks the publisher. Events go into a bounded queue
/// that drops the oldest entry when full; one background task delivers them to the observers. A slow
/// or stalled observer therefore only loses events (reported by a periodic notice), it never stalls a
/// session. Observer exceptions never reach the server.
/// </summary>
internal sealed class EventHub : IObservable<ServerEvent>
{
    public const int Capacity = 10_000;

    private static readonly TimeSpan NoticeInterval = TimeSpan.FromSeconds(5);

    private readonly Channel<ServerEvent> _queue;
    private readonly Task _dispatcher;
    private ImmutableArray<IObserver<ServerEvent>> _observers = ImmutableArray<IObserver<ServerEvent>>.Empty;
    private long _dropped;
    private long _droppedTotal;
    private long _lastNotice = long.MinValue;

    public EventHub()
    {
        _queue = Channel.CreateBounded<ServerEvent>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ =>
            {
                Interlocked.Increment(ref _dropped);
                Interlocked.Increment(ref _droppedTotal);
            });
        _dispatcher = Task.Run(DispatchAsync);
    }

    /// <summary>Events discarded since the hub was created because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedTotal);

    public IDisposable Subscribe(IObserver<ServerEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ImmutableInterlocked.Update(ref _observers, list => list.Add(observer));
        return new Subscription(this, observer);
    }

    /// <summary>Queues the event and returns at once; never waits for an observer.</summary>
    public void Publish(ServerEvent serverEvent)
    {
        if (_observers.IsEmpty)
        {
            return;
        }

        _queue.Writer.TryWrite(serverEvent);
    }

    /// <summary>Stops accepting events and waits briefly for queued ones to be delivered.</summary>
    public async Task CompleteAsync(TimeSpan drainTimeout)
    {
        _queue.Writer.TryComplete();
        try
        {
            await _dispatcher.WaitAsync(drainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A stalled observer keeps the dispatcher busy; it is abandoned.
        }
    }

    private async Task DispatchAsync()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var serverEvent))
            {
                Deliver(serverEvent);
                ReportDropsIfDue();
            }
        }

        ReportDropsIfDue(force: true);
    }

    private void ReportDropsIfDue(bool force = false)
    {
        if (Interlocked.Read(ref _dropped) == 0)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!force && _lastNotice != long.MinValue && now - _lastNotice < NoticeInterval.TotalMilliseconds)
        {
            return;
        }

        _lastNotice = now;
        var count = Interlocked.Exchange(ref _dropped, 0);
        Deliver(new ErrorEvent
        {
            SessionId = 0,
            Message = $"{count} events were dropped because an event subscriber could not keep up.",
        });
    }

    private void Deliver(ServerEvent serverEvent)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnNext(serverEvent);
            }
            catch
            {
                // An observer bug must never take down the server.
            }
        }
    }

    private void Unsubscribe(IObserver<ServerEvent> observer) =>
        ImmutableInterlocked.Update(ref _observers, list => list.Remove(observer));

    private sealed class Subscription(EventHub hub, IObserver<ServerEvent> observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                hub.Unsubscribe(observer);
            }
        }
    }
}

internal sealed class ActionObserver(Action<ServerEvent> onNext) : IObserver<ServerEvent>
{
    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void OnNext(ServerEvent value) => onNext(value);
}
