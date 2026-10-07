using ModernFTP.Host.Console;

namespace ModernFTP.Engine.Tests;

public sealed class SystemCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "modernftp-syscmd-" + Guid.NewGuid().ToString("N"));

    public SystemCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<(string File, string Args)> Calls { get; } = [];

        public int ExitCode { get; set; }

        public int Run(string fileName, string arguments, out string output)
        {
            Calls.Add((fileName, arguments));
            output = string.Empty;
            return ExitCode;
        }
    }

    private sealed class FakeElevation(bool elevated) : IElevationCheck
    {
        public bool IsElevated() => elevated;
    }

    private static (int Code, string Out, string Err) Run(string[] args, FakeLauncher launcher, bool elevated = true, bool windows = true, string dir = @"C:\ModernFTP")
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = SystemCommands.Run(args, launcher, new FakeElevation(elevated), windows, dir, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private string WriteConfig(int port, int min, int max)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, $$"""{ "port": {{port}}, "passivePortMin": {{min}}, "passivePortMax": {{max}} }""");
        return path;
    }

    [Fact]
    public void ParsesServiceInstallWithConfig()
    {
        Assert.True(SystemCommands.TryParse(["service", "install", "--config", "x.json"], out var c, out _));
        Assert.Equal(new SystemCommand("service", "install", "x.json", null), c);
    }

    [Theory]
    [InlineData("service", "bogus")]
    [InlineData("firewall", "install")]
    [InlineData("service", "start", "--config", "x.json")]
    [InlineData("firewall", "remove", "--program", "a.exe")]
    [InlineData("service", "install", "--nope")]
    [InlineData("service")]
    public void RejectsBadArguments(params string[] args)
    {
        Assert.False(SystemCommands.TryParse(args, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ScCreateArgumentsAreExact()
    {
        Assert.Equal(
            "create ModernFTP binPath= \"\\\"C:\\ModernFTP\\modernftp-service.exe\\\"\" start= auto obj= \"NT AUTHORITY\\NetworkService\" DisplayName= \"ModernFTP\"",
            SystemCommands.ScCreateArguments(@"C:\ModernFTP\modernftp-service.exe", null));
        Assert.Equal(
            "create ModernFTP binPath= \"\\\"C:\\ModernFTP\\modernftp-service.exe\\\" --config \\\"D:\\cfg\\config.json\\\"\" start= auto obj= \"NT AUTHORITY\\NetworkService\" DisplayName= \"ModernFTP\"",
            SystemCommands.ScCreateArguments(@"C:\ModernFTP\modernftp-service.exe", @"D:\cfg\config.json"));
    }

    [Fact]
    public void InstallRunsCreateThenDescription()
    {
        var launcher = new FakeLauncher();
        var (code, _, _) = Run(["service", "install"], launcher, dir: _dir);
        Assert.Equal(0, code);
        Assert.Equal(2, launcher.Calls.Count);
        Assert.All(launcher.Calls, c => Assert.Equal("sc.exe", c.File));
        Assert.StartsWith("create ModernFTP binPath= ", launcher.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_dir, "modernftp-service.exe"), launcher.Calls[0].Args, StringComparison.Ordinal);
        Assert.StartsWith("description ModernFTP \"", launcher.Calls[1].Args, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("uninstall", "delete ModernFTP")]
    [InlineData("start", "start ModernFTP")]
    [InlineData("stop", "stop ModernFTP")]
    [InlineData("status", "query ModernFTP")]
    public void ServiceActionsMapToSc(string action, string expected)
    {
        var launcher = new FakeLauncher();
        var (code, _, _) = Run(["service", action], launcher);
        Assert.Equal(0, code);
        Assert.Equal([("sc.exe", expected)], launcher.Calls);
    }

    [Fact]
    public void FirewallAddRunsControlAndPassiveRules()
    {
        var config = WriteConfig(2121, 50000, 50999);
        var launcher = new FakeLauncher();
        var (code, _, _) = Run(["firewall", "add", "--config", config], launcher, dir: @"C:\ModernFTP");
        Assert.Equal(0, code);
        var program = Path.GetFullPath(Path.Combine(@"C:\ModernFTP", "modernftp-service.exe"));
        Assert.Equal(
            [
                ("netsh.exe", $"advfirewall firewall add rule name=\"ModernFTP control\" dir=in action=allow protocol=TCP localport=2121 program=\"{program}\" enable=yes"),
                ("netsh.exe", "advfirewall firewall add rule name=\"ModernFTP passive\" dir=in action=allow protocol=TCP localport=50000-50999 enable=yes"),
            ],
            launcher.Calls);
    }

    [Fact]
    public void FirewallAddUsesProgramOverride()
    {
        var config = WriteConfig(21, 50000, 50010);
        var launcher = new FakeLauncher();
        var program = Path.Combine(_dir, "ModernFTP.exe");
        Run(["firewall", "add", "--config", config, "--program", program], launcher);
        Assert.Contains($"program=\"{program}\"", launcher.Calls[0].Args, StringComparison.Ordinal);
    }

    [Fact]
    public void FirewallRemoveDeletesBothRules()
    {
        var launcher = new FakeLauncher();
        var (code, _, _) = Run(["firewall", "remove"], launcher);
        Assert.Equal(0, code);
        Assert.Equal(
            [
                ("netsh.exe", "advfirewall firewall delete rule name=\"ModernFTP control\""),
                ("netsh.exe", "advfirewall firewall delete rule name=\"ModernFTP passive\""),
            ],
            launcher.Calls);
    }

    [Fact]
    public void NotElevatedExitsFourWithoutRunningAnything()
    {
        var launcher = new FakeLauncher();
        var (code, _, err) = Run(["service", "start"], launcher, elevated: false);
        Assert.Equal(4, code);
        Assert.Contains("Run this from an elevated prompt", err, StringComparison.Ordinal);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void NonWindowsExitsFourWithoutRunningAnything()
    {
        var launcher = new FakeLauncher();
        var (code, _, err) = Run(["firewall", "remove"], launcher, windows: false);
        Assert.Equal(4, code);
        Assert.Contains("Windows only", err, StringComparison.Ordinal);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void FailedCommandReturnsTwo()
    {
        var launcher = new FakeLauncher { ExitCode = 1060 };
        var (code, _, _) = Run(["service", "stop"], launcher);
        Assert.Equal(2, code);
    }
}
