using ModernFTP.Config;

namespace ModernFTP.Engine.Tests;

public class DirectoryRuleTests
{
    [Fact]
    public void WithoutDirectoriesTheLegacyHomeAndPermissionsApply()
    {
        var user = new UserConfig { HomeDirectory = "/legacy", Permissions = new PermissionsConfig { Upload = true } };
        var (path, permissions) = user.EffectiveHome();
        Assert.Equal("/legacy", path);
        Assert.True(permissions.Upload);
    }

    [Fact]
    public void HomeEntryWinsOverLegacyFields()
    {
        var user = new UserConfig
        {
            HomeDirectory = "/legacy",
            Directories =
            [
                new DirectoryConfig { Path = "/link", Alias = "link" },
                new DirectoryConfig { Path = "/home", Permissions = new PermissionsConfig { Delete = true } },
            ],
        };
        var (path, permissions) = user.EffectiveHome();
        Assert.Equal("/home", path);
        Assert.True(permissions.Delete);
    }

    [Fact]
    public void FirstEntryIsHomeWhenNoneIsUnaliased()
    {
        var user = new UserConfig { Directories = [new DirectoryConfig { Path = "/a", Alias = "a" }, new DirectoryConfig { Path = "/b", Alias = "b" }] };
        Assert.Equal("/a", user.EffectiveHome().Path);
    }

    [Fact]
    public void ServerOptionsUseTheHomeEntry()
    {
        var dir = Directory.CreateTempSubdirectory("mftp-home").FullName;
        try
        {
            var config = new ModernFtpConfig
            {
                AllowPlaintextPasswords = true,
                Tls = new TlsConfig { Enabled = false },
                Users =
                [
                    new UserConfig
                    {
                        Username = "u",
                        Password = "p",
                        HomeDirectory = "/does/not/exist",
                        Directories = [new DirectoryConfig { Path = dir, Permissions = new PermissionsConfig { Upload = true } }],
                    },
                ],
            };
            Assert.Empty(ConfigLoader.Validate(config, dir));
            var user = ConfigLoader.ToServerOptions(config, dir).Users.Single();
            Assert.Equal(dir, user.HomeDirectory);
            Assert.True(user.Permissions.Home.Upload);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
