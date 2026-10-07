using ModernFTP.Engine;

namespace ModernFTP.Config;

/// <summary>Applies a saved config to a running server.</summary>
public static class ConfigApply
{
    /// <summary>
    /// Converts <paramref name="config"/> the way the server was started and applies it with
    /// <see cref="FtpServer.ApplyOptions"/>. Returns the names of changed settings that need a restart;
    /// empty when every change is live. Throws <see cref="InvalidDataException"/> when the config is invalid.
    /// </summary>
    public static IReadOnlyList<string> ApplyConfig(this FtpServer server, ModernFtpConfig config, string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(server);
        var next = ConfigLoader.ToServerOptions(config, configDirectory);
        try
        {
            return server.ApplyOptions(next);
        }
        finally
        {
            // The running server keeps its own certificate; a certificate change needs a restart.
            if (!ReferenceEquals(next.Certificate, server.Options.Certificate))
            {
                next.Certificate?.Dispose();
            }
        }
    }
}
