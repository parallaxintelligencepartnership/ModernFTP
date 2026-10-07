namespace ModernFTP.Engine.Tests;

public class ProtocolTests
{
    [Fact]
    public async Task UnknownCommandGets500AndPreLoginCommandGets530()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(login: false);
        Assert.Equal(500, (await client.SendAsync("XYZZY")).Code);
        Assert.Equal(530, (await client.SendAsync("PWD")).Code);
        Assert.Equal(530, (await client.SendAsync("LIST")).Code);
        Assert.Equal(215, (await client.SendAsync("SYST")).Code);
    }

    [Fact]
    public async Task WrongPasswordGets530AndThirdFailureCloses()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(login: false);
        for (var i = 0; i < 2; i++)
        {
            await client.SendAsync($"USER {TestServer.UserName}");
            Assert.Equal(530, (await client.SendAsync("PASS wrong")).Code);
        }

        await client.SendAsync($"USER {TestServer.UserName}");
        Assert.Equal(421, (await client.SendAsync("PASS wrong")).Code);
        Assert.Null(await client.ReadLineOrNullAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task BannerAndGoodbyeComeFromOptions()
    {
        await using var server = await TestServer.StartAsync(configure: o =>
        {
            o.WelcomeMessage = "Hello there";
            o.GoodbyeMessage = "See you";
            o.HideServerName = true;
        });
        await using var client = await FtpTestClient.ConnectAsync(server.Port);
        var banner = await client.ReadReplyAsync();
        Assert.Equal("220 Hello there", banner.Text);
        Assert.DoesNotContain("ModernFTP", banner.Text, StringComparison.Ordinal);
        Assert.Equal("221 See you", (await client.SendAsync("QUIT")).Text);
    }

    [Fact]
    public async Task FeatAdvertisesExtensionsAndPwdQuotes()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        var feat = await client.SendAsync("FEAT");
        Assert.Equal(211, feat.Code);
        Assert.Contains(" EPSV", feat.Text, StringComparison.Ordinal);
        Assert.Contains(" REST STREAM", feat.Text, StringComparison.Ordinal);
        Assert.Equal("257 \"/\" is the current directory.", (await client.SendAsync("PWD")).Text);
        Assert.Equal(200, (await client.SendAsync("OPTS UTF8 ON")).Code);
        Assert.Equal(431, (await client.SendAsync("AUTH TLS")).Code); // no certificate configured
    }

    [Fact]
    public async Task RestartedRetrSendsTheTail()
    {
        await using var server = await TestServer.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(server.Home, "f.txt"), "0123456789");
        await using var client = await server.ConnectAsync();
        Assert.Equal(350, (await client.SendAsync("REST 4")).Code);
        using var data = await client.OpenPassiveAsync();
        Assert.Equal(150, (await client.SendAsync("RETR f.txt")).Code);
        using var reader = new StreamReader(data.GetStream());
        Assert.Equal("456789", await reader.ReadToEndAsync());
        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
    }

    [Fact]
    public async Task HandlerFailureKeepsSessionAlive()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync();
        Directory.CreateDirectory(Path.Combine(server.Home, "full"));
        await File.WriteAllTextAsync(Path.Combine(server.Home, "full", "x"), "x");
        Assert.Equal(550, (await client.SendAsync("RMD full")).Code); // not empty
        Assert.Equal(503, (await client.SendAsync("RNTO nowhere")).Code);
        Assert.Equal(200, (await client.SendAsync("NOOP")).Code);
    }
}
