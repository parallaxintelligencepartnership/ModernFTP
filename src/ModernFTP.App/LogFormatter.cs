using System.Globalization;
using ModernFTP.Engine;

namespace ModernFTP.App;

public readonly record struct LogSegment(string Text, LogTag Tag);

/// <summary>Turns one engine event into colored log segments. Pure, so it is unit testable.</summary>
public static class LogFormatter
{
    private static readonly HashSet<string> DirectoryCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "CWD", "XCWD", "MKD", "XMKD", "RMD", "XRMD", "LIST", "NLST", "MLSD",
    };

    private static readonly HashSet<string> FileCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "RETR", "STOR", "APPE", "DELE", "RNFR", "RNTO", "SIZE", "MDTM", "MLST", "STAT",
    };

    public static IReadOnlyList<LogSegment> Format(ServerEvent e)
    {
        var segments = new List<LogSegment>
        {
            new(e.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), LogTag.Time),
            new("  ", LogTag.Separator),
            new("#" + e.SessionId.ToString(CultureInfo.InvariantCulture), LogTag.SessionId),
            new("  ", LogTag.Separator),
            new(e.UserName ?? "-", LogTag.UserName),
            new("  ", LogTag.Separator),
            new(e.RemoteEndPoint?.Address.ToString() ?? "-", LogTag.Ip),
            new("  ", LogTag.Separator),
        };

        switch (e)
        {
            case CommandReceivedEvent c:
                segments.Add(new LogSegment(c.Command, LogTag.Text));
                if (c.Argument.Length > 0)
                {
                    segments.Add(new LogSegment(" ", LogTag.Text));
                    var tag = DirectoryCommands.Contains(c.Command) ? LogTag.Directory
                        : FileCommands.Contains(c.Command) ? LogTag.File
                        : LogTag.Text;
                    segments.Add(new LogSegment(c.Argument, tag));
                }

                break;
            case TransferStartedEvent t:
                segments.Add(new LogSegment(t.Direction + " ", LogTag.Text));
                AddPath(segments, t.Path);
                segments.Add(new LogSegment($" on data port {t.DataPort}{(t.Secure ? " (TLS)" : string.Empty)}", LogTag.Text));
                break;
            case TransferCompletedEvent t:
                segments.Add(new LogSegment(t.Direction + " ", LogTag.Text));
                AddPath(segments, t.Path);
                segments.Add(new LogSegment(
                    $" {(t.Success ? "completed" : "failed")}, {t.Bytes} bytes in {t.Duration.TotalMilliseconds:0} ms{(t.Error is null ? string.Empty : " (" + t.Error + ")")}",
                    LogTag.Text));
                break;
            default:
                segments.Add(new LogSegment(e.Kind + ": " + e.Describe(), LogTag.Text));
                break;
        }

        return segments;
    }

    private static void AddPath(List<LogSegment> segments, string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash >= 0)
        {
            segments.Add(new LogSegment(path[..(slash + 1)], LogTag.Directory));
        }

        segments.Add(new LogSegment(path[(slash + 1)..], LogTag.File));
    }
}
