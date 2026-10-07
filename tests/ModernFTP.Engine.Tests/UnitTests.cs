using System.Net;

namespace ModernFTP.Engine.Tests;

public class BanListTests
{
    [Fact]
    public void MatchesAddressesAndCidrRanges()
    {
        var bans = new BanList();
        bans.Add("203.0.113.7");
        bans.Add("10.0.0.0/8");
        bans.Add("2001:db8::/32");
        Assert.True(bans.IsBanned(IPAddress.Parse("203.0.113.7")));
        Assert.False(bans.IsBanned(IPAddress.Parse("203.0.113.8")));
        Assert.True(bans.IsBanned(IPAddress.Parse("10.200.1.1")));
        Assert.True(bans.IsBanned(IPAddress.Parse("::ffff:10.1.2.3")));
        Assert.True(bans.IsBanned(IPAddress.Parse("2001:db8:1::5")));
        Assert.False(bans.IsBanned(IPAddress.Parse("2001:db9::1")));
        Assert.True(bans.Remove("10.0.0.0/8"));
        Assert.False(bans.IsBanned(IPAddress.Parse("10.200.1.1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    public void RejectsInvalidEntries(string entry)
    {
        Assert.False(BanList.IsValidEntry(entry));
        Assert.Throws<FormatException>(() => new BanList().Add(entry));
    }
}

public class CredentialTests
{
    [Fact]
    public void Pbkdf2RoundTrip()
    {
        var credential = Pbkdf2Credential.Create("correct horse", iterations: 1000);
        Assert.True(credential.Verify("correct horse"));
        Assert.False(credential.Verify("correct horsf"));
        var copy = new Pbkdf2Credential(credential.Hash, credential.Salt, credential.Iterations);
        Assert.True(copy.Verify("correct horse"));
    }

    [Fact]
    public void PlaintextCredentialComparesExactly()
    {
        var credential = new PlaintextCredential("pw");
        Assert.True(credential.Verify("pw"));
        Assert.False(credential.Verify("PW"));
    }
}

public class PermissionRulesTests
{
    [Fact]
    public void HomeRuleAppliesEverywhereInPhaseZero()
    {
        var rules = new PermissionRules(FtpPermissions.ReadOnly);
        Assert.True(VirtualPath.TryResolve(VirtualPath.Root, "/deep/sub/dir", out var path));
        Assert.Same(FtpPermissions.ReadOnly, rules.Resolve(path));
        Assert.Same(FtpPermissions.ReadOnly, rules.Resolve(VirtualPath.Root));
    }
}
