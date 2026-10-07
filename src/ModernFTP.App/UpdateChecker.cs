using System.Net.Http;
using System.Text.Json;

namespace ModernFTP.App;

public enum UpdateState
{
    UpToDate,
    Available,
    Failed,
}

public sealed record UpdateResult(UpdateState State, string Version, string Url);

/// <summary>
/// Looks up the latest GitHub release. Only called when the user clicks "Check for updates";
/// the app never makes this call on its own.
/// </summary>
public static class UpdateChecker
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/parallaxintelligencepartnership/ModernFTP/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/parallaxintelligencepartnership/ModernFTP/releases";

    /// <summary>Reads tag_name and html_url from the release JSON and compares the tag with the current version.</summary>
    public static UpdateResult Evaluate(string releaseJson, string currentVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(releaseJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            {
                return new UpdateResult(UpdateState.Failed, string.Empty, ReleasesPageUrl);
            }

            var tag = StripV(tagElement.GetString()!);
            var url = root.TryGetProperty("html_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()!
                : ReleasesPageUrl;
            if (!TryParseVersion(tag, out var latest) || !TryParseVersion(currentVersion, out var current))
            {
                return new UpdateResult(UpdateState.Failed, tag, url);
            }

            return new UpdateResult(latest > current ? UpdateState.Available : UpdateState.UpToDate, tag, url);
        }
        catch (JsonException)
        {
            return new UpdateResult(UpdateState.Failed, string.Empty, ReleasesPageUrl);
        }
    }

    public static string StripV(string tag) =>
        tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V') ? tag[1..] : tag;

    public static async Task<UpdateResult> CheckAsync(string currentVersion, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        var owned = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("ModernFTP-update-check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Evaluate(json, currentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UpdateResult(UpdateState.Failed, string.Empty, ReleasesPageUrl);
        }
        finally
        {
            if (owned)
            {
                client.Dispose();
            }
        }
    }

    private static bool TryParseVersion(string text, out Version version)
    {
        var core = StripV(text).Split('-', '+')[0];
        var ok = Version.TryParse(core, out var parsed);
        version = parsed ?? new Version(0, 0);
        return ok;
    }
}
