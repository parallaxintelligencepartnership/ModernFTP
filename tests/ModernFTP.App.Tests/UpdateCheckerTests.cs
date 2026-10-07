using ModernFTP.App;

namespace ModernFTP.App.Tests;

public sealed class UpdateCheckerTests
{
    [Fact]
    public void NewerTagIsAvailableAndStripsTheV()
    {
        var result = UpdateChecker.Evaluate("""{ "tag_name": "v0.2.0", "html_url": "https://example.test/r" }""", "0.1.0");
        Assert.Equal(UpdateState.Available, result.State);
        Assert.Equal("0.2.0", result.Version);
        Assert.Equal("https://example.test/r", result.Url);
    }

    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("v0.1.0", "0.2.0")]
    [InlineData("v0.1.0", "0.2.0+abc123")]
    public void SameOrOlderIsUpToDate(string tag, string current)
    {
        var result = UpdateChecker.Evaluate($$"""{ "tag_name": "{{tag}}" }""", current);
        Assert.Equal(UpdateState.UpToDate, result.State);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{ "tag_name": "nightly" }""")]
    public void UnreadableReleaseFails(string json)
    {
        Assert.Equal(UpdateState.Failed, UpdateChecker.Evaluate(json, "0.1.0").State);
    }

    [Fact]
    public void FirewallArgumentsAreQuoted()
    {
        Assert.Equal(
            "firewall add --config \"C:\\a b\\config.json\" --program \"C:\\x\\ModernFTP.exe\"",
            FirewallHelper.BuildArguments(@"C:\a b\config.json", @"C:\x\ModernFTP.exe"));
    }
}
