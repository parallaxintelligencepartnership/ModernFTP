using ModernFTP.Config;

namespace ModernFTP.Engine.Tests;

public class ConfigTests
{
    [Fact]
    public void LoginLimitsHaveDefaultsAndReachTheEngine()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            var config = ConfigLoader.Parse("{}");
            Assert.Equal(30, config.LoginTimeoutSeconds);
            Assert.Equal(5, config.MaxUnauthenticatedPerIp);
            config.Tls.Enabled = false;
            var options = ConfigLoader.ToServerOptions(config, directory);
            Assert.Equal(TimeSpan.FromSeconds(30), options.LoginTimeout);
            Assert.Equal(5, options.MaxUnauthenticatedPerIp);

            var custom = ConfigLoader.Parse("""{ "loginTimeoutSeconds": 12, "maxUnauthenticatedPerIp": 0, "tls": { "enabled": false } }""");
            options = ConfigLoader.ToServerOptions(custom, directory);
            Assert.Equal(TimeSpan.FromSeconds(12), options.LoginTimeout);
            Assert.Equal(0, options.MaxUnauthenticatedPerIp);

            var invalid = ConfigLoader.Parse("""{ "loginTimeoutSeconds": -1, "maxUnauthenticatedPerIp": -1 }""");
            var errors = ConfigLoader.Validate(invalid, directory);
            Assert.Contains(errors, e => e.Contains("loginTimeoutSeconds", StringComparison.Ordinal));
            Assert.Contains(errors, e => e.Contains("connection limits", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisabledUserNeedsNoPasswordButEnabledUserDoes()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "home"));
            var config = ConfigLoader.Parse("""
                {
                  "tls": { "enabled": false },
                  "users": [
                    { "username": "imported", "enabled": false, "homeDirectory": "home" },
                    { "username": "active", "enabled": true, "homeDirectory": "home" }
                  ]
                }
                """);
            var errors = ConfigLoader.Validate(config, directory);
            Assert.Equal(["user 'active' has no password."], errors);

            config.Users.RemoveAt(1);
            Assert.Empty(ConfigLoader.Validate(config, directory));
            var user = Assert.Single(ConfigLoader.ToServerOptions(config, directory).Users);
            Assert.False(user.Enabled);
            Assert.False(user.Credential.Verify(string.Empty));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
