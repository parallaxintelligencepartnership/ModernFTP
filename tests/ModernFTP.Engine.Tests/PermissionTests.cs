namespace ModernFTP.Engine.Tests;

public class PermissionTests
{
    [Fact]
    public async Task ReadOnlyUserIsDeniedEveryWrite()
    {
        await using var server = await TestServer.StartAsync(FtpPermissions.ReadOnly);
        await File.WriteAllTextAsync(Path.Combine(server.Home, "keep.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(server.Home, "dir"));
        await using var client = await server.ConnectAsync();

        Assert.Equal(550, (await client.SendAsync("STOR new.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("APPE keep.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("DELE keep.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("MKD newdir")).Code);
        Assert.Equal(550, (await client.SendAsync("RMD dir")).Code);
        Assert.Equal(550, (await client.SendAsync("RNFR keep.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("RNFR dir")).Code);
        Assert.True(File.Exists(Path.Combine(server.Home, "keep.txt")));
        Assert.True(Directory.Exists(Path.Combine(server.Home, "dir")));
        Assert.False(File.Exists(Path.Combine(server.Home, "new.txt")));
    }

    [Fact]
    public async Task DownloadAndListFlagsAreEnforced()
    {
        await using var server = await TestServer.StartAsync(FtpPermissions.None);
        await File.WriteAllTextAsync(Path.Combine(server.Home, "f.txt"), "x");
        await using var client = await server.ConnectAsync();
        Assert.Equal(550, (await client.SendAsync("RETR f.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("LIST")).Code);
        Assert.Equal(550, (await client.SendAsync("NLST")).Code);
        Assert.Equal(550, (await client.SendAsync("MLSD")).Code);
        Assert.Equal(550, (await client.SendAsync("MLST f.txt")).Code);
        Assert.Equal(550, (await client.SendAsync("SIZE f.txt")).Code);
    }

    [Fact]
    public async Task OverwriteNeedsDeleteButNewUploadDoesNot()
    {
        var uploadOnly = FtpPermissions.None with { Upload = true, List = true };
        await using var server = await TestServer.StartAsync(uploadOnly);
        await File.WriteAllTextAsync(Path.Combine(server.Home, "exists.txt"), "x");
        await using var client = await server.ConnectAsync();
        Assert.Equal(550, (await client.SendAsync("STOR exists.txt")).Code);
        using var data = await client.OpenPassiveAsync();
        Assert.Equal(150, (await client.SendAsync("STOR fresh.txt")).Code);
        data.Close();
        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        Assert.True(File.Exists(Path.Combine(server.Home, "fresh.txt")));
    }

    [Fact]
    public async Task RestThenStorOnExistingFileNeedsDelete()
    {
        var uploadOnly = FtpPermissions.None with { Upload = true, Download = true, List = true };
        await using var server = await TestServer.StartAsync(uploadOnly);
        var target = Path.Combine(server.Home, "precious.txt");
        await File.WriteAllTextAsync(target, new string('P', 10_000));
        await using var client = await server.ConnectAsync();
        using var data = await client.OpenPassiveAsync();
        Assert.Equal(350, (await client.SendAsync("REST 1")).Code);
        Assert.Equal(550, (await client.SendAsync("STOR precious.txt")).Code);
        Assert.Equal(10_000, new FileInfo(target).Length);
    }

    [Fact]
    public async Task RestBeyondEndOfFileGets554AndLeavesFilesAlone()
    {
        await using var server = await TestServer.StartAsync(FtpPermissions.All);
        var target = Path.Combine(server.Home, "short.txt");
        await File.WriteAllTextAsync(target, "0123456789");
        await using var client = await server.ConnectAsync();
        Assert.Equal(350, (await client.SendAsync("REST 11")).Code);
        Assert.Equal(554, (await client.SendAsync("STOR short.txt")).Code);
        Assert.Equal("0123456789", await File.ReadAllTextAsync(target));
        Assert.Equal(350, (await client.SendAsync("REST 5")).Code);
        Assert.Equal(554, (await client.SendAsync("STOR missing.txt")).Code);
        Assert.False(File.Exists(Path.Combine(server.Home, "missing.txt")));

        // A resume inside the file still works for a user with Delete.
        using (var data = await client.OpenPassiveAsync())
        {
            Assert.Equal(350, (await client.SendAsync("REST 3")).Code);
            Assert.Equal(150, (await client.SendAsync("STOR short.txt")).Code);
            await data.GetStream().WriteAsync("abc"u8.ToArray());
        }

        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        Assert.Equal("012abc", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task AppeIgnoresRestAndAppendsWithoutDelete()
    {
        var uploadOnly = FtpPermissions.None with { Upload = true, List = true };
        await using var server = await TestServer.StartAsync(uploadOnly);
        var target = Path.Combine(server.Home, "log.txt");
        await File.WriteAllTextAsync(target, "0123456789");
        await using var client = await server.ConnectAsync();
        Assert.Equal(350, (await client.SendAsync("REST 2")).Code);
        using (var data = await client.OpenPassiveAsync())
        {
            Assert.Equal(150, (await client.SendAsync("APPE log.txt")).Code);
            await data.GetStream().WriteAsync("abc"u8.ToArray());
        }

        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        Assert.Equal("0123456789abc", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task FullUserCanManageFilesAndDirectories()
    {
        await using var server = await TestServer.StartAsync(FtpPermissions.All);
        await File.WriteAllTextAsync(Path.Combine(server.Home, "a.txt"), "x");
        await using var client = await server.ConnectAsync();
        Assert.Equal(257, (await client.SendAsync("MKD sub")).Code);
        Assert.Equal(350, (await client.SendAsync("RNFR a.txt")).Code);
        Assert.Equal(250, (await client.SendAsync("RNTO sub/b.txt")).Code);
        Assert.Equal(213, (await client.SendAsync("SIZE /sub/b.txt")).Code);
        Assert.Equal(250, (await client.SendAsync("CWD sub")).Code);
        Assert.Equal(250, (await client.SendAsync("DELE b.txt")).Code);
        Assert.Equal(250, (await client.SendAsync("CDUP")).Code);
        Assert.Equal(250, (await client.SendAsync("RMD sub")).Code);
        Assert.False(Directory.Exists(Path.Combine(server.Home, "sub")));
    }

    [Fact]
    public async Task RemoveDirNeedsRemoveDirNotDelete()
    {
        var deleteOnly = FtpPermissions.None with { Delete = true, List = true };
        await using (var server = await TestServer.StartAsync(deleteOnly))
        {
            Directory.CreateDirectory(Path.Combine(server.Home, "d"));
            await using var client = await server.ConnectAsync();
            Assert.Equal(550, (await client.SendAsync("RMD d")).Code);
            Assert.True(Directory.Exists(Path.Combine(server.Home, "d")));
        }

        var removeOnly = FtpPermissions.None with { RemoveDir = true, List = true };
        await using (var server = await TestServer.StartAsync(removeOnly))
        {
            Directory.CreateDirectory(Path.Combine(server.Home, "d"));
            await File.WriteAllTextAsync(Path.Combine(server.Home, "f.txt"), "x");
            await using var client = await server.ConnectAsync();
            Assert.Equal(550, (await client.SendAsync("DELE f.txt")).Code);
            Assert.Equal(250, (await client.SendAsync("RMD d")).Code);
        }
    }

    [Fact]
    public async Task RenameFlagCoversFilesAndDirectories()
    {
        var renameOnly = FtpPermissions.None with { Rename = true, List = true };
        await using var server = await TestServer.StartAsync(renameOnly);
        await File.WriteAllTextAsync(Path.Combine(server.Home, "a.txt"), "x");
        Directory.CreateDirectory(Path.Combine(server.Home, "d"));
        await using var client = await server.ConnectAsync();
        Assert.Equal(350, (await client.SendAsync("RNFR a.txt")).Code);
        Assert.Equal(250, (await client.SendAsync("RNTO b.txt")).Code);
        Assert.Equal(350, (await client.SendAsync("RNFR d")).Code);
        Assert.Equal(250, (await client.SendAsync("RNTO e")).Code);
    }
}
