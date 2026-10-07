using System.Net;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App.Tests;

public class UsersPageViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static SessionInfo Session(long id, string? user, string ip, TransferInfo? transfer = null, DateTimeOffset? lastActivity = null) =>
        new()
        {
            Id = id,
            User = user,
            RemoteAddress = IPAddress.Parse(ip),
            ConnectedAt = Start,
            LastActivity = lastActivity ?? Start,
            BytesSent = 0,
            BytesReceived = 0,
            CurrentTransfer = transfer,
        };

    private static TransferInfo Download(long done, long? total) =>
        new() { Path = "/a/b/file.bin", Direction = TransferDirection.Download, BytesDone = done, TotalBytes = total, StartedAt = Start };

    [Fact]
    public void SnapshotBecomesRowsAndRowsAreReusedAndRemoved()
    {
        var model = new UsersPageViewModel();
        model.Refresh([Session(1, null, "10.0.0.1"), Session(2, "bob", "10.0.0.2", lastActivity: Start.AddSeconds(-65))], Start);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal("-", model.Rows[0].User);
        Assert.Equal("bob", model.Rows[1].User);
        Assert.Equal("10.0.0.2", model.Rows[1].Ip);
        Assert.Equal("00:01:05", model.Rows[1].Idle);
        Assert.False(model.Rows[1].HasTransfer);

        var first = model.Rows[0];
        model.Refresh([Session(1, "alice", "10.0.0.1")], Start.AddSeconds(1));
        Assert.Same(first, Assert.Single(model.Rows));
        Assert.Equal("alice", first.User);
    }

    [Fact]
    public void ProgressIsPercentOfTotalAndIndeterminateWithoutOne()
    {
        var model = new UsersPageViewModel();
        model.Refresh([Session(1, "a", "10.0.0.1", Download(250, 1000))], Start);
        var row = model.Rows[0];
        Assert.Equal("file.bin", row.Transfer);
        Assert.Equal(25, row.Progress);
        Assert.False(row.IsIndeterminate);

        model.Refresh([Session(1, "a", "10.0.0.1", Download(5000, null))], Start.AddSeconds(1));
        Assert.True(row.IsIndeterminate);
        Assert.Equal("-", row.TimeLeft);

        model.Refresh([Session(1, "a", "10.0.0.1")], Start.AddSeconds(2));
        Assert.False(row.HasTransfer);
        Assert.False(row.IsIndeterminate);
        Assert.Equal(string.Empty, row.TimeLeft);
    }

    [Fact]
    public void TimeLeftUsesTheRateOverTheLastFiveSeconds()
    {
        var model = new UsersPageViewModel();
        // 100 B/s for the first 10 seconds, then 1000 B/s: only the last 5 seconds count.
        for (var second = 0; second <= 10; second++)
        {
            model.Refresh([Session(1, "a", "10.0.0.1", Download(second * 100, 100_000))], Start.AddSeconds(second));
        }

        for (var second = 11; second <= 16; second++)
        {
            model.Refresh([Session(1, "a", "10.0.0.1", Download(1000 + (second - 10) * 1000, 100_000))], Start.AddSeconds(second));
        }

        // done 7000 of 100000 at 1000 B/s: 93 seconds left.
        Assert.Equal("00:01:33", model.Rows[0].TimeLeft);
    }

    [Fact]
    public void ClearEmptiesTheRows()
    {
        var model = new UsersPageViewModel();
        model.Refresh([Session(1, "a", "10.0.0.1")], Start);
        model.Clear();
        Assert.Empty(model.Rows);
    }
}
