using System.Windows.Media;

namespace ModernFTP.App;

public enum LogTag
{
    Separator,
    Text,
    SessionId,
    UserName,
    Ip,
    Time,
    File,
    Directory,
}

/// <summary>Default log colors, hard coded here until they move into config. One palette per theme.</summary>
public static class LogPalette
{
    private static readonly Dictionary<LogTag, Brush> Light = Build(
        ("6B7280", LogTag.Separator), ("1F2937", LogTag.Text), ("7C3AED", LogTag.SessionId), ("0369A1", LogTag.UserName),
        ("B45309", LogTag.Ip), ("15803D", LogTag.Time), ("BE185D", LogTag.File), ("0F766E", LogTag.Directory));

    private static readonly Dictionary<LogTag, Brush> Dark = Build(
        ("9CA3AF", LogTag.Separator), ("E5E7EB", LogTag.Text), ("C4B5FD", LogTag.SessionId), ("7DD3FC", LogTag.UserName),
        ("FCD34D", LogTag.Ip), ("86EFAC", LogTag.Time), ("F9A8D4", LogTag.File), ("5EEAD4", LogTag.Directory));

    public static Brush Get(LogTag tag, bool dark) => (dark ? Dark : Light)[tag];

    private static Dictionary<LogTag, Brush> Build(params (string Hex, LogTag Tag)[] entries)
    {
        var map = new Dictionary<LogTag, Brush>();
        foreach (var (hex, tag) in entries)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + hex));
            brush.Freeze();
            map[tag] = brush;
        }

        return map;
    }
}
