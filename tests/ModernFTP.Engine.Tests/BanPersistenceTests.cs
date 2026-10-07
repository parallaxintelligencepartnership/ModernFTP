using System.Net;
using ModernFTP.Config;

namespace ModernFTP.Engine.Tests;

public class BanPersistenceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void BanListRaisesChangedOnlyWhenTheListChanges()
    {
        var bans = new BanList();
        var changes = 0;
        bans.Changed += (_, _) => changes++;
        bans.Add("203.0.113.7");
        bans.Add("203.0.113.7");
        Assert.False(bans.Remove("198.51.100.1"));
        Assert.True(bans.Remove("203.0.113.7"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void PersistedBansKeepTheRestOfTheConfigAndLoadBack()
    {
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, """{ "port": 2121, "welcomeMessage": "hi", "bannedAddresses": ["198.51.100.9"], "tls": { "enabled": false } }""");
        var store = new ConfigStore(path);
        var options = ConfigLoader.ToServerOptions(store.Load(), _directory);
        var errors = new List<Exception>();
        using (store.PersistBans(options.BanList, errors.Add))
        {
            options.BanList.Add("203.0.113.7");
            options.BanList.Add("10.0.0.0/8");
            options.BanList.Add("2001:db8::1");
            options.BanList.Remove("198.51.100.9");
        }

        options.BanList.Add("192.0.2.1");
        Assert.Empty(errors);
        var saved = store.Load();
        Assert.Equal(["203.0.113.7", "10.0.0.0/8", "2001:db8::1"], saved.BannedAddresses);
        Assert.Equal(2121, saved.Port);
        Assert.Equal("hi", saved.WelcomeMessage);
        Assert.True(ConfigLoader.ToServerOptions(saved, _directory).BanList.IsBanned(IPAddress.Parse("10.1.2.3")));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SaveWritesTheWholeConfig()
    {
        var path = Path.Combine(_directory, "sub", "config.json");
        var store = new ConfigStore(path);
        store.Save(new ModernFtpConfig { Port = 2222, BannedAddresses = ["203.0.113.7"] });
        var loaded = store.Load();
        Assert.Equal(2222, loaded.Port);
        Assert.Equal(["203.0.113.7"], loaded.BannedAddresses);
    }

    [Fact]
    public void PerUserLimitsReachTheEngineAndAreValidated()
    {
        var config = ConfigLoader.Parse("""
            { "tls": { "enabled": false }, "users": [
              { "username": "a", "password": "x", "homeDirectory": ".", "maxConnections": 2, "maxConnectionsPerIp": 1, "idleTimeoutSeconds": 90 },
              { "username": "b", "password": "y", "homeDirectory": "." } ], "allowPlaintextPasswords": true }
            """);
        var options = ConfigLoader.ToServerOptions(config, _directory);
        var a = options.Users.Single(u => u.UserName == "a");
        Assert.Equal(2, a.MaxConnections);
        Assert.Equal(1, a.MaxConnectionsPerIp);
        Assert.Equal(TimeSpan.FromSeconds(90), a.IdleTimeout);
        var b = options.Users.Single(u => u.UserName == "b");
        Assert.Null(b.MaxConnections);
        Assert.Null(b.MaxConnectionsPerIp);
        Assert.Null(b.IdleTimeout);

        config.Users[0].MaxConnections = -1;
        config.Users[0].IdleTimeoutSeconds = 40000;
        var errors = ConfigLoader.Validate(config, _directory);
        Assert.Contains(errors, e => e.Contains("maxConnections", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("idleTimeoutSeconds", StringComparison.Ordinal));
    }
}
