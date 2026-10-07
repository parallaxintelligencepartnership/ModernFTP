using System.Net;

namespace ModernFTP.Engine;

/// <summary>
/// Failed login counts per source address, kept in memory only (a restart clears them). Failures older than the
/// window do not count; reaching twice the limit bans the address for the ban duration.
/// </summary>
internal sealed class LoginThrottle
{
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, Entry> _entries = [];

    public bool IsBanned(IPAddress address, long? nowMs = null)
    {
        var now = nowMs ?? Environment.TickCount64;
        lock (_gate)
        {
            return _entries.TryGetValue(address, out var entry) && entry.BannedUntil > now;
        }
    }

    /// <summary>Failures from the address inside the window.</summary>
    public int RecentFailures(IPAddress address, TimeSpan window, long? nowMs = null)
    {
        var now = nowMs ?? Environment.TickCount64;
        lock (_gate)
        {
            if (!_entries.TryGetValue(address, out var entry))
            {
                return 0;
            }

            Trim(entry, window, now);
            return entry.Failures.Count;
        }
    }

    /// <summary>Records one failure; true when it caused a ban.</summary>
    public bool RecordFailure(IPAddress address, FtpServerOptions options, long? nowMs = null)
    {
        if (options.LoginFailureLimit <= 0)
        {
            return false;
        }

        var now = nowMs ?? Environment.TickCount64;
        lock (_gate)
        {
            if (_entries.Count > 10_000)
            {
                foreach (var key in _entries.Where(e => e.Value.BannedUntil <= now && e.Value.Failures.Count == 0).Select(e => e.Key).ToList())
                {
                    _entries.Remove(key);
                }
            }

            if (!_entries.TryGetValue(address, out var entry))
            {
                entry = new Entry();
                _entries[address] = entry;
            }

            Trim(entry, options.LoginFailureWindow, now);
            entry.Failures.Enqueue(now);
            if (entry.Failures.Count >= options.LoginFailureLimit * 2 && entry.BannedUntil <= now)
            {
                entry.BannedUntil = now + (long)options.LoginBanDuration.TotalMilliseconds;
                entry.Failures.Clear();
                return true;
            }

            return false;
        }
    }

    private static void Trim(Entry entry, TimeSpan window, long now)
    {
        while (entry.Failures.Count > 0 && now - entry.Failures.Peek() > window.TotalMilliseconds)
        {
            entry.Failures.Dequeue();
        }
    }

    private sealed class Entry
    {
        public Queue<long> Failures { get; } = new();

        public long BannedUntil { get; set; }
    }
}
