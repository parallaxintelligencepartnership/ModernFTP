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
    public void AnsiFilesFromTheOriginalAreReadAsCodePage1252WithAWarning()
    {
        // The original is a Delphi program that wrote its INI and message files in the Windows ANSI code page.
        var source = Path.Combine(_work, "ansi");
        Directory.CreateDirectory(source);
        var ansi = System.Text.Encoding.Latin1;
        File.WriteAllBytes(Path.Combine(source, "config.ini"), ansi.GetBytes("; Serveur de l\u0027\u00e9quipe\r\n[Setup]\r\nPort=21\r\nEnterMessage=welcome.txt\r\n"));
        File.WriteAllBytes(Path.Combine(source, "welcome.txt"), ansi.GetBytes("Bienvenue \u00e0 tous, d\u00e9j\u00e0 pr\u00eat."));
        File.WriteAllBytes(
            Path.Combine(source, "users.ini"),
            ansi.GetBytes("[Andr\u00e9]\r\nHomePath=C:\\Donn\u00e9es\\\r\nDir0=C:\\Donn\u00e9es\\|DU_______S_|\r\n"));

        var result = TypsoftImporter.Import(source);

        var user = Assert.Single(result.Config.Users);
        Assert.Equal("Andr\u00e9", user.Username);
        Assert.Equal("C:\\Donn\u00e9es\\", user.EffectiveHome().Path);
        Assert.Equal("Bienvenue \u00e0 tous, d\u00e9j\u00e0 pr\u00eat.", result.Config.WelcomeMessage);
        Assert.Contains(result.Warnings, w => w.StartsWith("users.ini is not UTF-8", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("config.ini is not UTF-8", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("welcome.txt is not UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    public void Utf8FilesAreReadAsUtf8WithoutAnEncodingWarning()
    {
        var source = Path.Combine(_work, "utf8");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "config.ini"), "[Setup]\r\nPort=21\r\n");
        File.WriteAllText(Path.Combine(source, "users.ini"), "[Andr\u00e9]\r\nHomePath=C:\\Donn\u00e9es\\\r\nDir0=C:\\Donn\u00e9es\\|DU_______S_|\r\n");

        var result = TypsoftImporter.Import(source);

        Assert.Equal("Andr\u00e9", Assert.Single(result.Config.Users).Username);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("UTF-8", StringComparison.Ordinal));
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
