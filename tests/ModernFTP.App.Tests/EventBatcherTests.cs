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

            var lines = LogView.Lines(events, dropped);
            Assert.Equal("7000 events dropped", Assert.Single(lines[0]).Text);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The review's probe: one client that never logs in pipelines NOOP as fast as it can (about 100,000 events
    /// per second on a laptop). The window thread must keep serving input and memory must stay flat.
    /// </summary>
    [Fact]
    public void ANoopFloodFromAClientThatNeverLogsInKeepsTheWindowResponsive()
    {
        RunOnDispatcher(async dispatcher =>
        {
            var box = new RichTextBox { IsReadOnly = true };
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
            using var batcher = new EventBatcher(dispatcher, (events, dropped) => log.Append(LogView.Lines(events, dropped)));

            await using var server = new FtpServer(new FtpServerOptions { ListenAddress = IPAddress.Loopback, Port = 0, LoginTimeout = TimeSpan.FromMinutes(1) });
            await server.StartAsync();
            using var subscription = batcher.Attach(server);
            long published = 0;
            using var counter = server.Subscribe(_ => Interlocked.Increment(ref published));

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var flood = Task.Run(() => FloodAsync(server.LocalEndPoint!.Port, stop.Token));

            // Measure from a pool thread how long an input priority operation waits for the window thread.
            var probe = Task.Run(async () =>
            {
                var latencies = new List<double>();
                var memory = new List<long>();
                var watch = Stopwatch.StartNew();
                while (!stop.IsCancellationRequested)
                {
                    var sent = Stopwatch.GetTimestamp();
                    await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Input);
                    latencies.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                    if (memory.Count < watch.Elapsed.TotalSeconds)
                    {
                        memory.Add(GC.GetTotalMemory(forceFullCollection: false));
                    }

                    await Task.Delay(20);
                }

                return (latencies, memory);
            });

            var (latencies, samples) = await probe;
            await flood;
            latencies.Sort();
            var max = latencies[^1];
            var p95 = latencies[(int)(latencies.Count * 0.95)];
            var growth = samples.Count > 1 ? samples[^1] - samples[1] : 0;
            var summary = $"events published {Interlocked.Read(ref published)}, dropped in app {batcher.DroppedTotal}, " +
                $"input latency p95 {p95:0} ms max {max:0} ms over {latencies.Count} probes, managed memory growth after 1 s {growth / (1024 * 1024)} MB, " +
                $"log lines {log.LineCount}, pending {batcher.Pending}";
            output.WriteLine(summary);
            Console.WriteLine("Event flood probe: " + summary);

            Assert.True(Interlocked.Read(ref published) > 20_000, summary);
            Assert.True(batcher.Pending <= EventBatcher.Capacity, summary);
            Assert.True(log.LineCount <= LogView.MaxLines, summary);
            Assert.True(max < 1000, summary);
            Assert.True(growth < 64L * 1024 * 1024, summary);
            window.Close();
        });
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
