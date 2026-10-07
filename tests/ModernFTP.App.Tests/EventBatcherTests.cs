using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ModernFTP.Engine;
using Xunit.Abstractions;

namespace ModernFTP.App.Tests;

public class EventBatcherTests(ITestOutputHelper output)
{
    [Fact]
    public void QueueKeepsTheNewestFiveThousandAndCountsTheDropped()
    {
        RunOnDispatcher(dispatcher =>
        {
            var batches = new List<(IReadOnlyList<ServerEvent> Events, long Dropped)>();
            using var batcher = new EventBatcher(dispatcher, (events, dropped) => batches.Add((events, dropped)));
            for (var i = 1; i <= 12_000; i++)
            {
                batcher.Post(new ErrorEvent { SessionId = i, Message = "x" });
            }

            Assert.Equal(EventBatcher.Capacity, batcher.Pending);
            Assert.Equal(7_000, batcher.DroppedTotal);
            batcher.Flush();
            batcher.Flush();

            var (events, dropped) = Assert.Single(batches);
            Assert.Equal(7_000, dropped);
            Assert.Equal(5_000, events.Count);
            Assert.Equal(7_001, events[0].SessionId);
            Assert.Equal(12_000, events[^1].SessionId);

            // 500 lines at most per tick: the other 4,500 of this batch count as dropped too.
            var lines = LogView.Lines(events, dropped);
            Assert.Equal(LogView.MaxLinesPerBatch + 1, lines.Count);
            Assert.Equal("11500 events dropped", Assert.Single(lines[0]).Text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void RingKeepsTheNewestLinesAndRaisesOneResetPerBatch()
    {
        var ring = new LogRing(5);
        var notifications = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        ring.CollectionChanged += (_, e) => notifications.Add(e.Action);
        LogLine Line(int n) => new([new LogSegment(n.ToString(System.Globalization.CultureInfo.InvariantCulture), LogTag.Text)], false);

        ring.AppendBatch([.. Enumerable.Range(1, 3).Select(Line)]);
        ring.AppendBatch([.. Enumerable.Range(4, 6).Select(Line)]);

        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Reset, System.Collections.Specialized.NotifyCollectionChangedAction.Reset], notifications);
        Assert.Equal(["5", "6", "7", "8", "9"], ring.Select(l => l.Text));
        Assert.Equal("5", ring[0].Text);
        Assert.Equal(5, ring.Count);
    }

    /// <summary>
    /// The review's probe: one client that never logs in pipelines NOOP as fast as it can. Over 5 seconds, 20
    /// probes measure how long an input priority operation waits for the window thread; the median must stay
    /// under 250 ms and managed memory must stay flat. Each stage of a tick is timed and printed.
    /// </summary>
    [Fact]
    public void ANoopFloodFromAClientThatNeverLogsInKeepsTheWindowResponsive()
    {
        RunOnDispatcher(async dispatcher =>
        {
            // Same list setup as the main window's log pane: virtualized, one TextBlock per row.
            var row = new FrameworkElementFactory(typeof(TextBlock));
            row.SetBinding(LogView.LineProperty, new System.Windows.Data.Binding());
            var box = new ListBox { ItemTemplate = new DataTemplate { VisualTree = row } };
            VirtualizingPanel.SetIsVirtualizing(box, true);
            VirtualizingPanel.SetVirtualizationMode(box, VirtualizationMode.Recycling);
            var window = new Window
            {
                Content = box,
                Width = 800,
                Height = 600,
                Left = -2000,
                Top = -2000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            window.Show();
            var log = new LogView(box, () => false);
            var stages = new StageTimes();
            EventBatcher? batcherRef = null;
            using var batcher = new EventBatcher(dispatcher, (events, dropped) =>
            {
                var watch = Stopwatch.StartNew();
                var lines = LogView.Lines(events, dropped);
                var format = watch.Elapsed;
                log.Append(lines);
                watch.Restart();
                box.UpdateLayout();
                stages.Add(batcherRef!.LastDrain, format, log.LastUpdate, log.LastScroll, watch.Elapsed);
            });
            batcherRef = batcher;

            await using var server = new FtpServer(new FtpServerOptions { ListenAddress = IPAddress.Loopback, Port = 0, LoginTimeout = TimeSpan.FromMinutes(1) });
            await server.StartAsync();
            using var subscription = batcher.Attach(server);
            long published = 0;
            using var counter = server.Subscribe(_ => Interlocked.Increment(ref published));

            using var stop = new CancellationTokenSource();
            var flood = Task.Run(() => FloodAsync(server.LocalEndPoint!.Port, stop.Token));

            var probe = Task.Run(async () =>
            {
                var latencies = new List<double>();
                var memory = new List<long>();
                await Task.Delay(250);
                for (var i = 0; i < 20; i++)
                {
                    var sent = Stopwatch.GetTimestamp();
                    await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Input);
                    var waited = Stopwatch.GetElapsedTime(sent);
                    latencies.Add(waited.TotalMilliseconds);
                    if (i % 4 == 0)
                    {
                        memory.Add(GC.GetTotalMemory(forceFullCollection: false));
                    }

                    var rest = TimeSpan.FromMilliseconds(250) - waited;
                    if (rest > TimeSpan.Zero)
                    {
                        await Task.Delay(rest);
                    }
                }

                return (latencies, memory);
            });

            var (latencies, samples) = await probe;
            await stop.CancelAsync();
            await flood;
            var sorted = latencies.Order().ToList();
            var median = (sorted[9] + sorted[10]) / 2;
            var growth = samples[^1] - samples[1];
            var summary = $"events published {Interlocked.Read(ref published)}, dropped in app {batcher.DroppedTotal}, " +
                $"input latency median {median:0} ms max {sorted[^1]:0} ms over {latencies.Count} probes, managed memory growth {growth / (1024 * 1024)} MB, " +
                $"log lines {log.LineCount}, pending {batcher.Pending}; per tick {stages}";
            output.WriteLine(summary);

            Assert.True(Interlocked.Read(ref published) > 20_000, summary);
            Assert.True(batcher.Pending <= EventBatcher.Capacity, summary);
            Assert.True(log.LineCount <= LogView.MaxLines, summary);
            Assert.True(median < 250, summary);
            Assert.True(growth < 64L * 1024 * 1024, summary);
            window.Close();
        });
    }

    /// <summary>Per stage tick times: drain (take the queue), format, collection update, scroll, layout.</summary>
    private sealed class StageTimes
    {
        private readonly string[] _names = ["drain", "format", "update", "scroll", "layout"];
        private readonly double[] _total = new double[5];
        private readonly double[] _max = new double[5];
        private int _ticks;

        public void Add(params TimeSpan[] times)
        {
            _ticks++;
            for (var i = 0; i < times.Length; i++)
            {
                _total[i] += times[i].TotalMilliseconds;
                _max[i] = Math.Max(_max[i], times[i].TotalMilliseconds);
            }
        }

        public override string ToString() =>
            $"{_ticks} ticks: " + string.Join(", ", _names.Select((n, i) =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{n} avg {(_ticks == 0 ? 0 : _total[i] / _ticks):0.0} max {_max[i]:0.0} ms")));
    }

    private static async Task FloodAsync(int port, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var drain = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            try
            {
                while (await stream.ReadAsync(buffer, token) > 0)
                {
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        });
        var noops = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("NOOP\r\n", 2000)));
        try
        {
            while (!token.IsCancellationRequested)
            {
                await stream.WriteAsync(noops, token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }

        client.Close();
        await drain;
    }

    /// <summary>Runs <paramref name="body"/> on a new STA thread with a running WPF dispatcher.</summary>
    private static void RunOnDispatcher(Func<Dispatcher, Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body(dispatcher);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The dispatcher thread did not finish.");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
