using System.Diagnostics;

namespace ModernFTP.Conformance;

internal sealed record CurlResult(int ExitCode, string StdOut, string StdErr);

internal static class Curl
{
    private static readonly Lazy<string?> Located = new(Locate);

    public static string? Path => Located.Value;

    public static async Task<CurlResult> RunAsync(params string[] arguments)
    {
        var path = Path ?? throw new InvalidOperationException("curl is not on the PATH.");
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // -q ignores any ~/.curlrc so results do not depend on the machine.
        info.ArgumentList.Add("-q");
        info.ArgumentList.Add("--silent");
        info.ArgumentList.Add("--show-error");
        info.ArgumentList.Add("--max-time");
        info.ArgumentList.Add("30");
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start curl.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return new CurlResult(process.ExitCode, await stdout, await stderr);
    }

    private static string? Locate()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "curl.exe" } : ["curl"];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(System.IO.Path.PathSeparator))
        {
            foreach (var name in names)
            {
                var candidate = System.IO.Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

/// <summary>A fact that is skipped, with a clear message, when curl is not installed.</summary>
public sealed class CurlFactAttribute : FactAttribute
{
    public CurlFactAttribute()
    {
        if (Curl.Path is null)
        {
            Skip = "curl was not found on the PATH; the conformance suite drives the server with curl.";
        }
    }
}
