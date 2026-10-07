using System.Collections.Immutable;

namespace ModernFTP.Engine;

/// <summary>Minimal thread safe multicast observable. Observer exceptions never reach the server.</summary>
internal sealed class EventHub : IObservable<ServerEvent>
{
    private ImmutableArray<IObserver<ServerEvent>> _observers = ImmutableArray<IObserver<ServerEvent>>.Empty;

    public IDisposable Subscribe(IObserver<ServerEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ImmutableInterlocked.Update(ref _observers, list => list.Add(observer));
        return new Subscription(this, observer);
    }

    public void Publish(ServerEvent serverEvent)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnNext(serverEvent);
            }
            catch
            {
                // An observer bug must never take down a session.
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
