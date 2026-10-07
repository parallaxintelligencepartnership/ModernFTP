using System.Diagnostics;

namespace ModernFTP.Engine.Tests;

public class VirtualPathTests
{
    private static VirtualPath Resolve(string current, string input)
    {
        Assert.True(VirtualPath.TryResolve(VirtualPath.Root, current, out var cwd));
        Assert.True(VirtualPath.TryResolve(cwd, input, out var result), $"'{input}' should resolve");
        return result;
    }

    [Theory]
    [InlineData("/", "a/b", "/a/b")]
    [InlineData("/a/b", "../c", "/a/c")]
    [InlineData("/a", "..", "/")]
    [InlineData("/", "../../..", "/")]
    [InlineData("/", "//../", "/")]
    [InlineData("/x", "//..//..//../etc/passwd", "/etc/passwd")]
    [InlineData("/a", "./b/./c/", "/a/b/c")]
    [InlineData("/a", "/", "/")]
    [InlineData("/a", "", "/a")]
    [InlineData("/", "caf\u00e9 menu.txt", "/caf\u00e9 menu.txt")]
    public void Normalizes(string current, string input, string expected) =>
        Assert.Equal(expected, Resolve(current, input).ToString());

    [Theory]
    [InlineData("a\\b")]
    [InlineData("..\\..\\windows")]
    [InlineData("C:/Windows")]
    [InlineData("c:")]
    [InlineData("file.txt:stream")]
    [InlineData("a\0b")]
    [InlineData("a\r\nDELE b")]
    [InlineData("tab\there")]
    [InlineData("\u0085next")]
    [InlineData("con")]
    [InlineData("dir/NUL.txt")]
    [InlineData("com1")]
    [InlineData("...")]
    [InlineData("trailingdot.")]
    [InlineData("trailingspace ")]
    [InlineData("*.txt")]
    [InlineData("a?b")]
    [InlineData("a|b")]
    public void RejectsDangerousInput(string input) =>
        Assert.False(VirtualPath.TryResolve(VirtualPath.Root, input, out _));

    [Fact]
    public void RejectsOverlongInputAndSegments()
    {
        Assert.False(VirtualPath.TryResolve(VirtualPath.Root, new string('a', VirtualPath.MaxInputLength + 1), out _));
        Assert.False(VirtualPath.TryResolve(VirtualPath.Root, new string('a', VirtualPath.MaxSegmentLength + 1), out _));
        Assert.False(VirtualPath.TryResolve(VirtualPath.Root, string.Concat(Enumerable.Repeat("d/", VirtualPath.MaxDepth + 1)), out _));
    }

    [Fact]
    public void ResolutionIsLinearForPathologicalInput()
    {
        var input = string.Concat(Enumerable.Repeat("//../", VirtualPath.MaxInputLength / 5));
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(VirtualPath.TryResolve(VirtualPath.Root, input, out var result));
            Assert.True(result.IsRoot);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"1000 resolutions took {watch.Elapsed}");
    }

    [Fact]
    public void PhysicalPathStaysInsideHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "jail-" + Guid.NewGuid().ToString("N"));
        var escaped = Resolve("/", "../../../../etc/passwd").ToPhysical(home);
        Assert.StartsWith(Path.GetFullPath(home) + Path.DirectorySeparatorChar, escaped, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(home), VirtualPath.Root.ToPhysical(home));
        Assert.False(Directory.Exists(home)); // resolution never creates or touches the directory
    }

    [Fact]
    public void ValidNameRules()
    {
        Assert.True(VirtualPath.IsValidName("report.pdf"));
        Assert.False(VirtualPath.IsValidName(".."));
        Assert.False(VirtualPath.IsValidName("a/b"));
        Assert.False(VirtualPath.IsValidName("aux"));
    }
}
