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
}
