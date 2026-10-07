namespace ModernFTP.Engine.Tests;

/// <summary>A fact that runs only on the named platforms and is skipped, with the reason, elsewhere.</summary>
public sealed class PlatformFactAttribute : FactAttribute
{
    public PlatformFactAttribute(bool windows, bool linux, bool macOS, string reason)
    {
        var runs = (windows && OperatingSystem.IsWindows())
            || (linux && OperatingSystem.IsLinux())
            || (macOS && OperatingSystem.IsMacOS());
        if (!runs)
        {
            Skip = reason;
        }
    }
}
