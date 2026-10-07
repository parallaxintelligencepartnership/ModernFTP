using System.Diagnostics;
using System.Text;

namespace ModernFTP.Engine.Tests;

/// <summary>
/// One regression per public exploit against TYPSoft FTP Server. Each drives a real session over
/// loopback with the raw commands from the advisory and checks the server answers promptly and
/// stays usable afterwards.
/// </summary>
public class CveRegressionTests
{
    [Fact]
    public async Task Cve_2004_0325_DotDotSlashPathsDoNotSpinOrEscape()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        var watch = Stopwatch.StartNew();
        foreach (var path in new[] { "//../", "//../../../../", "/../../../../..", ".../...//", string.Concat(Enumerable.Repeat("//../", 400)) })
        {
            var cwd = await client.SendAsync($"CWD {path}");
            Assert.True(cwd.Code is 250 or 550, cwd.Text);
            Assert.Equal("257 \"/\" is the current directory.", (await client.SendAsync("PWD")).Text);
            Assert.Equal(550, (await client.SendAsync($"RETR {path}etc/passwd")).Code);
            Assert.Equal(550, (await client.SendAsync($"SIZE {path}")).Code);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task Cve_2005_3294_RetrFloodDoesNotCrash()
    {
        await using var server = await TestServer.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(server.Home, "f.txt"), "data");
        await using var client = await server.ConnectAsync();
        const int count = 300;
        var flood = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            flood.Append(i % 2 == 0 ? "RETR f.txt\r\n" : "RETR missing.txt\r\n");
        }

        await client.WriteAsync(flood.ToString());
        for (var i = 0; i < count; i++)
        {
            var reply = await client.ReadReplyAsync();
            Assert.True(reply.Code is 425 or 550, reply.Text);
        }

        // Repeated real downloads on the same session also work.
        for (var i = 0; i < 5; i++)
        {
            using var data = await client.OpenPassiveAsync();
            Assert.Equal(150, (await client.SendAsync("RETR f.txt")).Code);
            using var reader = new StreamReader(data.GetStream());
            Assert.Equal("data", await reader.ReadToEndAsync());
            Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        }

        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task Cve_2009_1668_AborWithoutTransferRepliesImmediately()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(226, (await client.SendAsync("ABOR")).Code);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");

        // ABOR with a data channel prepared but no transfer started also answers at once.
        await client.SendAsync("EPSV");
        Assert.Equal(226, (await client.SendAsync("ABOR")).Code);

        // ABOR with IAC IP / IAC DM telnet prefix (what real clients send).
        await client.WriteBytesAsync([0xFF, 0xF4, 0xFF, 0xF2, .. "ABOR\r\n"u8]);
        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task Cve_2009_1668_AborDuringTransferAbortsIt()
    {
        await using var server = await TestServer.StartAsync(downloadRateKBps: 16);
        await File.WriteAllBytesAsync(Path.Combine(server.Home, "slow.bin"), new byte[1024 * 1024]);
        await using var client = await server.ConnectAsync();
        using var data = await client.OpenPassiveAsync();
        Assert.Equal(150, (await client.SendAsync("RETR slow.bin")).Code);
        await Task.Delay(200);
        await client.WriteAsync("ABOR\r\n");
        Assert.Equal(426, (await client.ReadReplyAsync()).Code);
        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task Cve_2009_4105_AppeThenDeleDoesNotCrash()
    {
        await using var server = await TestServer.StartAsync();
        var target = Path.Combine(server.Home, "log.txt");
        await File.WriteAllTextAsync(target, "start ");
        await using var client = await server.ConnectAsync();

        // APPE with no data channel, then DELE.
        Assert.Equal(425, (await client.SendAsync("APPE log.txt")).Code);
        Assert.Equal(250, (await client.SendAsync("DELE log.txt")).Code);

        // APPE on a missing file creates it; DELE sent while the append is still streaming.
        using (var data = await client.OpenPassiveAsync())
        {
            Assert.Equal(150, (await client.SendAsync("APPE log.txt")).Code);
            await data.GetStream().WriteAsync(Encoding.ASCII.GetBytes("appended"));
            await client.WriteAsync("DELE log.txt\r\n");
            await Task.Delay(100);
            data.Close();
        }

        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        Assert.Equal(250, (await client.ReadReplyAsync()).Code);
        Assert.False(File.Exists(target));

        // APPE, ABOR, DELE on the same file.
        await File.WriteAllTextAsync(target, "again");
        using (var data = await client.OpenPassiveAsync())
        {
            Assert.Equal(150, (await client.SendAsync("APPE log.txt")).Code);
            await client.WriteAsync("ABOR\r\n");
            Assert.Equal(426, (await client.ReadReplyAsync()).Code);
            Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        }

        Assert.Equal(250, (await client.SendAsync("DELE log.txt")).Code);
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
    }

    [Fact]
    public async Task ExploitDb_18469_CwdNlstSizeLongArgumentsDoNotCrash()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        foreach (var verb in new[] { "CWD", "NLST", "SIZE" })
        {
            foreach (var argument in new[] { new string('A', 3000), string.Concat(Enumerable.Repeat("../", 1000)), new string('%', 3000) })
            {
                var reply = await client.SendAsync($"{verb} {argument}");
                Assert.True(reply.Code is 550 or 250, $"{verb}: {reply.Text}");
            }

            // Over the 4 KB line cap: rejected without buffering the whole line.
            Assert.Equal(500, (await client.SendAsync($"{verb} {new string('B', 100_000)}")).Code);
        }

        // A megabyte with no line break at all, then a normal command.
        await client.WriteAsync(new string('C', 1_000_000) + "\r\n");
        Assert.Equal(500, (await client.ReadReplyAsync()).Code);
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
        Assert.Equal("257 \"/\" is the current directory.", (await client.SendAsync("PWD")).Text);
    }
}
