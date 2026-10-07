using ModernFTP.Config;

namespace ModernFTP.App.Tests;

public class SetupViewModelTests
{
    [Fact]
    public void SaveKeepsSettingsThatTheWindowDoesNotShow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "modernftp-app-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            ConfigLoader.Save(new ModernFtpConfig
            {
                LoginTimeoutSeconds = 77,
                MaxUnauthenticatedPerIp = 9,
                MaxConnectionsPerUser = 3,
                AllowActiveMode = false,
                BannedAddresses = ["192.0.2.0/24"],
            }, path);

            var model = new SetupViewModel(ConfigLoader.Load(path), new AppSettings());
            model.WelcomeMessage = "Hello";
            model.Port = "2121";
            Assert.Null(model.Save(path));

            var saved = ConfigLoader.Load(path);
            Assert.Equal(77, saved.LoginTimeoutSeconds);
            Assert.Equal(9, saved.MaxUnauthenticatedPerIp);
            Assert.Equal(3, saved.MaxConnectionsPerUser);
            Assert.False(saved.AllowActiveMode);
            Assert.Equal(["192.0.2.0/24"], saved.BannedAddresses);
            Assert.Equal("Hello", saved.WelcomeMessage);
            Assert.Equal(2121, saved.Port);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApplyReportsANonNumericField()
    {
        var model = new SetupViewModel(new ModernFtpConfig(), new AppSettings()) { Port = "abc" };
        Assert.NotNull(model.Save("unused.json"));
        Assert.Equal(nameof(SetupViewModel.Port), model.ErrorField);
    }
}
