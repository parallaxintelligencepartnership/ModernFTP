using System.Text;

namespace ModernFTP.Host.Service;

/// <summary>
/// Appends log lines to a text file beside the config, one file per day
/// (modernftp-service-yyyyMMdd.log), and deletes files older than the retention window.
/// </summary>
internal sealed class RollingFileLog : IDisposable
{
    public const int RetentionDays = 14;
    private const string Prefix = "modernftp-service-";

    private readonly string _directory;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private string? _currentName;

    public RollingFileLog(string directory, Func<DateTime>? clock = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTime.Now);
    }

    public static string FileNameFor(DateTime day) => $"{Prefix}{day:yyyyMMdd}.log";

    public void WriteLine(string line)
    {
        try
        {
            lock (_gate)
            {
                var now = _clock();
                var name = FileNameFor(now);
                if (name != _currentName)
                {
                    Directory.CreateDirectory(_directory);
                    _currentName = name;
                    Prune(now);
                }

                File.AppendAllText(Path.Combine(_directory, name), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk or a locked file must never take the FTP service down.
        }
    }

    public void Dispose()
    {
    }

    private void Prune(DateTime now)
    {
        var cutoff = FileNameFor(now.AddDays(-RetentionDays));
        foreach (var file in Directory.EnumerateFiles(_directory, Prefix + "*.log"))
        {
            if (string.CompareOrdinal(Path.GetFileName(file), cutoff) < 0)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Try again tomorrow.
                }
            }
        }
    }
}
