using System.Net;
using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App.Tests;

public sealed class BanIpTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modernftp-ban").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task BanIpBansTheAddressAndSavesItToTheConfigKeepingOtherSettings()
    {
        var store = new ConfigStore(Path.Combine(_directory, "config.json"));
        store.Save(new ModernFtpConfig { LoginTimeoutSeconds = 55, BannedAddresses = ["198.51.100.0/24"] });
        await using var server = new FtpServer(new FtpServerOptions());
        var controller = new EngineSessionController(() => server, store);

        Assert.True(controller.BanIp(IPAddress.Parse("203.0.113.9")));
        Assert.True(controller.BanIp(IPAddress.Parse("203.0.113.9")));

        Assert.True(server.Options.BanList.IsBanned(IPAddress.Parse("203.0.113.9")));
        var saved = store.Load();
        Assert.Equal(["198.51.100.0/24", "203.0.113.9"], saved.BannedAddresses);
        Assert.Equal(55, saved.LoginTimeoutSeconds);
    }

    [Fact]
    public void BanIpWithoutARunningServerDoesNothing()
    {
        var store = new ConfigStore(Path.Combine(_directory, "config.json"));
        var controller = new EngineSessionController(() => null, store);
        Assert.False(controller.BanIp(IPAddress.Parse("203.0.113.9")));
        Assert.False(File.Exists(store.Path));
        Assert.Empty(controller.Sessions);
    }
}
