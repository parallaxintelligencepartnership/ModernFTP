using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ModernFTP.Host.Console;

namespace ModernFTP.Engine.Tests;

public class EventHubTests
{
    [Fact]
    public async Task SessionKeepsAnsweringWhileNobodyReadsTheHub()
    {
        await using var server = await TestServer.StartAsync();
        using var gate = new ManualResetEventSlim(false);
        var notices = new ConcurrentQueue<string>();
        using var stalled = server.Server.Subscribe(e =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            if (e is ErrorEvent { SessionId: 0 } error)
            {
                notices.Enqueue(error.Message);
            }
        });

        try
        {
            await using var client = await server.ConnectAsync(login: false);

            // More events than the queue holds, pipelined: every NOOP must still be answered.
            const int count = EventHub.Capacity + 2_000;
            var flood = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("NOOP\r\n", count)));
            var writer = client.WriteBytesAsync(flood);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal(200, (await client.ReadReplyAsync()).Code);
            }

            await writer;
            await using var second = await server.ConnectAsync(login: false);
            Assert.Equal(200, (await second.SendAsync("NOOP")).Code);
            Assert.True(server.Server.DroppedEventCount > 0);
        }
        finally
        {
            gate.Set();
        }

        var deadline = Stopwatch.StartNew();
        while (notices.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
        }

        Assert.Contains(notices, m => m.Contains("dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConsoleLogNeverBlocksTheCaller()
    {
        using var gate = new ManualResetEventSlim(false);
        await using (var log = new ConsoleLog(new BlockingWriter(gate)))
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < ConsoleLog.Capacity * 2; i++)
            {
                log.WriteLine("line");
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"writing took {watch.Elapsed}");
            gate.Set();
        }
    }

    private sealed class BlockingWriter(ManualResetEventSlim gate) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => gate.Wait(TimeSpan.FromSeconds(30));
    }
}
