using ModernFTP.Config;
using ModernFTP.Config.Import;
using ModernFTP.Host.Console;

namespace ModernFTP.Engine.Tests;

public class TypsoftImporterTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("mftp-import").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string Source(bool withUsers = true)
    {
        var source = Path.Combine(_work, "src");
        Directory.CreateDirectory(source);
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "typsoft");
        File.Copy(Path.Combine(fixtures, "config.ini"), Path.Combine(source, "config.ini"), overwrite: true);
        if (withUsers)
        {
            File.Copy(Path.Combine(fixtures, "users.ini"), Path.Combine(source, "users.ini"), overwrite: true);
        }

        return source;
    }

    [Fact]
    public void GlobalSettingsAreMapped()
    {
        var result = TypsoftImporter.Import(Source());
        var c = result.Config;
        Assert.Equal(2121, c.Port);
        Assert.Equal(25, c.MaxConnections);
        Assert.Equal(300, c.IdleTimeoutSeconds);
        Assert.Equal(50000, c.PassivePortMin);
        Assert.Equal(50050, c.PassivePortMax);
        Assert.Equal("203.0.113.7", c.PassivePublicAddress);
        Assert.True(c.HideServerName);
        Assert.Contains("198.51.100.9", c.BannedAddresses);
        Assert.Contains(result.Warnings, w => w.Contains("Setup Log", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("TrayIcon", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("[Color]", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalUserWithTwoDirectories()
    {
        var alice = TypsoftImporter.Import(Source()).Config.Users.Single(u => u.Username == "alice");
        Assert.False(alice.Enabled);
        Assert.Null(alice.PasswordHash);
        Assert.Null(alice.Password);
        Assert.Equal(2, alice.Directories.Count);
        var home = alice.Directories[0];
        Assert.Equal(@"C:\FTP\alice\", home.Path);
        Assert.Null(home.Alias);
        Assert.True(home.IncludeSubdirectories);
        var p = home.Permissions;
        Assert.True(p.Download && p.Upload && p.Delete && p.MakeDir && p.RemoveDir && p.Rename && p.List);
        var shared = alice.Directories[1];
        Assert.True(shared.Permissions.Download);
        Assert.False(shared.Permissions.Upload || shared.Permissions.Delete || shared.Permissions.Rename);
        Assert.Equal(alice.Directories[0].Path, alice.EffectiveHome().Path);
    }

    [Fact]
    public void PerUserLimitsAndTimeOutAreMapped()
    {
        var result = TypsoftImporter.Import(Source());
        var alice = result.Config.Users.Single(u => u.Username == "alice");
        Assert.Equal(3, alice.MaxConnections);
        Assert.Equal(2, alice.MaxConnectionsPerIp);
        Assert.Equal(600, alice.IdleTimeoutSeconds);
        var bob = result.Config.Users.Single(u => u.Username == "bob");
        Assert.Null(bob.MaxConnections);
        Assert.Null(bob.MaxConnectionsPerIp);
        Assert.Null(bob.IdleTimeoutSeconds);
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("User alice: ", StringComparison.Ordinal) && w.Contains("no ModernFTP equivalent", StringComparison.Ordinal));
    }

    [Fact]
    public void NoAccessEntryClearsEveryRight()
    {
        var bob = TypsoftImporter.Import(Source()).Config.Users.Single(u => u.Username == "bob");
        var secret = bob.Directories.Single(d => d.Path.Contains("secret", StringComparison.Ordinal));
        var p = secret.Permissions;
        Assert.False(p.Download || p.Upload || p.Delete || p.MakeDir || p.RemoveDir || p.Rename || p.List);
    }

    [Fact]
    public void VirtualLinkBecomesAlias()
    {
        var result = TypsoftImporter.Import(Source());
        var bob = result.Config.Users.Single(u => u.Username == "bob");
        var link = bob.Directories.Single(d => d.Alias == "Public");
        Assert.Equal(@"C:\FTP\public\", link.Path);
        Assert.Equal(@"C:\FTP\bob\", bob.EffectiveHome().Path);
        Assert.Contains(result.Warnings, w => w.Contains("bob", StringComparison.Ordinal) && w.Contains("Public", StringComparison.Ordinal));
    }

    [Fact]
    public void PasswordsAreNotMigratedAndWarned()
    {
        var result = TypsoftImporter.Import(Source());
        Assert.Contains("User alice: set a password and enable the account", result.Warnings);
        Assert.Contains("User bob: set a password and enable the account", result.Warnings);
        Assert.All(result.Config.Users.Where(u => u.Username != "Anonymous"), u => Assert.False(u.Enabled));
    }

    [Fact]
    public void AnonymousOnIsImportedEnabledWithWarning()
    {
        var result = TypsoftImporter.Import(Source());
        Assert.True(result.Config.AllowAnonymous);
        Assert.True(result.Config.Users.Single(u => u.Username == "Anonymous").Enabled);
        Assert.Contains(result.Warnings, w => w.Contains("ships with anonymous off", StringComparison.Ordinal));
    }

    [Fact]
    public void BadLineReportsFileAndLine()
    {
        var source = Source();
        File.AppendAllText(Path.Combine(source, "users.ini"), "this is not valid\n");
        var ex = Assert.Throws<InvalidDataException>(() => TypsoftImporter.Import(source));
        Assert.Contains("users.ini line 20", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandWritesConfigAndReportsSummary()
    {
        var target = Path.Combine(_work, "out", "config.json");
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, ImportCommand.Run(Source(), target, force: false, output, error));
        Assert.True(File.Exists(target));
        Assert.Equal(3, ConfigLoader.Load(target).Users.Count);
        Assert.Contains("Imported 3 users, 6 directories", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CommandWithMissingUsersIniExitsTwo()
    {
        var error = new StringWriter();
        var target = Path.Combine(_work, "config.json");
        Assert.Equal(2, ImportCommand.Run(Source(withUsers: false), target, force: false, new StringWriter(), error));
        Assert.Contains("users.ini", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void CommandWithParseFailureExitsTwo()
    {
        var source = Source();
        File.AppendAllText(Path.Combine(source, "config.ini"), "garbage\n");
        var error = new StringWriter();
        Assert.Equal(2, ImportCommand.Run(source, Path.Combine(_work, "c.json"), force: false, new StringWriter(), error));
        Assert.Contains("config.ini line", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CommandRefusesToOverwriteWithoutForce()
    {
        var target = Path.Combine(_work, "config.json");
        File.WriteAllText(target, "keep");
        var error = new StringWriter();
        Assert.NotEqual(0, ImportCommand.Run(Source(), target, force: false, new StringWriter(), error));
        Assert.Equal("keep", File.ReadAllText(target));
        Assert.Equal(0, ImportCommand.Run(Source(), target, force: true, new StringWriter(), error));
        Assert.NotEqual("keep", File.ReadAllText(target));
    }
}
