namespace ModernFTP.Engine.Tests;

public class SymlinkTests
{
    [Fact]
    public async Task LinksThatLeaveTheHomeAreRefusedWith550()
    {
        await using var server = await TestServer.StartAsync();
        var outside = Directory.CreateDirectory(Path.Combine(server.Root, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "SECRET");
        Directory.CreateSymbolicLink(Path.Combine(server.Home, "out"), outside);
        File.CreateSymbolicLink(Path.Combine(server.Home, "secret-link.txt"), Path.Combine(outside, "secret.txt"));
        await using var client = await server.ConnectAsync();

        Assert.Equal(550, (await client.SendAsync("CWD out")).Code);
        Assert.Equal(550, (await client.SendAsync("SIZE out/secret.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("SIZE secret-link.txt")).Code);
        using var data = await client.OpenPassiveAsync();
        Assert.Equal(550, (await client.SendAsync("RETR out/secret.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("RETR secret-link.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("STOR out/planted.txt")).Code);
        Assert.False(File.Exists(Path.Combine(outside, "planted.txt")));
    }

    [Fact]
    public async Task LinksThatStayInsideTheHomeKeepWorking()
    {
        await using var server = await TestServer.StartAsync();
        var sub = Directory.CreateDirectory(Path.Combine(server.Home, "sub")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sub, "file.txt"), "INSIDE");
        Directory.CreateSymbolicLink(Path.Combine(server.Home, "shortcut"), sub);
        File.CreateSymbolicLink(Path.Combine(server.Home, "file-link.txt"), Path.Combine(sub, "file.txt"));
        await using var client = await server.ConnectAsync();

        Assert.Equal(250, (await client.SendAsync("CWD shortcut")).Code);
        Assert.Equal(250, (await client.SendAsync("CWD /")).Code);
        Assert.Equal(213, (await client.SendAsync("SIZE file-link.txt")).Code);
        foreach (var name in new[] { "shortcut/file.txt", "file-link.txt" })
        {
            using var data = await client.OpenPassiveAsync();
            Assert.Equal(150, (await client.SendAsync($"RETR {name}")).Code);
            using var reader = new StreamReader(data.GetStream());
            Assert.Equal("INSIDE", await reader.ReadToEndAsync());
            Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        }
    }
}
