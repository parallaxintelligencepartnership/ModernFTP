using System.Collections.ObjectModel;
using ModernFTP.Engine;

namespace ModernFTP.App;

/// <summary>
/// Logic behind the Users page: turns session snapshots into rows. Rows are reused between refreshes so the
/// selection survives. Time left comes from the transfer rate over the last <see cref="RateWindow"/>.
/// </summary>
public sealed class UsersPageViewModel
{
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(5);

    private readonly Dictionary<long, SessionRow> _byId = [];
    private readonly Dictionary<long, List<(DateTimeOffset At, long Bytes)>> _samples = [];
    private readonly Dictionary<long, DateTimeOffset> _transferStart = [];

    public ObservableCollection<SessionRow> Rows { get; } = [];

    public void Clear()
    {
        Rows.Clear();
        _byId.Clear();
        _samples.Clear();
        _transferStart.Clear();
    }

    public void Refresh(IReadOnlyList<SessionInfo> snapshot, DateTimeOffset now)
    {
        var open = snapshot.Select(s => s.Id).ToHashSet();
        foreach (var gone in _byId.Keys.Where(id => !open.Contains(id)).ToList())
        {
            Rows.Remove(_byId[gone]);
            _byId.Remove(gone);
            _samples.Remove(gone);
            _transferStart.Remove(gone);
        }

        foreach (var info in snapshot.OrderBy(s => s.Id))
        {
            if (!_byId.TryGetValue(info.Id, out var row))
            {
                row = new SessionRow(info.Id, info.RemoteAddress.ToString());
                _byId[info.Id] = row;
                Rows.Add(row);
            }

            Update(row, info, now);
        }
    }

    private void Update(SessionRow row, SessionInfo info, DateTimeOffset now)
    {
        row.User = info.User ?? "-";
        row.Idle = SessionRow.FormatSpan(now - info.LastActivity);

        var transfer = info.CurrentTransfer;
        if (transfer is null)
        {
            row.Transfer = string.Empty;
            row.Progress = 0;
            row.IsIndeterminate = false;
            row.TimeLeft = string.Empty;
            _samples.Remove(info.Id);
            _transferStart.Remove(info.Id);
            return;
        }

        // A new transfer on the same session starts a new rate history.
        if (_transferStart.TryGetValue(info.Id, out var started) && started != transfer.StartedAt)
        {
            _samples.Remove(info.Id);
        }

        _transferStart[info.Id] = transfer.StartedAt;
        row.Transfer = transfer.Path[(transfer.Path.LastIndexOf('/') + 1)..];
        if (transfer.TotalBytes is not { } total)
        {
            row.IsIndeterminate = true;
            row.Progress = 0;
            row.TimeLeft = "-";
            return;
        }

        row.IsIndeterminate = false;
        row.Progress = Percent(transfer.BytesDone, total);
        row.TimeLeft = TimeLeft(info.Id, transfer.BytesDone, total, now);
    }

    public static double Percent(long done, long total) =>
        total <= 0 ? 100 : Math.Clamp(done * 100.0 / total, 0, 100);

    private string TimeLeft(long id, long done, long total, DateTimeOffset now)
    {
        if (!_samples.TryGetValue(id, out var samples))
        {
            samples = [];
            _samples[id] = samples;
        }

        samples.Add((now, done));
        samples.RemoveAll(s => now - s.At > RateWindow);
        var oldest = samples[0];
        var seconds = (now - oldest.At).TotalSeconds;
        if (seconds <= 0 || done <= oldest.Bytes)
        {
            return "-";
        }

        var rate = (done - oldest.Bytes) / seconds;
        var remaining = Math.Max(total - done, 0);
        return SessionRow.FormatSpan(TimeSpan.FromSeconds(Math.Min(Math.Ceiling(remaining / rate), TimeSpan.FromHours(99).TotalSeconds)));
    }
}
